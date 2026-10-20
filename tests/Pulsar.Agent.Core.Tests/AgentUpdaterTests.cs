using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Updates;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// An update the panel asks for (Phase 19): the agent takes the release from GitHub alone, signed by a trusted key, the
/// one the panel checked, newer than itself, with the ZIP its manifest describes; anything else installs nothing.
/// </summary>
public sealed class AgentUpdaterTests : IDisposable
{
    private const string Version = "1.0.1";
    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-updater-tests-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakePlatform _platform = new();
    private readonly List<UpdateReport> _reports = [];
    private readonly TaskCompletionSource<UpdateReport> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task AVerifiedReleaseIsUnpackedAndItsInstallerStarted()
    {
        var release = Release();
        using var updater = Updater(release.Handler);

        var ack = updater.Start(Guid.NewGuid(), new UpdatePulsarCommand(Version, release.ManifestSha256));

        Assert.Equal(AckStatus.Accepted, ack.Status);
        Assert.Equal(UpdateStatuses.Installing, (await _ended.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status);
        var (script, folder) = Assert.Single(_platform.Started);
        Assert.Equal(Path.Combine(folder, "Install-Pulsar.ps1"), script);
        Assert.StartsWith(Path.Combine(_root, "Updates") + Path.DirectorySeparatorChar, folder, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(folder, "panel", "Pulsar.Api.dll")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(folder)!, "*.zip"));
        Assert.Equal([UpdateStatuses.Downloading, UpdateStatuses.Installing], _reports.Select(r => r.Status));
        // The redirect to GitHub's release storage was followed, nowhere else was asked.
        Assert.All(release.Handler.Requested, uri => Assert.True(UpdateRelease.IsAllowedDownload(uri), uri.ToString()));
        Assert.Contains(release.Handler.Requested, uri => uri.Host == "release-assets.githubusercontent.com");

        // A second request while it runs is refused.
        Assert.Equal("update.in_progress", updater.Start(Guid.NewGuid(), new UpdatePulsarCommand(Version, release.ManifestSha256)).ErrorCode);
    }

    [Fact]
    public void WhatTheCommandAsksIsCheckedBeforeAnythingIsDownloaded()
    {
        var release = Release();
        using var updater = Updater(release.Handler);
        var sha = release.ManifestSha256;

        Assert.Equal("update.invalid", updater.Start(Guid.NewGuid(), new UpdatePulsarCommand("1.0", sha)).ErrorCode);
        Assert.Equal("update.invalid", updater.Start(Guid.NewGuid(), new UpdatePulsarCommand(Version, sha.ToUpperInvariant())).ErrorCode);
        // Not newer than this agent (its version: 1.0.0), however well signed.
        Assert.Equal("update.not_newer", updater.Start(Guid.NewGuid(), new UpdatePulsarCommand(AgentInfo.Version, sha)).ErrorCode);
        Assert.Equal("update.not_newer", updater.Start(Guid.NewGuid(), new UpdatePulsarCommand("0.9.0", sha)).ErrorCode);
        _platform.Cannot = ("update.msi_managed", "The agent MSI maintains this agent.");
        Assert.Equal("update.msi_managed", updater.Start(Guid.NewGuid(), new UpdatePulsarCommand(Version, sha)).ErrorCode);
        Assert.DoesNotContain(AgentCapabilities.UpdatePulsar, updater.Capabilities);
        Assert.Empty(release.Handler.Requested);
    }

