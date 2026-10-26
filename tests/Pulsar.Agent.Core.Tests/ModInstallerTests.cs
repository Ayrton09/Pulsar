using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// A mod catalog's files put into a server (Pulsar 1.0.9): downloaded from Modrinth's CDN only, checked against their
/// SHA-512 before anything changes, written and deleted only in the game's mod folders.
/// </summary>
public sealed class ModInstallerTests : IDisposable
{
    private const string Lithium = "https://cdn.modrinth.com/data/gvQqBUqZ/versions/qxIL7Kb8/lithium-fabric-0.18.1.jar";
    private const string FabricApi = "https://cdn.modrinth.com/data/P7dR8mSH/versions/abc/fabric-api-0.130.0.jar";

    private static readonly HashSet<string> s_folders = new(StringComparer.Ordinal) { "mods", "plugins" };

    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-mods-").FullName;
    private readonly Files _http = new();
    private readonly byte[] _lithium = Encoding.UTF8.GetBytes("lithium");
    private readonly byte[] _fabricApi = Encoding.UTF8.GetBytes("fabric api");

    public ModInstallerTests()
    {
        _http.Routes[Lithium] = _lithium;
        _http.Routes[FabricApi] = _fabricApi;
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

    private Task ChangeAsync(IReadOnlyList<ModDownload> add, IReadOnlyList<string> remove) =>
        new ModInstaller(new DirectServerFiles(), _http) { StallTimeout = TimeSpan.FromSeconds(5) }
            .ChangeAsync(_root, s_folders, add, remove, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

    private static ModDownload Download(string path, string url, byte[] content) =>
        new(path, url, Convert.ToHexStringLower(SHA512.HashData(content)), content.Length);

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
