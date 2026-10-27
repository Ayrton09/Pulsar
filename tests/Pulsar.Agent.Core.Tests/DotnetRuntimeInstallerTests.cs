using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The .NET runtime of a server's folder (Pulsar 1.0.12) against a fake of Microsoft's metadata and downloads.</summary>
public sealed class DotnetRuntimeInstallerTests : IDisposable
{
    private const string Metadata = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json";

    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-dotnet-").FullName;
    private readonly FakeHttp _http = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task TheChannelsLatestRuntimeGoesInTheDotnetFolder()
    {
        Publish("9.0.21", Runtime("9.0.21"));

        var version = await InstallAsync();

        Assert.Equal("9.0.21", version);
        Assert.Equal("muxer", File.ReadAllText(Path.Combine(_root, "dotnet", "dotnet.exe")));
        Assert.True(File.Exists(Path.Combine(_root, "dotnet", "shared", "Microsoft.NETCore.App", "9.0.21", "System.Private.CoreLib.dll")));
        Assert.StartsWith("9.0.21\n", File.ReadAllText(Path.Combine(_root, "dotnet", "pulsar-dotnet.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInstalledPatchIsNotDownloadedAgainAndANewOneReplacesIt()
    {
        Publish("9.0.21", Runtime("9.0.21"));
        await InstallAsync();
        _http.Requested.Clear();

        await InstallAsync();
        Assert.DoesNotContain(_http.Requested, r => r.EndsWith(".zip", StringComparison.Ordinal));

        Publish("9.0.22", Runtime("9.0.22"));
        Assert.Equal("9.0.22", await InstallAsync());
        Assert.True(File.Exists(Path.Combine(_root, "dotnet", "shared", "Microsoft.NETCore.App", "9.0.22", "System.Private.CoreLib.dll")));
        Assert.False(Directory.Exists(Path.Combine(_root, "dotnet", "shared", "Microsoft.NETCore.App", "9.0.21"))); // emptied, so gone
        Assert.Equal("muxer", File.ReadAllText(Path.Combine(_root, "dotnet", "dotnet.exe")));
    }

    [Fact]
    public async Task AnUpdateCheckComparesTheInstalledPatchWithTheLatest()
    {
        Publish("9.0.21", Runtime("9.0.21"));
        Assert.Equal(new GameBuild(null, ".NET 9.0.21", true), await Installer().CheckAsync(_root, "9.0", CancellationToken.None));

        await InstallAsync();
        Assert.Equal(new GameBuild(".NET 9.0.21", ".NET 9.0.21", false), await Installer().CheckAsync(_root, "9.0", CancellationToken.None));

        Publish("9.0.22", Runtime("9.0.22"));
        Assert.Equal(new GameBuild(".NET 9.0.21", ".NET 9.0.22", true), await Installer().CheckAsync(_root, "9.0", CancellationToken.None));
    }

    [Fact]
    public async Task ADownloadThatIsNotWhatMicrosoftListsIsRefused()
    {
        var zip = Runtime("9.0.21");
        Publish("9.0.21", zip, hash: Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes("something else"))));

        var failure = await Assert.ThrowsAsync<GameProcessException>(InstallAsync);

        Assert.StartsWith("dotnet.", failure.Code, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "dotnet", "dotnet.exe")));
    }

    [Theory]
    [InlineData("https://example.com/dotnet-runtime-9.0.21-win-x64.zip")]
    [InlineData("http://builds.dotnet.microsoft.com/dotnet/Runtime/9.0.21/dotnet-runtime-9.0.21-win-x64.zip")]
    public async Task ARuntimeFromAnotherHostIsNotDownloaded(string url)
    {
        Publish("9.0.21", Runtime("9.0.21"), url: url);

        var failure = await Assert.ThrowsAsync<GameProcessException>(InstallAsync);

        Assert.StartsWith("dotnet.", failure.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(url, _http.Requested);
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("sub/../../outside.dll")]
    [InlineData("pulsar-dotnet.txt")]
    [InlineData("C:/outside.dll")]
    public async Task AnArchiveThatReachesOutOfItsFolderIsRefused(string entry)
    {
        Publish("9.0.21", Zip(("dotnet.exe", "muxer"), (entry, "x")));

        var failure = await Assert.ThrowsAsync<GameProcessException>(InstallAsync);

        Assert.Equal("dotnet.bad_archive", failure.Code);
        Assert.False(File.Exists(Path.Combine(_root, "dotnet", "dotnet.exe")));
    }

    [Theory]
    [InlineData("9.0.21; del")]
    [InlineData("10.0.1")]
    public async Task AVersionOfAnotherShapeOrChannelIsRefused(string version)
    {
        Publish(version, Runtime("9.0.21"));

        var failure = await Assert.ThrowsAsync<GameProcessException>(InstallAsync);

        Assert.Equal("dotnet.no_runtime", failure.Code);
    }

    private DotnetRuntimeInstaller Installer() => new(new DirectServerFiles(), _http);

    private Task<string> InstallAsync() => Installer().InstallAsync(_root, "9.0", (_, _) => Task.CompletedTask, CancellationToken.None);

    /// <summary>Microsoft's metadata naming <paramref name="version"/> the latest, with its Windows x64 zip.</summary>
    private void Publish(string version, byte[] zip, string? hash = null, string? url = null)
    {
        var download = url ?? $"https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/dotnet-runtime-{version}-win-x64.zip";
        var metadata = $$"""
            {
              "channel-version": "9.0",
              "latest-runtime": {{JsonSerializer.Serialize(version)}},
              "releases": [
                { "release-version": {{JsonSerializer.Serialize(version)}}, "runtime": { "version": {{JsonSerializer.Serialize(version)}}, "files": [
                  { "name": "dotnet-runtime-linux-x64.tar.gz", "rid": "linux-x64", "url": "https://builds.dotnet.microsoft.com/x.tar.gz", "hash": "{{new string('0', 128)}}" },
                  { "name": "dotnet-runtime-win-x64.zip", "rid": "win-x64", "url": {{JsonSerializer.Serialize(download)}}, "hash": "{{hash ?? Convert.ToHexString(SHA512.HashData(zip))}}" }
                ] } }
              ]
            }
            """;
        // Microsoft's file starts with a byte order mark.
        _http.Routes[Metadata] = () => Ok([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(metadata)]);
        _http.Routes[download] = () => Ok(zip);
    }

    private static byte[] Runtime(string version) =>
        Zip(("dotnet.exe", "muxer"), ("LICENSE.txt", "MIT"), ($"host/fxr/{version}/hostfxr.dll", "fxr"),
            ($"shared/Microsoft.NETCore.App/{version}/System.Private.CoreLib.dll", "corelib"));

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var entry = new StreamWriter(zip.CreateEntry(path).Open());
                entry.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private sealed class FakeHttp : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.OriginalString;
            Requested.Add(url);
            return Task.FromResult(Routes.TryGetValue(url, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
