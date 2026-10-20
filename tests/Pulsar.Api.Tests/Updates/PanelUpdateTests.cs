using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Updates;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Api.Updates;
using Pulsar.Application.Access;
using Pulsar.Application.Updates;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Updates;

/// <summary>
/// One-click updates (Phase 19) through the panel and real agent cores: the panel offers only a release signed by a
/// trusted key, updates its own machine first, and only for an administrator who gives their password; the node's agent
/// takes the release from "GitHub" itself and reports how its installer ended.
/// </summary>
public sealed class PanelUpdateTests(PanelUpdateTests.UpdatesFactory factory) : IClassFixture<PanelUpdateTests.UpdatesFactory>
{
    private const string Next = "1.0.1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ACheckOffersOnlyASignedNewerRelease()
    {
        using var admin = await factory.CreateAdminClientAsync();
        factory.Releases.Publish(Next, signer: ECDsa.Create(ECCurve.NamedCurves.nistP256));
        // Signed by another key: refused, and why is shown (what another test of this class found stays offered).
        var refused = await CheckAsync(admin);
        Assert.Contains("update.unknown_key", refused.CheckError, StringComparison.Ordinal);

        factory.Releases.Publish(Next);
        var view = await CheckAsync(admin);
        Assert.Equal(("1.0.0", Next, "What changed."), (view.PanelVersion, view.Available?.Version, view.Available?.Notes));
        Assert.True(view.Available!.NewerThanPanel);
        Assert.Null(view.CheckError);

        // Unreachable later: what was found stays offered, with why the look failed.
        factory.Releases.Offline = true;
        view = await CheckAsync(admin);
        Assert.Equal(Next, view.Available?.Version);
        Assert.Contains("never ask", view.CheckError, StringComparison.Ordinal);
        factory.Releases.Offline = false;

        // The daily look can be turned off (audited).
        using (var off = await admin.PutAsync("/api/v1/updates/settings", new UpdateSettingsRequest(false)))
        {
            Assert.False((await ReadAsync<UpdatesView>(off)).CheckAutomatically);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog
            .AnyAsync(e => e.Action == AuditActions.Updates.SettingsChanged, Ct));
        using var on = await admin.PutAsync("/api/v1/updates/settings", new UpdateSettingsRequest(true));
        on.EnsureSuccessStatusCode();
    }

    [DatabaseFact]
    public async Task ThePanelsNodeIsUpdatedFirstWithThePasswordAndReportsHowItEnded()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var host = await UpdatingAgentAsync(admin, "updates-host", hostsPanel: true);
        await using var other = await UpdatingAgentAsync(admin, "updates-other", hostsPanel: false);
        factory.Releases.Publish(Next);
        var view = await CheckAsync(admin);
        var hostView = view.Nodes.Single(n => n.NodeId == host.Agent.Config!.NodeId);
        var otherView = view.Nodes.Single(n => n.NodeId == other.Agent.Config!.NodeId);
        Assert.Equal((true, true, true, null), (hostView.HostsPanel, hostView.CanUpdate, hostView.UpdateAvailable, hostView.Blocked));
        // A node newer than its panel would be refused by it: the panel's own machine first.
        Assert.Equal("update.panel_first", otherView.Blocked);

        // A wrong password starts nothing (and counts, as every wrong password does).
        using (var wrong = await admin.PostAsync("/api/v1/updates/apply", new ApplyUpdateRequest([hostView.NodeId], "not the password", null)))
        {
            await SetupTests.AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.wrong_current_password");
        }

        Assert.Empty(host.Platform.Started);
        using var applied = await admin.PostAsync("/api/v1/updates/apply",
            new ApplyUpdateRequest([hostView.NodeId, otherView.NodeId], PulsarApiFactory.AdminPassword, null));
        var results = await ReadAsync<List<UpdateRequestResult>>(applied);
        Assert.True(results.Single(r => r.NodeId == hostView.NodeId).Sent);
        Assert.Equal("update.panel_first", results.Single(r => r.NodeId == otherView.NodeId).ErrorCode);

