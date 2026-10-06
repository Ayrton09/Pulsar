using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>Addons from GitHub releases against a fake GitHub: its API, its redirect to the asset host and the asset.</summary>
public sealed class AddonInstallerTests : IDisposable
{
    private const string Latest = "https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest";
    private const string Download = "https://github.com/OxideMod/Oxide.Rust/releases/download/2.0.7801/Oxide.Rust.zip";
    private const string AssetHost = "https://release-assets.githubusercontent.com/oxide.zip";

    private static readonly AddonDefinition s_oxide = new("oxide", "Oxide", "MIT", "https://umod.org/",
        new GitHubReleaseSource("OxideMod/Oxide.Rust", "^Oxide\\.Rust\\.zip$"), ReinstallAfterUpdate: true);

    private static readonly AddonDefinition s_metamod = new("metamod", "Metamod:Source", "zlib", "https://www.metamodsource.net/",
        new GitHubReleaseSource("alliedmodders/metamod-source", "^mmsource-2\\.0\\.0-git[0-9]+-windows\\.zip$", Prerelease: true),
        Target: "game/csgo", LineEdits: [new LineEdit("game/csgo/gameinfo.gi", "Game_LowViolence csgo_lv", "Game\tcsgo/addons/metamod")]);

    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-addons-").FullName;
    private readonly FakeHttp _http = new();
    private readonly List<string> _stages = [];

