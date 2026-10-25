using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// Modrinth modpacks (Pulsar 1.0.9): the pack and every file it lists are downloaded and checked before the server
/// changes, only from the hosts the format allows and only to plain paths outside Pulsar's folder; the previous pack's
/// files go, the user's stay.
/// </summary>
public sealed class ModpackInstallerTests : IDisposable
{
    private const string PackUrl = "https://cdn.modrinth.com/data/pack/versions/v1/pack-1.0.mrpack";
    private const string Lithium = "https://cdn.modrinth.com/data/gvQqBUqZ/versions/x/lithium.jar";
    private const string Config = "https://raw.githubusercontent.com/someone/pack/main/config/lithium.properties";
    private const string ClientOnly = "https://cdn.modrinth.com/data/AANobbMI/versions/y/sodium.jar";

    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-modpack-").FullName;
    private readonly Files _http = new();
    private readonly byte[] _lithium = Encoding.UTF8.GetBytes("lithium");
    private readonly byte[] _config = Encoding.UTF8.GetBytes("mixin.ai=false");

    public ModpackInstallerTests()
    {
        _http.Routes[Lithium] = _lithium;
        _http.Routes[Config] = _config;
        _http.Routes[ClientOnly] = Encoding.UTF8.GetBytes("sodium");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task APackPutsItsServerFilesAndOverridesOnTheServer()
    {
        var pack = Pack(Index(
            File("mods/lithium.jar", Lithium, _lithium),
            File("config/lithium.properties", Config, _config),
            File("mods/sodium.jar", ClientOnly, Encoding.UTF8.GetBytes("sodium"), server: "unsupported")),
            ("overrides/config/a.toml", "from overrides"), ("overrides/server.properties", "motd=pack"), ("server-overrides/config/a.toml", "for servers"));

        var written = await InstallAsync(pack);

        Assert.Equal(_lithium, ReadBytes("mods/lithium.jar"));
        Assert.Equal(_config, ReadBytes("config/lithium.properties"));
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "mods", "sodium.jar"))); // only for players' games
        Assert.Equal("for servers", Read("config/a.toml")); // server-overrides over overrides
        Assert.Equal("motd=pack", Read("server.properties"));
        Assert.Equal(4, written);
        Assert.DoesNotContain(ClientOnly, _http.Requested);
    }

    [Fact]
    public async Task ANewPackTakesTheOldPacksFilesAwayAndLeavesTheUsers()
    {
        await InstallAsync(Pack(Index(File("mods/lithium.jar", Lithium, _lithium), File("config/lithium.properties", Config, _config))));
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        System.IO.File.WriteAllText(Path.Combine(_root, "mods", "mine.jar"), "the user's");

        var ferrite = Encoding.UTF8.GetBytes("ferrite");
        _http.Routes["https://cdn.modrinth.com/data/uXXizFIs/versions/z/ferrite.jar"] = ferrite;
        await InstallAsync(Pack(Index(File("mods/ferrite.jar", "https://cdn.modrinth.com/data/uXXizFIs/versions/z/ferrite.jar", ferrite))));

        Assert.False(System.IO.File.Exists(Path.Combine(_root, "mods", "lithium.jar")));
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "config", "lithium.properties")));
        Assert.Equal(ferrite, ReadBytes("mods/ferrite.jar"));
        Assert.Equal("the user's", Read("mods/mine.jar"));
        Assert.Contains("mods/ferrite.jar", Read(ModpackInstaller.RecordPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.21.7", "fabric-loader", "0.17.2", null)]
    [InlineData("1.21.8", "forge", "58.1.0", null)]
    [InlineData("1.21.8", "fabric-loader", "0.17.2", "quilt-loader")]
    [InlineData("1.21.8", "fabric-loader", "not a version!", null)]
    public async Task APackIsForTheGameVersionAndLoaderThePanelChoseOnly(string minecraft, string loaderKey, string loaderVersion, string? other)
    {
        var index = Index(File("mods/lithium.jar", Lithium, _lithium));
        index["dependencies"] = new JsonObject { ["minecraft"] = minecraft, [loaderKey] = loaderVersion };
        if (other is not null)
        {
            index["dependencies"]![other] = "0.29.0";
        }

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(Pack(index)));

        Assert.Equal("modpack.mismatch", error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("../server.jar")]
    [InlineData("mods/../../x.jar")]
    [InlineData(".pulsar/steamcmd/steamcmd.exe")]
    [InlineData("PULSAR~1/x")]
    [InlineData("C:/Windows/x.dll")]
    public async Task FilesGoOnlyToPlainPathsInTheServer(string path)
    {
        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(Pack(Index(File(path, Lithium, _lithium)))));
        Assert.Equal("modpack.unsafe_path", error.Code);

        error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(Pack(Index(), ($"overrides/{path}", "x"))));
        Assert.Equal("modpack.unsafe_path", error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.Equal([PackUrl, PackUrl], _http.Requested); // refused before any of its files is fetched
    }

    [Fact]
    public async Task FilesComeOnlyFromTheHostsTheFormatAllows()
    {
        const string Elsewhere = "https://example.com/lithium.jar";
        _http.Routes[Elsewhere] = _lithium;

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(Pack(Index(File("mods/lithium.jar", Elsewhere, _lithium)))));

        Assert.Equal("modpack.untrusted_host", error.Code);
        Assert.DoesNotContain(Elsewhere, _http.Requested);
    }

    [Fact]
    public async Task AFileOrPackThatIsNotWhatTheIndexSaysChangesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        System.IO.File.WriteAllText(Path.Combine(_root, "mods", "mine.jar"), "the user's");
        _http.Routes[Config] = Encoding.UTF8.GetBytes("tampered");

        var error = await Assert.ThrowsAsync<GameProcessException>(() =>
            InstallAsync(Pack(Index(File("mods/lithium.jar", Lithium, _lithium), File("config/lithium.properties", Config, _config)))));
        Assert.Equal("modpack.checksum_mismatch", error.Code);

        var pack = Pack(Index(File("mods/lithium.jar", Lithium, _lithium)));
        error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(pack, Encoding.UTF8.GetBytes("another pack")));
        Assert.Equal("modpack.checksum_mismatch", error.Code);

        Assert.Equal(["mine.jar"], Directory.EnumerateFiles(Path.Combine(_root, "mods")).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(_root, ".pulsar")));
    }

    [Fact]
    public async Task APacksNameIsShownAsTextOnly()
    {
        var index = Index();
        index["name"] = "Pack\r\n\u001b[2m[Pulsar] Done\u202e" + new string('x', 200);
        var pack = Pack(index);
        _http.Routes[PackUrl] = pack;

        using var prepared = await new ModpackInstaller(new DirectServerFiles(), _http).PrepareAsync(
            new ModDownload("pack.mrpack", PackUrl, Convert.ToHexStringLower(SHA512.HashData(pack)), pack.Length), "fabric", "1.21.8", _ => null,
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.StartsWith("Pack[2m[Pulsar] Donexxx", prepared.Name, StringComparison.Ordinal);
        Assert.Equal(64, prepared.Name!.Length);
        Assert.DoesNotContain(prepared.Name, char.IsControl);
    }

    [Fact]
    public async Task APackWithoutAnIndexOfMinecraftsIsNotOne()
    {
        var notAPack = Pack(null, ("overrides/server.properties", "motd=x"));
        Assert.Equal("modpack.invalid", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(notAPack))).Code);

        var index = Index();
        index["game"] = "terraria";
        Assert.Equal("modpack.invalid", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(Pack(index)))).Code);
    }

    /// <summary>Prepares and applies the pack as the agent does (the install steps in between are not this class's).</summary>
    private async Task<int> InstallAsync(byte[] pack, byte[]? hashedAs = null)
    {
        _http.Routes[PackUrl] = pack;
        var installer = new ModpackInstaller(new DirectServerFiles(), _http) { StallTimeout = TimeSpan.FromSeconds(5) };
        using var prepared = await installer.PrepareAsync(
            new ModDownload("pack.mrpack", PackUrl, Convert.ToHexStringLower(SHA512.HashData(hashedAs ?? pack)), pack.Length), "fabric", "1.21.8",
            version => version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? null : "not a version",
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal("0.17.2", prepared.LoaderVersion);
        return installer.Apply(_root, prepared);
    }

    private static JsonObject Index(params JsonObject[] files) => new()
    {
        ["formatVersion"] = 1,
        ["game"] = "minecraft",
        ["versionId"] = "1.0",
        ["name"] = "Test pack",
        ["files"] = new JsonArray([.. files]),
        ["dependencies"] = new JsonObject { ["minecraft"] = "1.21.8", ["fabric-loader"] = "0.17.2" },
    };

    private static JsonObject File(string path, string url, byte[] content, string server = "required") => new()
    {
        ["path"] = path,
        ["hashes"] = new JsonObject { ["sha1"] = new string('0', 40), ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(content)) },
        ["env"] = new JsonObject { ["client"] = "required", ["server"] = server },
        ["downloads"] = new JsonArray(url),
        ["fileSize"] = content.Length,
    };

    private static byte[] Pack(JsonObject? index, params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (index is not null)
            {
                using var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open());
                writer.Write(index.ToJsonString());
            }

            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private string Read(string path) => System.IO.File.ReadAllText(Path.Combine(_root, path));

    private byte[] ReadBytes(string path) => System.IO.File.ReadAllBytes(Path.Combine(_root, path));

    private sealed class Files : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Routes { get; } = [];

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.OriginalString;
            Requested.Add(url);
            return Task.FromResult(Routes.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
