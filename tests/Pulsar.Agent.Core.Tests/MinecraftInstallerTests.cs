using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Tests;

public sealed class MinecraftInstallerTests : IDisposable
{
    private const string Manifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string Details = "https://piston-meta.mojang.com/v1/packages/abc/26.3.json";
    private const string Jar = "https://piston-data.mojang.com/v1/objects/def/server.jar";
    private const string Assets = "https://api.adoptium.net/v3/assets/latest/25/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse";
    private const string Release = "https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25/jre.zip";
    private const string Asset = "https://release-assets.githubusercontent.com/jre.zip";

    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-minecraft-").FullName;
    private readonly FakeHttp _http = new();
    private readonly byte[] _jar = Encoding.UTF8.GetBytes("the server jar");
    private readonly List<string> _stages = [];

    public MinecraftInstallerTests()
    {
        Serve("jdk-25.0.4.1+1", Runtime("jdk-25.0.4.1+1-jre", ("bin/java.exe", "java"), ("lib/server/jvm.dll", "jvm"), ("release", "JAVA_VERSION=25")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task InstallsTheServerAndTheJavaItsVersionNeeds()
    {
        await InstallAsync("latest");

        Assert.Equal(_jar, File.ReadAllBytes(Path.Combine(_root, "server.jar")));
        Assert.Equal("java", File.ReadAllText(Path.Combine(_root, "java", "bin", "java.exe")));
        Assert.Equal("jvm", File.ReadAllText(Path.Combine(_root, "java", "lib", "server", "jvm.dll")));
        Assert.False(Directory.Exists(Path.Combine(_root, "java", "jdk-25.0.4.1+1-jre"))); // the archive's top folder is dropped
        Assert.Contains("downloading", _stages);
        Assert.Contains(Asset, _http.Requested); // followed github.com's redirect to its asset host
    }

    [Fact]
    public async Task TheSameRuntimeIsNotDownloadedAgainAndANewOneReplacesItsFiles()
    {
        await InstallAsync("26.3");
        _http.Requested.Clear();

        await InstallAsync("26.3");
        Assert.DoesNotContain(Asset, _http.Requested);
        Assert.Contains(Jar, _http.Requested);

        Serve("jdk-25.0.5+2", Runtime("jdk-25.0.5+2-jre", ("bin/java.exe", "java 2"), ("lib/server/jvm2.dll", "jvm 2")));
        await InstallAsync("26.3");
        Assert.Equal("java 2", File.ReadAllText(Path.Combine(_root, "java", "bin", "java.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "java", "lib", "server", "jvm.dll"))); // the previous runtime's file
        Assert.False(File.Exists(Path.Combine(_root, "java", "release")));
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchItsChecksumIsRefused()
    {
        _http.Routes[Jar] = () => Ok(Encoding.UTF8.GetBytes("tampered"));

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("latest"));

        Assert.Equal("minecraft.checksum_mismatch", error.Code);
        Assert.False(File.Exists(Path.Combine(_root, "server.jar")));
    }

    [Fact]
    public async Task VersionDetailsWithoutAChecksumAreRefused()
    {
        // The details name the server's download and its checksum: unverified, they could name anything.
        _http.Routes[Manifest] = () => Ok(Json(new
        {
            latest = new { release = "26.3", snapshot = "26.4-snapshot-2" },
            versions = new[] { new { id = "26.3", type = "release", url = Details } },
        }));

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("latest"));

        Assert.Equal("minecraft.no_checksum", error.Code);
        Assert.DoesNotContain(Details, _http.Requested);
        Assert.False(File.Exists(Path.Combine(_root, "server.jar")));
    }

    [Fact]
    public async Task RedirectsLeaveThePublishersHostsOnlyOverHttpsToKnownHosts()
    {
        _http.Routes[Release] = () => Redirect("https://evil.example/jre.zip");

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("latest"));

        Assert.Equal("minecraft.untrusted_host", error.Code);
        Assert.DoesNotContain("https://evil.example/jre.zip", _http.Requested);
    }

    [Fact]
    public async Task AnOversizedRuntimeRecordIsNotTrusted()
    {
        // The server's account can write java/pulsar-java.txt: the agent must not read a file of any size from it.
        await InstallAsync("26.3");
        var marker = Path.Combine(_root, "java", "pulsar-java.txt");
        await File.WriteAllTextAsync(marker, "jdk-25.0.4.1+1\n" + new string('x', 2 * 1024 * 1024) + "\n", TestContext.Current.CancellationToken);
        _http.Requested.Clear();

        await InstallAsync("26.3");

        Assert.Contains(Asset, _http.Requested); // installed again, as with no record
        Assert.StartsWith("jdk-25.0.4.1+1\n", await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(new FileInfo(marker).Length < 1024);
    }

    [Fact]
    public async Task AnUnknownVersionIsReported()
    {
        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("1.0-nope"));

        Assert.Equal("minecraft.unknown_version", error.Code);
    }

    [Fact]
    public async Task AnArchiveCannotWriteOutsideTheRuntimeFolder()
    {
        Serve("jdk-evil", Runtime("jdk-evil", ("bin/java.exe", "java"), ("../../server.jar", "overwritten")));

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("latest"));

        Assert.Equal("minecraft.bad_archive", error.Code);
        Assert.False(File.Exists(Path.Combine(_root, "java", "bin", "java.exe")));
    }

    private Task InstallAsync(string version) =>
        new MinecraftInstaller(new DirectServerFiles(), _http) { StallTimeout = TimeSpan.FromSeconds(5) }.InstallAsync(_root, version, (stage, _) =>
        {
            _stages.Add(stage);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

    /// <summary>Mojang's metadata for 26.3 (Java 25) and Adoptium's runtime <paramref name="release"/>.</summary>
    private void Serve(string release, byte[] runtime)
    {
        var details = Json(new
        {
            downloads = new { server = new { url = Jar, sha1 = Sha1(_jar), size = _jar.Length } },
            javaVersion = new { component = "java-runtime-epsilon", majorVersion = 25 },
        });
        _http.Routes[Manifest] = () => Ok(Json(new
        {
            latest = new { release = "26.3", snapshot = "26.4-snapshot-2" },
            versions = new[] { new { id = "26.3", type = "release", url = Details, sha1 = Sha1(details) } },
        }));
        _http.Routes[Details] = () => Ok(details);
        _http.Routes[Jar] = () => Ok(_jar);
        _http.Routes[Assets] = () => Ok(Json(new[]
        {
            new { release_name = release, binary = new { package = new { link = Release, checksum = Hex(SHA256.HashData(runtime)), size = runtime.Length } } },
        }));
        _http.Routes[Release] = () => Redirect(Asset);
        _http.Routes[Asset] = () => Ok(runtime);
    }

    private static byte[] Runtime(string top, params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            _ = zip.CreateEntry($"{top}/");
            foreach (var (path, content) in files)
            {
                using var entry = new StreamWriter(zip.CreateEntry($"{top}/{path}").Open());
                entry.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);

    /// <summary>Mojang lists SHA-1 checksums.</summary>
    private static string Sha1(byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(data);
        return Hex(hash.GetHashAndReset());
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

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