    [Theory]
    [InlineData("other release", "is not the one the panel checked")]
    [InlineData("unknown key", "update.unknown_key")]
    [InlineData("bad zip", "does not have the SHA-256")]
    [InlineData("elsewhere", "updates come from GitHub only")]
    [InlineData("escaping zip", "outside its folder")]
    [InlineData("too new", "update to 1.0.1 first")]
    [InlineData("other version", "is not a Pulsar release of that version")]
    public async Task AnythingElseFailsAndInstallsNothing(string tampering, string why)
    {
        var release = tampering switch
        {
            "unknown key" => Release(signer: ECDsa.Create(ECCurve.NamedCurves.nistP256)),
            "bad zip" => Release(zipSha: new string('0', 64)),
            "elsewhere" => Release(storage: "https://example.com"),
            "escaping zip" => Release(entries: [@"..\evil.ps1", "Install-Pulsar.ps1", "release.json"]),
            "too new" => Release(minimum: "1.0.1"),
            "other version" => Release(zipVersion: "1.0.2"),
            _ => Release(),
        };
        using var updater = Updater(release.Handler);
        var sha = tampering == "other release" ? new string('a', 64) : release.ManifestSha256;
        if (tampering == "too new")
        {
            // Released as 1.0.2, from 1.0.1 on: this 1.0.0 agent must take 1.0.1 first.
            release = Release(version: "1.0.2", minimum: "1.0.1");
            using var newer = Updater(release.Handler);
            Assert.Equal(AckStatus.Accepted, newer.Start(Guid.NewGuid(), new UpdatePulsarCommand("1.0.2", release.ManifestSha256)).Status);
        }
        else
        {
            Assert.Equal(AckStatus.Accepted, updater.Start(Guid.NewGuid(), new UpdatePulsarCommand(Version, sha)).Status);
        }

        var ended = await _ended.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStatuses.Failed, ended.Status);
        Assert.Contains(why, ended.Error, StringComparison.Ordinal);
        Assert.Empty(_platform.Started);
        Assert.False(File.Exists(Path.Combine(_root, "evil.ps1")));
        // Its folder is gone, and the next request is taken.
        Assert.Empty(Directory.Exists(Path.Combine(_root, "Updates")) ? Directory.GetDirectories(Path.Combine(_root, "Updates")) : []);
    }

    [Fact]
    public async Task AnInstallerThatEndsWithoutUpdatingIsAFailureWithItsExitCode()
    {
        var release = Release();
        _platform.ExitCode = 3;
        using var updater = Updater(release.Handler);

        Assert.Equal(AckStatus.Accepted, updater.Start(Guid.NewGuid(), new UpdatePulsarCommand(Version, release.ManifestSha256)).Status);

        var failed = await FailuresAsync(1);
        Assert.Contains("exit code 3 before updating anything", failed.Error, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_root, "Setup", "logs"), failed.Error, StringComparison.Ordinal);

        // Another update may be asked at once (it ends the same way here).
        Assert.Equal(AckStatus.Accepted, updater.Start(Guid.NewGuid(), new UpdatePulsarCommand(Version, release.ManifestSha256)).Status);
        await FailuresAsync(2);
    }

    /// <summary>The first failure reported, once <paramref name="count"/> were.</summary>
    private async Task<UpdateReport> FailuresAsync(int count)
    {
        for (var waited = 0; ; waited += 50)
        {
            lock (_reports)
            {
                if (_reports.Count(r => r.Status == UpdateStatuses.Failed) >= count)
                {
                    return _reports.First(r => r.Status == UpdateStatuses.Failed);
                }
            }

            Assert.True(waited < 30_000, "No failure was reported.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void EarlierReleasesAreRemovedButNotRecentOnesNorTheInstallers()
    {
        using var updater = Updater(Release().Handler);
        var root = Directory.CreateDirectory(updater.UpdatesRoot).FullName;
        var old = Directory.CreateDirectory(Path.Combine(root, "1.0.0-0011aabb"));
        File.WriteAllText(Path.Combine(old.FullName, "Install-Pulsar.ps1"), "old");
        old.CreationTimeUtc = DateTime.UtcNow.AddHours(-2);
        var recent = Directory.CreateDirectory(Path.Combine(root, "1.0.1-ccddeeff"));
        var backups = Directory.CreateDirectory(Path.Combine(root, "backups"));
        backups.CreationTimeUtc = DateTime.UtcNow.AddDays(-2);
        var other = Directory.CreateDirectory(Path.Combine(root, "1.0.0-NOTHEX12"));
        other.CreationTimeUtc = DateTime.UtcNow.AddDays(-2);

        updater.RemoveEarlierReleases();

        Assert.False(old.Exists && Directory.Exists(old.FullName));
        Assert.True(Directory.Exists(recent.FullName));
        Assert.True(Directory.Exists(backups.FullName));
        Assert.True(Directory.Exists(other.FullName));
    }

    [Fact]
    public void TheInstallersResultIsReadAsItWroteIt()
    {
        using var updater = Updater(Release().Handler);
        Directory.CreateDirectory(updater.UpdatesRoot);
        File.WriteAllText(updater.ResultFile, """
            {"from":"1.0.0","to":"1.0.1","parts":["agent","panel","other"],"status":"rolled_back","error":"The panel did not start.","log":"C:\\x.log","finishedAt":"2026-10-21T10:00:00Z"}
            """);

        var result = updater.ReadResult();

        Assert.Equal((UpdateStatuses.RolledBack, "1.0.0", "1.0.1", "The panel did not start."), (result!.Status, result.From, result.To, result.Error));
        Assert.Equal(["agent", "panel"], result.Parts!);
        File.WriteAllText(updater.ResultFile, """{"status":"downloading","finishedAt":"2026-10-21T10:00:00Z"}""");
        Assert.Null(updater.ReadResult());
        File.WriteAllText(updater.ResultFile, "not json");
        Assert.Null(updater.ReadResult());
        File.WriteAllText(updater.ResultFile, new string(' ', 20_000));
        Assert.Null(updater.ReadResult());
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private AgentUpdater Updater(HttpMessageHandler handler) => new(_platform, new PanelChannel(), new AgentPaths(Path.Combine(_root, "Agent")),
        TimeProvider.System, NullLogger<AgentUpdater>.Instance, new AgentUpdaterOptions
        {
            Verifier = new UpdateVerifier([Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())]),
            Handler = () => handler,
            OnReport = report =>
            {
                lock (_reports)
                {
                    _reports.Add(report);
                }

                if (report.Status is UpdateStatuses.Installing or UpdateStatuses.Failed)
                {
                    _ended.TrySetResult(report);
                }

                return Task.CompletedTask;
            },
        });

    /// <summary>A release on a fake GitHub: its files redirect to the release storage, as GitHub's do.</summary>
    private FakeRelease Release(string version = Version, string minimum = "1.0.0", ECDsa? signer = null, string? zipSha = null,
        string storage = "https://release-assets.githubusercontent.com", string[]? entries = null, string? zipVersion = null)
    {
        Assert.True(UpdateVersion.TryParse(version, out var parsed));
        var zip = Zip(entries ?? ["Install-Pulsar.ps1", "release.json", @"panel\Pulsar.Api.dll", @"agent\PulsarAgent.dll"], zipVersion ?? version);
        var zipName = UpdateRelease.ZipName(parsed);
        var manifest = ProtocolJson.Serialize(new UpdateManifest("pulsar", version, minimum, DateTimeOffset.UtcNow, "Notes.",
            [new UpdateFile(zipName, zip.Length, zipSha ?? Convert.ToHexStringLower(SHA256.HashData(zip)))]));
        var key = signer ?? _key;
        var signature = ProtocolJson.Serialize(new UpdateSignature(ProtocolCrypto.Fingerprint(key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ProtocolCrypto.Sign(key, ProtocolCrypto.SignedData(UpdateRelease.SignaturePurpose, manifest)))));
        var files = new Dictionary<string, byte[]>
        {
            [UpdateRelease.ManifestName] = manifest,
            [UpdateRelease.SignatureName] = signature,
            [zipName] = zip,
        };
        return new FakeRelease(new FakeGitHub(version, files, storage), Convert.ToHexStringLower(SHA256.HashData(manifest)));
    }

    private static byte[] Zip(string[] entries, string version)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name.EndsWith("release.json", StringComparison.Ordinal) ? $$"""{"version":"{{version}}"}""" : "content");
            }
        }

        return stream.ToArray();
    }

    private sealed record FakeRelease(FakeGitHub Handler, string ManifestSha256);

    private sealed class FakeGitHub(string version, Dictionary<string, byte[]> files, string storage) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requested)
            {
                Requested.Add(uri);
            }

            var prefix = $"/Ayrton09/Pulsar/releases/download/v{version}/";
            if (uri.Host == "github.com" && uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri($"{storage}/github-production-release-asset/{uri.AbsolutePath[prefix.Length..]}?sig=x");
                return Task.FromResult(redirect);
            }

            var name = uri.AbsolutePath.Split('/')[^1];
            return Task.FromResult(uri.Host == "release-assets.githubusercontent.com" && files.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found", Encoding.UTF8) });
        }
    }

    private sealed class FakePlatform : IUpdatePlatform
    {
        public (string Code, string Message)? Cannot { get; set; }

        public List<(string Script, string Folder)> Started { get; } = [];

        /// <summary>The installer's exit code when it ends while the agent runs; null: it stops the agent (never ends here).</summary>
        public int? ExitCode { get; set; }

        public (string Code, string Message)? CannotUpdate() => Cannot;

        public bool HostsPanel() => false;

        public string PrepareFolder(string updatesRoot, string name)
        {
            var folder = Path.Combine(updatesRoot, name);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public Task<int> StartInstaller(string script, string workingDirectory)
        {
            Started.Add((script, workingDirectory));
            return ExitCode is { } code ? Task.FromResult(code) : new TaskCompletionSource<int>().Task;
        }
    }
}