    public AddonInstallerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "RustDedicated_Data", "Managed"));
        File.WriteAllText(Path.Combine(_root, "RustDedicated_Data", "Managed", "Assembly-CSharp.dll"), "the game's");
        Release("2.0.7801", Zip(("RustDedicated_Data/Managed/Assembly-CSharp.dll", "patched by oxide"), ("RustDedicated_Data/Managed/Oxide.Core.dll", "oxide core"),
            ("oxide/config.json", "{}")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AnAddonIsUnpackedAndItsFilesAreTold()
    {
        var record = await InstallAsync(s_oxide);

        Assert.Equal("2.0.7801", record.Release);
        Assert.Equal("patched by oxide", File.ReadAllText(Path.Combine(_root, "RustDedicated_Data", "Managed", "Assembly-CSharp.dll")));
        Assert.Equal(["RustDedicated_Data/Managed/Oxide.Core.dll", "oxide/config.json"], record.Added);
        Assert.Equal(["RustDedicated_Data/Managed/Assembly-CSharp.dll"], record.Replaced); // the game's own file
        Assert.Contains(AssetHost, _http.Requested); // followed github.com's redirect to its asset host
        Assert.Contains("downloading", _stages);
        var stored = new AddonInstaller(new DirectServerFiles(), _http).Read(_root, "oxide")!;
        Assert.Equal((record.Release, record.Sha256), (stored.Release, stored.Sha256));
        Assert.Equal(record.Added, stored.Added);
        Assert.Equal(record.Replaced, stored.Replaced);
    }

    [Fact]
    public async Task TheSameReleaseIsNotDownloadedAgainUnlessForcedAndANewOneDropsTheOldFiles()
    {
        await InstallAsync(s_oxide);
        _http.Requested.Clear();

        await InstallAsync(s_oxide);
        Assert.DoesNotContain(Download, _http.Requested);

        // A game update put the game's file back: forced, the addon goes in again and still knows the file is the game's.
        File.WriteAllText(Path.Combine(_root, "RustDedicated_Data", "Managed", "Assembly-CSharp.dll"), "the game's, updated");
        var forced = await InstallAsync(s_oxide, force: true);
        Assert.Equal(["RustDedicated_Data/Managed/Assembly-CSharp.dll"], forced.Replaced);

        Release("2.0.7900", Zip(("RustDedicated_Data/Managed/Assembly-CSharp.dll", "patched again"), ("RustDedicated_Data/Managed/Oxide.Core.dll", "core 2")));
        var next = await InstallAsync(s_oxide);
        Assert.Equal("2.0.7900", next.Release);
        Assert.False(File.Exists(Path.Combine(_root, "oxide", "config.json"))); // not in the new release
    }

    [Fact]
    public async Task RemovingAnAddonDeletesWhatItAddedAndReturnsWhatItReplaced()
    {
        await InstallAsync(s_oxide);

        var replaced = new AddonInstaller(new DirectServerFiles(), _http).Remove(_root, s_oxide);

        Assert.Equal(["RustDedicated_Data/Managed/Assembly-CSharp.dll"], replaced);
        Assert.False(File.Exists(Path.Combine(_root, "RustDedicated_Data", "Managed", "Oxide.Core.dll")));
        Assert.False(Directory.Exists(Path.Combine(_root, "oxide"))); // emptied, so gone too
        Assert.True(Directory.Exists(Path.Combine(_root, "RustDedicated_Data", "Managed"))); // still holds the game's files
        Assert.True(File.Exists(Path.Combine(_root, "RustDedicated_Data", "Managed", "Assembly-CSharp.dll")));
        Assert.Null(new AddonInstaller(new DirectServerFiles(), _http).Read(_root, "oxide"));
    }

    [Fact]
    public async Task APreReleaseSourceTakesTheNewestReleaseIntoItsFolderAndItsLineGoesAwayWithIt()
    {
        var zip = Zip(("addons/metamod/bin/win64/server.dll", "mm"), ("addons/metamod.vdf", "\"Plugin\" {}"));
        _http.Routes["https://api.github.com/repos/alliedmodders/metamod-source/releases?per_page=10"] = () => Json(new object[]
        {
            new { tag_name = "draft", draft = true, assets = Array.Empty<object>() },
            new
            {
                tag_name = "2.0.0.1473", draft = false,
                assets = new[]
                {
                    new { name = "mmsource-2.0.0-git1473-linux.tar.gz", browser_download_url = "https://github.com/x/linux.tar.gz", digest = "sha256:" + new string('0', 64) },
                    new { name = "mmsource-2.0.0-git1473-windows.zip", browser_download_url = "https://github.com/x/mm.zip", digest = "sha256:" + Sha256(zip) },
                },
            },
        });
        _http.Routes["https://github.com/x/mm.zip"] = () => Ok(zip);
        Directory.CreateDirectory(Path.Combine(_root, "game", "csgo"));
        File.WriteAllText(Path.Combine(_root, "game", "csgo", "gameinfo.gi"), "\t\t\tGame_LowViolence\tcsgo_lv // Perfect World content override\n\t\t\tGame\tcsgo\n");

        var record = await InstallAsync(s_metamod);
        Assert.Equal("2.0.0.1473", record.Release);
        Assert.Equal("mm", File.ReadAllText(Path.Combine(_root, "game", "csgo", "addons", "metamod", "bin", "win64", "server.dll")));

        // The start adds the line (ServerSupervisor); removing the addon takes it out.
        var gameinfo = Path.Combine(_root, "game", "csgo", "gameinfo.gi");
        File.WriteAllText(gameinfo, LineEditor.Apply(File.ReadAllText(gameinfo), s_metamod.LineEditList[0]));
        Assert.Contains("\t\t\tGame\tcsgo/addons/metamod\n", File.ReadAllText(gameinfo), StringComparison.Ordinal);
        new AddonInstaller(new DirectServerFiles(), _http).Remove(_root, s_metamod);
        Assert.False(Directory.Exists(Path.Combine(_root, "game", "csgo", "addons")));
        Assert.True(Directory.Exists(Path.Combine(_root, "game", "csgo"))); // its target folder stays
        Assert.Equal("\t\t\tGame_LowViolence\tcsgo_lv // Perfect World content override\n\t\t\tGame\tcsgo\n", File.ReadAllText(gameinfo));
    }

    [Fact]
    public async Task AnAssetWithoutAChecksumOrNotMatchingItIsRefused()
    {
        Release("2.0.7801", Zip(("a.dll", "a")), digest: null);
        Assert.Equal("addon.no_checksum", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_oxide))).Code);

        Release("2.0.7801", Zip(("a.dll", "a")), digest: "sha256:" + new string('a', 64));
        Assert.Equal("addon.checksum_mismatch", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_oxide))).Code);
        Assert.False(File.Exists(Path.Combine(_root, "a.dll")));
    }

    [Fact]
    public async Task OnlyGitHubsHostsOverHttpsAreFollowed()
    {
        _http.Routes[Download] = () => Redirect("https://evil.example/oxide.zip");

        Assert.Equal("addon.untrusted_host", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_oxide))).Code);
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData(".pulsar/addons/oxide.json")]
    [InlineData("C:/Windows/evil.dll")]
    public async Task AnArchiveCannotWriteOutsideItsPlace(string entry)
    {
        Release("2.0.7801", Zip((entry, "x")));

        Assert.Equal("addon.bad_archive", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_oxide))).Code);
    }

    [Fact]
    public async Task OneAssetMustMatch()
    {
        _http.Routes[Latest] = () => Json(new { tag_name = "x", draft = false, assets = Array.Empty<object>() });

        Assert.Equal("addon.no_asset", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_oxide))).Code);
    }

    private Task<AddonRecord> InstallAsync(AddonDefinition addon, bool force = false) =>
        new AddonInstaller(new DirectServerFiles(), _http).InstallAsync(_root, addon, force, (stage, _) =>
        {
            _stages.Add(stage);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

    private void Release(string tag, byte[] zip, string? digest = "")
    {
        var published = digest == "" ? "sha256:" + Sha256(zip) : digest;
        _http.Routes[Latest] = () => Json(new
        {
            tag_name = tag,
            draft = false,
            assets = new[] { new { name = "Oxide.Rust.zip", browser_download_url = Download, digest = published } },
        });
        _http.Routes[Download] = () => Redirect(AssetHost);
        _http.Routes[AssetHost] = () => Ok(zip);
    }

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

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static HttpResponseMessage Json(object body) => Ok(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));

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