        // The agent downloaded and checked the release itself, started its installer, which ended well.
        await InProcessAgent.EventuallyAsync(async () => (await NodeStatusAsync(admin, hostView.NodeId))?.Status == "succeeded", "the update's end");
        var (script, folder) = Assert.Single(host.Platform.Started);
        Assert.Equal(Path.Combine(folder, "Install-Pulsar.ps1"), script);
        Assert.Contains(factory.Releases.Requested, uri => uri.AbsolutePath.EndsWith(UpdateRelease.ZipName(new UpdateVersion(1, 0, 1)), StringComparison.Ordinal));
        Assert.Empty(other.Platform.Started);

        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.Action == AuditActions.Updates.Requested || e.Action == AuditActions.Updates.Finished).ToListAsync(Ct);
        var node = hostView.NodeId.ToString();
        Assert.Contains(audit, e => e.Action == AuditActions.Updates.Requested && e.Outcome == AuditOutcome.Success && e.TargetId == node);
        Assert.Contains(audit, e => e.Action == AuditActions.Updates.Finished && e.Outcome == AuditOutcome.Success && e.TargetId == node);
        // The wrong password is audited on the administrator's account, as every wrong password is.
        Assert.Contains(audit, e => e.Action == AuditActions.Updates.Requested && e.Outcome == AuditOutcome.Failure && e.TargetType == "user");
    }

    [DatabaseFact]
    public async Task OnlyAnAdministratorsSessionUpdates()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var viewer = await scenario.SignedInWithAsync("updates-viewer", Permissions.Node.View);
        using (var denied = await viewer.GetAsync("/api/v1/updates"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var denied = await viewer.PostAsync("/api/v1/updates/apply", new ApplyUpdateRequest([Guid.NewGuid()], AccessScenario.Password, null)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // An API key, even one with settings.manage, never updates: the session cookie and the password are needed.
        using var manager = await scenario.SignedInWithAsync("updates-manager", Permissions.Settings.Manage);
        using var created = await manager.PostAsync("/api/v1/account/api-keys", new CreateApiKeyRequest("updates", null, null));
        await AccessScenario.EnsureSuccessAsync(created);
        var key = (await created.Content.ReadFromJsonAsync<CreatedApiKey>(AccessScenario.Json, Ct))!;
        using var script = factory.CreateDefaultClient(new Uri("https://localhost"));
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Value);
        using (var ok = await script.GetAsync(new Uri("/api/v1/updates", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var refused = await script.PostAsJsonAsync(new Uri("/api/v1/updates/apply", UriKind.Relative),
            new ApplyUpdateRequest([Guid.NewGuid()], AccessScenario.Password, null), AccessScenario.Json, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    private static async Task<UpdatesView> CheckAsync(PanelClient admin)
    {
        using var response = await admin.PostAsync("/api/v1/updates/check");
        return await ReadAsync<UpdatesView>(response);
    }

    private static async Task<NodeUpdateStatusView?> NodeStatusAsync(PanelClient admin, Guid node)
    {
        using var response = await admin.GetAsync("/api/v1/updates");
        return (await ReadAsync<UpdatesView>(response)).Nodes.Single(n => n.NodeId == node).Last;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(AccessScenario.Json, Ct))!;
    }

    /// <summary>An enrolled, connected agent whose updater takes releases from the fake GitHub and "installs" them.</summary>
    private async Task<UpdatingAgent> UpdatingAgentAsync(PanelClient admin, string name, bool hostsPanel)
    {
        var created = await NodeApi.CreateAsync(admin, name);
        var agent = new InProcessAgent(factory);
        var platform = new FakePlatform(hostsPanel);
        var root = Directory.CreateTempSubdirectory("pulsar-update-tests-").FullName;
        agent.UpdaterFactory = (channel, paths) => new AgentUpdater(platform, channel, paths, TimeProvider.System, NullLogger<AgentUpdater>.Instance,
            new AgentUpdaterOptions
            {
                Verifier = factory.Verifier,
                Handler = () => factory.Releases,
                ResultPoll = TimeSpan.FromMilliseconds(200),
                UpdatesRoot = root,
            });
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return new UpdatingAgent(agent, platform, root);
    }

    private sealed record UpdatingAgent(InProcessAgent Agent, FakePlatform Platform, string Root) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Agent.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>The machine, as far as an update needs it: the "installer" writes its result as the real one does.</summary>
    private sealed class FakePlatform(bool hostsPanel) : IUpdatePlatform
    {
        public List<(string Script, string Folder)> Started { get; } = [];

        public (string Code, string Message)? CannotUpdate() => null;

        public bool HostsPanel() => hostsPanel;

        public string PrepareFolder(string updatesRoot, string name) => Directory.CreateDirectory(Path.Combine(updatesRoot, name)).FullName;

        public Task<int> StartInstaller(string script, string workingDirectory)
        {
            Started.Add((script, workingDirectory));
            var updates = Path.GetDirectoryName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar))!;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(updates)!, "last-update.json"),
                $$"""{"from":"{{AgentInfo.Version}}","to":"{{Next}}","parts":["agent","panel"],"status":"succeeded","error":null,"finishedAt":"{{DateTimeOffset.UtcNow:O}}"}""");

            // The real one stops the agent: it never ends while the agent runs.
            return new TaskCompletionSource<int>().Task;
        }
    }

    /// <summary>The panel with a test update key, and "GitHub" as a handler both it and the agents use.</summary>
    public sealed class UpdatesFactory : PulsarApiFactory
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public UpdatesFactory()
        {
            Releases = new FakeReleases(_key);
            Verifier = new UpdateVerifier([Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())]);
        }

        public FakeReleases Releases { get; }

        public UpdateVerifier Verifier { get; }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _key.Dispose();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Verifier);
                services.AddScoped<IUpdateSource>(_ => new Source(Releases));
            });
        }

        private sealed class Source(FakeReleases releases) : IUpdateSource
        {
            public async Task<(byte[] Manifest, byte[] Signature)> GetLatestAsync(CancellationToken cancellationToken)
            {
                if (releases.Offline)
                {
                    throw new UpdateDownloadException("The tests never ask GitHub.");
                }

                using var client = new HttpClient(releases, disposeHandler: false);
                return (await UpdateDownloads.GetBytesAsync(client, UpdateRelease.LatestManifest, UpdateRelease.MaxManifestBytes, cancellationToken),
                    await UpdateDownloads.GetBytesAsync(client, UpdateRelease.LatestSignature, UpdateRelease.MaxSignatureBytes, cancellationToken));
            }
        }
    }

    /// <summary>GitHub's releases: files redirect to the release storage, the latest release's too.</summary>
    public sealed class FakeReleases(ECDsa key) : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private string _version = "0.0.0";

        public bool Offline { get; set; }

        public List<Uri> Requested { get; } = [];

        public void Publish(string version, ECDsa? signer = null)
        {
            Assert.True(UpdateVersion.TryParse(version, out var parsed));
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in new[] { "Install-Pulsar.ps1", "release.json", @"agent\PulsarAgent.dll" })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(name == "release.json" ? $$"""{"version":"{{version}}"}""" : "content");
                }
            }

            var bytes = stream.ToArray();
            var manifest = ProtocolJson.Serialize(new UpdateManifest("pulsar", version, "1.0.0", DateTimeOffset.UtcNow, "What changed.",
                [new UpdateFile(UpdateRelease.ZipName(parsed), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]));
            var by = signer ?? key;
            lock (_files)
            {
                _version = version;
                _files.Clear();
                _files[UpdateRelease.ZipName(parsed)] = bytes;
                _files[UpdateRelease.ManifestName] = manifest;
                _files[UpdateRelease.SignatureName] = ProtocolJson.Serialize(new UpdateSignature(ProtocolCrypto.Fingerprint(by.ExportSubjectPublicKeyInfo()),
                    Convert.ToBase64String(ProtocolCrypto.Sign(by, ProtocolCrypto.SignedData(UpdateRelease.SignaturePurpose, manifest)))));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (_files)
            {
                Requested.Add(uri);
                var name = uri.AbsolutePath.Split('/')[^1];
                if (uri.Host == "github.com"
                    && (uri.AbsolutePath.StartsWith("/Ayrton09/Pulsar/releases/latest/download/", StringComparison.Ordinal)
                        || uri.AbsolutePath.StartsWith($"/Ayrton09/Pulsar/releases/download/v{_version}/", StringComparison.Ordinal)))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri($"https://release-assets.githubusercontent.com/github-production-release-asset/{name}?sig=x");
                    return Task.FromResult(redirect);
                }

                return Task.FromResult(uri.Host == "release-assets.githubusercontent.com" && _files.TryGetValue(name, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found", Encoding.UTF8) });
            }
        }
    }
}
