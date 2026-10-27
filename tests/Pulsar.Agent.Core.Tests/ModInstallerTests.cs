using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// A mod catalog's files put into a server (Pulsar 1.0.9): downloaded from Modrinth's CDN only, checked against their
/// SHA-512 before anything changes, written and deleted only in the game's mod folders.
/// </summary>
public sealed class ModInstallerTests : IDisposable
{
    private const string Lithium = "https://cdn.modrinth.com/data/gvQqBUqZ/versions/qxIL7Kb8/lithium-fabric-0.18.1.jar";
    private const string FabricApi = "https://cdn.modrinth.com/data/P7dR8mSH/versions/abc/fabric-api-0.130.0.jar";

    private const string Kits = "https://umod.org/plugins/Kits.cs";

    private static readonly Dictionary<string, IReadOnlyList<string>> s_folders = new(StringComparer.Ordinal)
    {
        ["mods"] = [".jar"],
        ["plugins"] = [".jar"],
        ["oxide/plugins"] = [".cs"],
    };

    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-mods-").FullName;
    private readonly Files _http = new();
    private readonly byte[] _lithium = Encoding.UTF8.GetBytes("lithium");
    private readonly byte[] _fabricApi = Encoding.UTF8.GetBytes("fabric api");
    private readonly byte[] _kits = Encoding.UTF8.GetBytes("namespace Oxide.Plugins { }");

