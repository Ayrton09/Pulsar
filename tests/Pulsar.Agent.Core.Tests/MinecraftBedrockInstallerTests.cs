using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Tests;

/// <summary>Minecraft: Bedrock Edition's server from Mojang (1.0.8), accepted only signed by Microsoft.</summary>
public sealed class MinecraftBedrockInstallerTests : IDisposable
{
    private const string Links = "https://net-secondary.web.minecraft-services.net/api/v1.0/download/links";
    private const string Latest = "https://www.minecraft.net/bedrockdedicatedserver/bin-win/bedrock-server-1.26.52.3.zip";

    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-bedrock-").FullName;
    private readonly FakeHttp _http = new();
    private readonly FakeSignatures _signatures = new();
    private readonly List<string> _stages = [];

    public MinecraftBedrockInstallerTests()
    {
        _http.Routes[Links] = () => Ok(LinksTo(Latest));
        _http.Routes[Latest] = () => Ok(Archive(("bedrock_server.exe", "server 1.26.52.3"), ("server.properties", "server-name=Dedicated Server\n"),
            ("allowlist.json", "[]"), ("behavior_packs/vanilla/manifest.json", "{}"), ("config/default/", null)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task InstallsTheLatestReleaseOnceItsServerIsSignedByMicrosoft()
    {
        await InstallAsync("latest");

        Assert.Equal("server 1.26.52.3", File.ReadAllText(Path.Combine(_root, "bedrock_server.exe")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_root, "behavior_packs", "vanilla", "manifest.json")));
        Assert.Equal("1.26.52.3", File.ReadAllText(Path.Combine(_root, ".pulsar", "bedrock-version.txt")));
        // Checked on the copy that is then put in the server's folder, and as Microsoft's.
        var (path, organization) = Assert.Single(_signatures.Checked);
        Assert.Equal("Microsoft Corporation", organization);
        Assert.False(File.Exists(path)); // the checked copy is gone
        Assert.Equal(["preparing", "downloading", "verifying", "installing"], _stages.Distinct());
    }

    [Fact]
    public async Task AServerNotSignedByMicrosoftIsNotInstalled()
    {
        _signatures.Signed = false;

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("latest"));

        Assert.Equal("bedrock.untrusted", error.Code);
        Assert.False(File.Exists(Path.Combine(_root, "bedrock_server.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "server.properties")));
    }

    [Fact]
    public async Task AnUpdateKeepsTheServersSettingsAndTheSameVersionIsNotDownloadedAgain()
    {
        await InstallAsync("latest");
        File.WriteAllText(Path.Combine(_root, "server.properties"), "server-name=Ours\n");
        File.WriteAllText(Path.Combine(_root, "allowlist.json"), "[{\"name\":\"Steve\"}]");
        _http.Requested.Clear();

        await InstallAsync("latest");
        Assert.DoesNotContain(Latest, _http.Requested); // the same version: nothing downloaded

        const string next = "https://www.minecraft.net/bedrockdedicatedserver/bin-win/bedrock-server-1.26.60.1.zip";
        _http.Routes[Links] = () => Ok(LinksTo(next));
        _http.Routes[next] = () => Ok(Archive(("bedrock_server.exe", "server 1.26.60.1"), ("server.properties", "server-name=Dedicated Server\n"),
            ("allowlist.json", "[]")));
        await InstallAsync("latest");

        Assert.Equal("server 1.26.60.1", File.ReadAllText(Path.Combine(_root, "bedrock_server.exe")));
        Assert.Equal("server-name=Ours\n", File.ReadAllText(Path.Combine(_root, "server.properties")));
        Assert.Equal("[{\"name\":\"Steve\"}]", File.ReadAllText(Path.Combine(_root, "allowlist.json")));
        Assert.Equal("1.26.60.1", File.ReadAllText(Path.Combine(_root, ".pulsar", "bedrock-version.txt")));
    }

    [Fact]
    public async Task AChosenVersionIsTakenFromMojangsAddressForIt()
    {
        const string chosen = "https://www.minecraft.net/bedrockdedicatedserver/bin-win/bedrock-server-1.21.50.07.zip";
        _http.Routes[chosen] = () => Ok(Archive(("bedrock_server.exe", "server 1.21.50.07")));

        await InstallAsync("1.21.50.07");

        Assert.DoesNotContain(Links, _http.Requested);
        Assert.Equal("server 1.21.50.07", File.ReadAllText(Path.Combine(_root, "bedrock_server.exe")));
        Assert.Equal("bedrock.unknown_version", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("1.21; x"))).Code);
    }

    [Theory]
    [InlineData("https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.26.52.3.zip")]
    [InlineData("https://example.com/bedrockdedicatedserver/bin-win/bedrock-server-1.26.52.3.zip")]
    [InlineData("http://www.minecraft.net/bedrockdedicatedserver/bin-win/bedrock-server-1.26.52.3.zip")]
    [InlineData("https://www.minecraft.net/bedrockdedicatedserver/bin-win/bedrock-server-1.26.52.3.zip?x=1")]
    public async Task OnlyMojangsWindowsDownloadsAreTakenFromItsList(string link)
    {
        _http.Routes[Links] = () => Ok(LinksTo(link));

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("latest"));

        Assert.Equal("bedrock.unexpected_link", error.Code);
        Assert.DoesNotContain(link, _http.Requested);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".pulsar/bedrock-version.txt")]
    [InlineData("PULSAR~1/steamcmd/steamcmd.exe")]
    [InlineData("Bedrock_Server.exe")] // a second executable Windows would write over the checked one
    [InlineData("C:/Windows/evil.dll")]
    public async Task AnArchiveWithAnEntryOutsideTheServersFilesIsRefusedWhole(string entry)
    {
        _http.Routes[Latest] = () => Ok(Archive(("bedrock_server.exe", "server"), (entry, "planted")));

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("latest"));

        Assert.Equal("bedrock.bad_archive", error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task AnArchiveWithoutTheServerIsRefused()
    {
        _http.Routes[Latest] = () => Ok(Archive(("server.properties", "x")));

        Assert.Equal("bedrock.bad_archive", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync("latest"))).Code);
    }

    private Task InstallAsync(string version) =>
        new MinecraftBedrockInstaller(new DirectServerFiles(), _http, _signatures) { StallTimeout = TimeSpan.FromSeconds(5) }.InstallAsync(_root, version,
            (stage, _) =>
            {
                _stages.Add(stage);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

    private static byte[] LinksTo(string windows) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        result = new
        {
            links = new[]
            {
                new { downloadType = "serverBedrockLinux", downloadUrl = "https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.26.52.3.zip" },
                new { downloadType = "serverBedrockWindows", downloadUrl = windows },
            },
        },
    });

    private static byte[] Archive(params (string Path, string? Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = zip.CreateEntry(path);
                if (content is not null)
                {
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(content);
                }
            }
        }

        return buffer.ToArray();
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private sealed class FakeSignatures : ICodeSignatures
    {
        public bool Signed { get; set; } = true;

        public List<(string Path, string Organization)> Checked { get; } = [];

        public bool IsSignedBy(string path, string organization)
        {
            Checked.Add((path, organization));
            return Signed && File.Exists(path);
        }
    }

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