    public ModInstallerTests()
    {
        _http.Routes[Lithium] = _lithium;
        _http.Routes[FabricApi] = _fabricApi;
        _http.Routes[Kits] = _kits;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task FilesAreCheckedThenWrittenAndOthersDeleted()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "mods", "lithium-fabric-0.17.0.jar"), "old");

        await ChangeAsync([Download("mods/lithium-fabric-0.18.1.jar", Lithium, _lithium), Download("mods/fabric-api-0.130.0.jar", FabricApi, _fabricApi)],
            ["mods/lithium-fabric-0.17.0.jar", "mods/never-there.jar"]);

        Assert.Equal(_lithium, File.ReadAllBytes(Path.Combine(_root, "mods", "lithium-fabric-0.18.1.jar")));
        Assert.Equal(_fabricApi, File.ReadAllBytes(Path.Combine(_root, "mods", "fabric-api-0.130.0.jar")));
        Assert.False(File.Exists(Path.Combine(_root, "mods", "lithium-fabric-0.17.0.jar")));
    }

    [Fact]
    public async Task AFileThatIsNotTheCatalogsChangesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "mods", "old.jar"), "old");
        _http.Routes[FabricApi] = Encoding.UTF8.GetBytes("tampered");

        var error = await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync(
            [Download("mods/lithium-fabric-0.18.1.jar", Lithium, _lithium), Download("mods/fabric-api-0.130.0.jar", FabricApi, _fabricApi)], ["mods/old.jar"]));

        Assert.Equal("mods.checksum_mismatch", error.Code);
        Assert.True(File.Exists(Path.Combine(_root, "mods", "old.jar"))); // nothing deleted
        Assert.False(File.Exists(Path.Combine(_root, "mods", "lithium-fabric-0.18.1.jar"))); // nor written
    }

    [Fact]
    public async Task CurseForgesFilesAreCheckedAgainstTheSha1ItPublishes()
    {
        const string Jei = "https://edge.forgecdn.net/files/6000/1/jei-1.21.8-fabric.jar";
        var jei = Encoding.UTF8.GetBytes("jei");
        _http.Routes[Jei] = jei;
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(jei);
        var hash = Convert.ToHexStringLower(sha1.GetHashAndReset());

        await ChangeAsync([new ModDownload("mods/jei.jar", Jei, hash, jei.Length)], []);
        Assert.Equal(jei, File.ReadAllBytes(Path.Combine(_root, "mods", "jei.jar")));

        _http.Routes[Jei] = Encoding.UTF8.GetBytes("tampered");
        var error = await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync([new ModDownload("mods/jei2.jar", Jei, hash, jei.Length)], []));
        Assert.Equal("mods.checksum_mismatch", error.Code);
    }

    [Fact]
    public async Task AFileSomeoneElsePutThereStaysTheirs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "mods", "lithium-fabric-0.18.1.jar"), "uploaded by hand");

        var error = await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync([Download("mods/lithium-fabric-0.18.1.jar", Lithium, _lithium)], []));

        Assert.Equal("mods.file_exists", error.Code);
        Assert.Equal("uploaded by hand", File.ReadAllText(Path.Combine(_root, "mods", "lithium-fabric-0.18.1.jar")));
        Assert.Empty(_http.Requested); // refused before anything is downloaded

        // One the panel put there, and removes in the same change, is replaced.
        await ChangeAsync([Download("mods/lithium-fabric-0.18.1.jar", Lithium, _lithium)], ["mods/lithium-fabric-0.18.1.jar"]);
        Assert.Equal(_lithium, File.ReadAllBytes(Path.Combine(_root, "mods", "lithium-fabric-0.18.1.jar")));
    }

    [Theory]
    [InlineData("server.jar")]
    [InlineData("mods/../server.jar")]
    [InlineData("config/x.jar")]
    [InlineData("mods/x.exe")]
    [InlineData(".pulsar/x.jar")]
    public async Task FilesGoOnlyIntoTheGamesModFolders(string path)
    {
        var error = await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync([Download(path, Lithium, _lithium)], []));
        Assert.Equal("mods.invalid_path", error.Code);

        error = await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync([], [path]));
        Assert.Equal("mods.invalid_path", error.Code);
        Assert.Empty(_http.Requested);
    }

    [Fact]
    public async Task OnlyTheCatalogsHostServesFiles()
    {
        const string Elsewhere = "https://example.com/lithium.jar";
        _http.Routes[Elsewhere] = _lithium;

        var error = await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync([Download("mods/lithium.jar", Elsewhere, _lithium)], []));
        Assert.StartsWith("mods.", error.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(Elsewhere, _http.Requested);

        error = await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync([Download("mods/lithium.jar", "http://cdn.modrinth.com/x.jar", _lithium)], []));
        Assert.StartsWith("mods.", error.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangeHasALimitAndEachFileAChecksum()
    {
        var tooMany = Enumerable.Range(0, ChangeModsCommand.MaxFiles + 1).Select(i => $"mods/m{i}.jar").ToList();
        Assert.Equal("mods.invalid", (await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync([], tooMany))).Code);
        Assert.Equal("mods.invalid", (await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync([], []))).Code);
        Assert.Equal("mods.invalid", (await Assert.ThrowsAsync<GameProcessException>(() =>
            ChangeAsync([new ModDownload("mods/lithium.jar", Lithium, "not-a-checksum", 7)], []))).Code);
    }

    [Fact]
    public async Task AUmodPluginComesFromUmodCheckedAgainstItsSha1IntoItsAddonsFolder()
    {
        // uMod publishes a plugin's SHA-1 only (Pulsar 1.0.14).
        var kits = new ModDownload("oxide/plugins/Kits.cs", Kits, Sha1(_kits), 0);

        await ChangeAsync([kits], []);

        Assert.Equal(_kits, File.ReadAllBytes(Path.Combine(_root, "oxide", "plugins", "Kits.cs")));
        Assert.All(_http.UserAgents, a => Assert.StartsWith("Pulsar-Agent/", a, StringComparison.Ordinal)); // uMod answers one without it once a minute
        Assert.Equal("mods.invalid_path", (await Assert.ThrowsAsync<GameProcessException>(() =>
            ChangeAsync([kits with { Path = "mods/Kits.cs" }], []))).Code); // not a file mods/ takes
        Assert.Equal("mods.invalid_path", (await Assert.ThrowsAsync<GameProcessException>(() =>
            ChangeAsync([kits with { Path = "oxide/Kits.cs" }], []))).Code);
        Assert.Equal("mods.untrusted_host", (await Assert.ThrowsAsync<GameProcessException>(() =>
            ChangeAsync([kits with { Url = "https://umod.org/plugins/search.json" }], ["oxide/plugins/Kits.cs"]))).Code); // only the plugin's own file
        _http.Routes[Kits] = Encoding.UTF8.GetBytes("tampered");
        Assert.Equal("mods.checksum_mismatch", (await Assert.ThrowsAsync<GameProcessException>(() =>
            ChangeAsync([kits], ["oxide/plugins/Kits.cs"]))).Code);
        Assert.Equal(_kits, File.ReadAllBytes(Path.Combine(_root, "oxide", "plugins", "Kits.cs")));
    }

    private const string Jotunn = "https://thunderstore.io/package/download/ValheimModding/Jotunn/2.30.2/";
    private const string JotunnPath = "BepInEx/plugins/ValheimModding-Jotunn";

    [Fact]
    public async Task APackageIsUnpackedIntoBepInExRecordedAndRemovedWithoutItsConfiguration()
    {
        // Thunderstore's layout: plugins, patchers and configuration by folder, a leading BepInEx/ the same, anything else beside the plugins.
        _http.Routes[Jotunn] = Package(("manifest.json", "{}"), ("plugins/Jotunn.dll", "jotunn"), ("plugins/Translations/en.json", "en"),
            ("BepInEx/patchers/JotunnPatch.dll", "patch"), ("config/com.jotunn.cfg", "theirs"), ("config/kept.cfg", "theirs"));
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "config"));
        File.WriteAllText(Path.Combine(_root, "BepInEx", "config", "kept.cfg"), "mine");

        await ChangeAsync([PackageDownload(JotunnPath, Jotunn)], []);

        var plugins = Path.Combine(_root, "BepInEx", "plugins", "ValheimModding-Jotunn");
        Assert.Equal("jotunn", File.ReadAllText(Path.Combine(plugins, "Jotunn.dll")));
        Assert.Equal("en", File.ReadAllText(Path.Combine(plugins, "Translations", "en.json")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(plugins, "manifest.json")));
        Assert.Equal("patch", File.ReadAllText(Path.Combine(_root, "BepInEx", "patchers", "ValheimModding-Jotunn", "JotunnPatch.dll")));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(_root, "BepInEx", "config", "com.jotunn.cfg")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_root, "BepInEx", "config", "kept.cfg"))); // configuration only where there is none
        Assert.True(File.Exists(Path.Combine(_root, ".pulsar", "packages", "ValheimModding-Jotunn.json")));

        // A newer version in its place: what the old one wrote and the new one does not have goes.
        _http.Routes[Jotunn] = Package(("plugins/Jotunn.dll", "jotunn 2"));
        await ChangeAsync([PackageDownload(JotunnPath, Jotunn)], [JotunnPath]);
        Assert.Equal("jotunn 2", File.ReadAllText(Path.Combine(plugins, "Jotunn.dll")));
        Assert.False(File.Exists(Path.Combine(plugins, "Translations", "en.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "BepInEx", "patchers", "ValheimModding-Jotunn")));

        await ChangeAsync([], [JotunnPath]);
        Assert.False(Directory.Exists(plugins));
        Assert.True(File.Exists(Path.Combine(_root, "BepInEx", "config", "com.jotunn.cfg"))); // its settings stay the server's
        Assert.False(File.Exists(Path.Combine(_root, ".pulsar", "packages", "ValheimModding-Jotunn.json")));
    }

    [Fact]
    public async Task APackageTakesNobodysFileAndWritesOnlyIntoBepInExsFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins", "ValheimModding-Jotunn"));
        File.WriteAllText(Path.Combine(_root, "BepInEx", "plugins", "ValheimModding-Jotunn", "Jotunn.dll"), "uploaded");
        _http.Routes[Jotunn] = Package(("plugins/Jotunn.dll", "jotunn"), ("plugins/Other.dll", "other"));

        Assert.Equal("mods.file_exists", (await Assert.ThrowsAsync<GameProcessException>(() => ChangeAsync([PackageDownload(JotunnPath, Jotunn)], []))).Code);
        Assert.Equal("uploaded", File.ReadAllText(Path.Combine(_root, "BepInEx", "plugins", "ValheimModding-Jotunn", "Jotunn.dll")));
        Assert.False(File.Exists(Path.Combine(_root, "BepInEx", "plugins", "ValheimModding-Jotunn", "Other.dll")));

        _http.Routes[Jotunn] = Package(("plugins/../../../outside.dll", "x"));
        Assert.Equal("mods.bad_package", (await Assert.ThrowsAsync<GameProcessException>(() =>
            ChangeAsync([PackageDownload("BepInEx/plugins/Other-Package", Jotunn)], []))).Code);
        Assert.Equal("mods.invalid", (await Assert.ThrowsAsync<GameProcessException>(() =>
            ChangeAsync([PackageDownload(JotunnPath, Jotunn) with { Layout = null }], []))).Code); // a package's folder takes a package
        Assert.Equal("mods.invalid", (await Assert.ThrowsAsync<GameProcessException>(() =>
            ChangeAsync([new ModDownload("mods/x.jar", Lithium, "", 0)], []))).Code); // only a package goes without a checksum
        _http.Routes["https://cdn.modrinth.com/package.zip"] = Package(("plugins/x.dll", "x"));
        Assert.Equal("mods.untrusted_host", (await Assert.ThrowsAsync<GameProcessException>(() =>
            ChangeAsync([PackageDownload("BepInEx/plugins/Other-Package", "https://cdn.modrinth.com/package.zip")], []))).Code); // Thunderstore's hosts only
    }

    [Fact]
    public async Task APackageWhoseRecordIsLostIsStillReplacedAndRemovedByItsOwnFolders()
    {
        _http.Routes[Jotunn] = Package(("plugins/Jotunn.dll", "jotunn"), ("plugins/Extra.dll", "extra"));
        await ChangeAsync([PackageDownload(JotunnPath, Jotunn)], []);
        File.Delete(Path.Combine(_root, ".pulsar", "packages", "ValheimModding-Jotunn.json"));

        _http.Routes[Jotunn] = Package(("plugins/Jotunn.dll", "jotunn 2"));
        await ChangeAsync([PackageDownload(JotunnPath, Jotunn)], [JotunnPath]);
        var plugins = Path.Combine(_root, "BepInEx", "plugins", "ValheimModding-Jotunn");
        Assert.Equal("jotunn 2", File.ReadAllText(Path.Combine(plugins, "Jotunn.dll")));
        Assert.False(File.Exists(Path.Combine(plugins, "Extra.dll")));

        File.Delete(Path.Combine(_root, ".pulsar", "packages", "ValheimModding-Jotunn.json"));
        await ChangeAsync([], [JotunnPath]);
        Assert.False(Directory.Exists(plugins));
    }

    [Fact]
    public async Task ARecordTheServerChangedDeletesNothingOutsideThePackagesFolders()
    {
        // The server's account can write .pulsar: its record of a package cannot make the agent delete the server's files.
        _http.Routes[Jotunn] = Package(("plugins/Jotunn.dll", "jotunn"));
        await ChangeAsync([PackageDownload(JotunnPath, Jotunn)], []);
        File.WriteAllText(Path.Combine(_root, "server.cfg"), "keep");
        File.WriteAllText(Path.Combine(_root, ".pulsar", "packages", "ValheimModding-Jotunn.json"),
            """{ "package": "ValheimModding-Jotunn", "files": ["server.cfg", "BepInEx/plugins/ValheimModding-Jotunn2/x.dll", "BepInEx/plugins/ValheimModding-Jotunn/Jotunn.dll"] }""");

        await ChangeAsync([], [JotunnPath]);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "server.cfg")));
        Assert.False(File.Exists(Path.Combine(_root, "BepInEx", "plugins", "ValheimModding-Jotunn", "Jotunn.dll")));
    }

    private Task ChangeAsync(IReadOnlyList<ModDownload> add, IReadOnlyList<string> remove) =>
        new ModInstaller(new DirectServerFiles(), _http) { StallTimeout = TimeSpan.FromSeconds(5) }
            .ChangeAsync(_root, s_folders, add, remove, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken,
                new HashSet<string>(StringComparer.Ordinal) { "BepInEx" });

    private static ModDownload PackageDownload(string path, string url) => new(path, url, "", 0, ModTargetDefinition.BepInExLayout);

    private static byte[] Package(params (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    private static string Sha1(byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(data);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static ModDownload Download(string path, string url, byte[] content) =>
        new(path, url, Convert.ToHexStringLower(SHA512.HashData(content)), content.Length);

    private sealed class Files : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Routes { get; } = [];

        public List<string> Requested { get; } = [];

        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.OriginalString;
            Requested.Add(url);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(Routes.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
