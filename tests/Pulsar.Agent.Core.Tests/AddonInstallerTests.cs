using System.Formats.Tar;
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
    public async Task ARecordTheServerChangedCannotMakeRemovingTouchPulsarsOwnFiles()
    {
        // The server's account can write .pulsar/addons: its record may name SteamCMD or another addon's record.
        await InstallAsync(s_oxide);
        var recordFile = Path.Combine(_root, ".pulsar", "addons", "oxide.json");
        var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(recordFile))!;
        var added = record["added"]!.AsArray();
        added.Add(".pulsar/steamcmd/steamcmd.exe");
        added.Add(".PULSAR/addons/metamod.json");
        File.WriteAllText(recordFile, record.ToJsonString());
        Directory.CreateDirectory(Path.Combine(_root, ".pulsar", "steamcmd"));
        File.WriteAllText(Path.Combine(_root, ".pulsar", "steamcmd", "steamcmd.exe"), "valve's");
        File.WriteAllText(Path.Combine(_root, ".pulsar", "addons", "metamod.json"), "{}");

        var installer = new AddonInstaller(new DirectServerFiles(), _http);
        Assert.DoesNotContain(installer.Read(_root, "oxide")!.Added, p => p.StartsWith(".pulsar", StringComparison.OrdinalIgnoreCase));
        installer.Remove(_root, s_oxide);

        Assert.True(File.Exists(Path.Combine(_root, ".pulsar", "steamcmd", "steamcmd.exe")));
        Assert.True(File.Exists(Path.Combine(_root, ".pulsar", "addons", "metamod.json")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "key": "oxide" }""")]
    [InlineData("""{ "key": "oxide", "added": null, "replaced": null }""")]
    public async Task ARecordWithoutItsListsIsReadAsEmptyAndOneWithoutItsKeyAsNoRecord(string json)
    {
        // The server's account can write .pulsar/addons: a record may lack anything.
        await InstallAsync(s_oxide);
        File.WriteAllText(Path.Combine(_root, ".pulsar", "addons", "oxide.json"), json);
        var installer = new AddonInstaller(new DirectServerFiles(), _http);

        var record = installer.Read(_root, "oxide");

        if (json == "{}")
        {
            Assert.Null(record);
        }
        else
        {
            Assert.NotNull(record);
            Assert.Empty(record.Added);
            Assert.Empty(record.Replaced);
        }

        Assert.Empty(installer.Remove(_root, s_oxide));
    }

    [Fact]
    public async Task RemovingAnAddonWhoseEditedFileIsNotUtf8FailsAndChangesNothing()
    {
        var zip = Zip(("addons/metamod/bin/win64/server.dll", "mm"));
        _http.Routes["https://api.github.com/repos/alliedmodders/metamod-source/releases?per_page=10"] = () => Json(new object[]
        {
            new
            {
                tag_name = "2.0.0.1473", draft = false,
                assets = new[] { new { name = "mmsource-2.0.0-git1473-windows.zip", browser_download_url = "https://github.com/x/mm.zip", digest = "sha256:" + Sha256(zip) } },
            },
        });
        _http.Routes["https://github.com/x/mm.zip"] = () => Ok(zip);
        Directory.CreateDirectory(Path.Combine(_root, "game", "csgo"));
        await InstallAsync(s_metamod);
        // Saved by hand as Windows-1252 ("é" is 0xE9), after the start added the addon's line.
        var gameinfo = Path.Combine(_root, "game", "csgo", "gameinfo.gi");
        byte[] ansi = [.. Encoding.ASCII.GetBytes("// caf"), 0xE9, .. Encoding.ASCII.GetBytes("\n\t\t\tGame_LowViolence\tcsgo_lv\n\t\t\tGame\tcsgo/addons/metamod\n")];
        File.WriteAllBytes(gameinfo, ansi);

        var failed = Assert.Throws<GameProcessException>(() => new AddonInstaller(new DirectServerFiles(), _http).Remove(_root, s_metamod));

        Assert.Equal("config.invalid", failed.Code);
        Assert.Equal(ansi, File.ReadAllBytes(gameinfo));
        Assert.True(File.Exists(Path.Combine(_root, "game", "csgo", "addons", "metamod", "bin", "win64", "server.dll")));
        Assert.NotNull(new AddonInstaller(new DirectServerFiles(), _http).Read(_root, "metamod"));
    }

    [Fact]
    public async Task AGameFileAnOlderReleaseReplacedStaysReplacedWhenANewerOneNoLongerShipsIt()
    {
        await InstallAsync(s_oxide);

        // The game's file still holds the older release's copy: only a validating update brings the game's back.
        Release("2.0.7900", Zip(("RustDedicated_Data/Managed/Oxide.Core.dll", "core 2")));
        var next = await InstallAsync(s_oxide);
        Assert.Equal(["RustDedicated_Data/Managed/Assembly-CSharp.dll"], next.Replaced);
        Assert.Equal(["RustDedicated_Data/Managed/Oxide.Core.dll"], next.Added);

        Assert.Equal(["RustDedicated_Data/Managed/Assembly-CSharp.dll"], new AddonInstaller(new DirectServerFiles(), _http).Remove(_root, s_oxide));
        Assert.True(File.Exists(Path.Combine(_root, "RustDedicated_Data", "Managed", "Assembly-CSharp.dll"))); // the game's, never deleted
    }

    [Fact]
    public async Task AFirstInstallThatFailsMidwayStillKnowsTheFilesItAdded()
    {
        // The last entry cannot be written the first time (a locked file, a full disk): two files are already in place.
        var files = new FailingServerFiles("oxide/config.json");
        var installer = new AddonInstaller(files, _http);
        var failed = await Assert.ThrowsAsync<GameProcessException>(() => installer.InstallAsync(_root, s_oxide, force: false, (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken));
        Assert.Equal("addon.download_failed", failed.Code);
        Assert.True(File.Exists(Path.Combine(_root, "RustDedicated_Data", "Managed", "Oxide.Core.dll")));

        var record = await installer.InstallAsync(_root, s_oxide, force: false, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(["RustDedicated_Data/Managed/Oxide.Core.dll", "oxide/config.json"], record.Added);
        Assert.Equal(["RustDedicated_Data/Managed/Assembly-CSharp.dll"], record.Replaced);

        Assert.Equal(["RustDedicated_Data/Managed/Assembly-CSharp.dll"], installer.Remove(_root, s_oxide));
        Assert.False(File.Exists(Path.Combine(_root, "RustDedicated_Data", "Managed", "Oxide.Core.dll")));
        Assert.False(Directory.Exists(Path.Combine(_root, "oxide")));
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
    [InlineData("PULSAR~1/addons/metamod.json")] // .pulsar's 8.3 short name, on volumes that keep them
    [InlineData("C:/Windows/evil.dll")]
    public async Task AnArchiveCannotWriteOutsideItsPlace(string entry)
    {
        Release("2.0.7801", Zip((entry, "x")));

        Assert.Equal("addon.bad_archive", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_oxide))).Code);
    }

    [Fact]
    public async Task AHostThatNeverAnswersIsGivenUpOn()
    {
        // The callers' clients have no timeout (a download takes as long as it keeps coming): the answer's headers do.
        using var http = new HttpClient(new SilentHttp()) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new VerifiedDownloader(http, new HashSet<string> { "github.com" }, "addon", "addons") { HeadersTimeout = TimeSpan.FromMilliseconds(200) };

        var failed = await Assert.ThrowsAsync<GameProcessException>(() => downloader.SendAsync(new Uri(Download), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal("addon.download_failed", failed.Code);
    }

    private sealed class SilentHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Never answered.");
        }
    }

    [Fact]
    public async Task OneAssetMustMatch()
    {
        _http.Routes[Latest] = () => Json(new { tag_name = "x", draft = false, assets = Array.Empty<object>() });

        Assert.Equal("addon.no_asset", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_oxide))).Code);
    }

    [Fact]
    public async Task AGameServerFromAReleaseIsInstalledByItsStepAndRecordedApartFromTheAddons()
    {
        var step = new GitHubReleaseStep("OxideMod/Oxide.Rust", @"^Oxide\.Rust\.zip$");
        var steps = new InstallSteps(new InstallOptions { HttpHandler = _http }, new DirectServerFiles());

        var version = await steps.RunAsync(step, _root, new Dictionary<string, string?>(), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal("2.0.7801", version); // the release's tag, as the game's version
        Assert.Equal("oxide core", File.ReadAllText(Path.Combine(_root, "RustDedicated_Data", "Managed", "Oxide.Core.dll")));
        Assert.True(File.Exists(Path.Combine(_root, ".pulsar", "releases", "oxidemod_oxide.rust.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".pulsar", "addons")));
        Assert.True(InstallSteps.IsCheckable(step));
        Assert.Equal("Getting OxideMod/Oxide.Rust's latest release from GitHub, unless it is already installed",
            InstallSteps.Describe(step, new Dictionary<string, string?>()));

        // An update to the same release downloads nothing.
        _http.Requested.Clear();
        await steps.RunAsync(step, _root, new Dictionary<string, string?>(), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Download, _http.Requested);
    }

    [Fact]
    public async Task AnUpdateCheckOfAReleaseComparesItsAssetWithTheInstalledOne()
    {
        var source = (GitHubReleaseSource)s_oxide.Source;
        var installer = new AddonInstaller(new DirectServerFiles(), _http);
        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal(new GameBuild(null, "2.0.7801", true), await installer.CheckAsync(_root, "oxide", source, cancellation));

        await InstallAsync(s_oxide);
        _http.Requested.Clear();
        Assert.Equal(new GameBuild("2.0.7801", "2.0.7801", false), await installer.CheckAsync(_root, "oxide", source, cancellation));

        Release("2.0.7900", Zip(("RustDedicated_Data/Managed/Oxide.Core.dll", "core 2")));
        Assert.Equal(new GameBuild("2.0.7801", "2.0.7900", true), await installer.CheckAsync(_root, "oxide", source, cancellation));
        Assert.DoesNotContain(AssetHost, _http.Requested); // a check downloads nothing
    }

    // Valheim's BepInEx (Pulsar 1.0.14): Thunderstore's newest package, its own folder of the zip unpacked into the server.
    private static readonly AddonDefinition s_bepinex = new("bepinex", "BepInEx", "LGPL-2.1", "https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/",
        new ThunderstoreSource("denikson-BepInExPack_Valheim", "BepInExPack_Valheim"));

    private const string PackageApi = "https://thunderstore.io/api/experimental/package/denikson/BepInExPack_Valheim/";
    private const string PackageDownload = "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/5.4.2351/";
    private const string PackageCdn = "https://ccdn.thunderstore.io/live/repository/packages/denikson-BepInExPack_Valheim-5.4.2351.zip";

    [Fact]
    public async Task AThunderstorePackagesFolderIsUnpackedIntoTheServerFromThunderstoresHostsOnly()
    {
        var zip = Zip(("manifest.json", "{}"), ("icon.png", "png"), ("BepInExPack_Valheim/winhttp.dll", "doorstop"),
            ("BepInExPack_Valheim/BepInEx/core/BepInEx.dll", "core"));
        _http.Routes[PackageApi] = () => Json(new { latest = new { version_number = "5.4.2351", download_url = PackageDownload } });
        _http.Routes[PackageDownload] = () => Redirect(PackageCdn);
        _http.Routes[PackageCdn] = () => Ok(zip);

        var record = await InstallAsync(s_bepinex);

        Assert.Equal(("5.4.2351", "denikson-BepInExPack_Valheim-5.4.2351.zip", Sha256(zip)), (record.Release, record.Asset, record.Sha256));
        Assert.Equal(["winhttp.dll", "BepInEx/core/BepInEx.dll"], record.Added);
        Assert.Equal("core", File.ReadAllText(Path.Combine(_root, "BepInEx", "core", "BepInEx.dll")));
        Assert.False(File.Exists(Path.Combine(_root, "manifest.json"))); // Thunderstore's own files about it stay out
        Assert.All(_http.Requested.Where(r => r != PackageApi), r => Assert.Contains("thunderstore.io", r, StringComparison.Ordinal));

        // Thunderstore's answer naming another download is not followed.
        _http.Routes[PackageApi] = () => Json(new { latest = new { version_number = "5.4.2352", download_url = "https://evil.example/x.zip" } });
        Assert.Equal("addon.no_release", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_bepinex, force: true))).Code);
        _http.Routes[PackageApi] = () => Json(new { latest = new { version_number = "5.4.2351", download_url = PackageDownload } });
        _http.Routes[PackageDownload] = () => Redirect("https://evil.example/pack.zip");
        Assert.Equal("addon.untrusted_host", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_bepinex, force: true))).Code);
    }

    private Task<AddonRecord> InstallAsync(AddonDefinition addon, bool force = false) =>
        new AddonInstaller(new DirectServerFiles(), _http).InstallAsync(_root, addon, force, (stage, _) =>
        {
            _stages.Add(stage);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

    private void Release(string tag, byte[] zip, string? digest = "", string asset = "Oxide.Rust.zip")
    {
        var published = digest == "" ? "sha256:" + Sha256(zip) : digest;
        _http.Routes[Latest] = () => Json(new
        {
            tag_name = tag,
            draft = false,
            assets = new[] { new { name = asset, browser_download_url = Download, digest = published } },
        });
        _http.Routes[Download] = () => Redirect(AssetHost);
        _http.Routes[AssetHost] = () => Ok(zip);
    }

    // EXILED's layout: a .tar.gz of folders and files, put into the game's AppData (Pulsar 1.0.12).
    private static readonly AddonDefinition s_exiled = new("exiled", "EXILED", "CC-BY-SA-3.0", "https://github.com/ExMod-Team/EXILED",
        new GitHubReleaseSource("OxideMod/Oxide.Rust", "^Exiled\\.tar\\.gz$"), Target: "profile/AppData/Roaming");

    [Fact]
    public async Task ATarGzAddonIsUnpackedIntoItsFolder()
    {
        Release("v9.15.2", TarGz(
            Folder("EXILED"), Folder("EXILED/Plugins"), File_("EXILED/Plugins/Exiled.Events.dll", "events"),
            Folder("SCP Secret Laboratory/LabAPI/plugins/global"), File_("./SCP Secret Laboratory/LabAPI/plugins/global/Exiled.Loader.dll", "loader")),
            asset: "Exiled.tar.gz");

        var record = await InstallAsync(s_exiled);

        Assert.Equal("v9.15.2", record.Release);
        Assert.Equal("Exiled.tar.gz", record.Asset);
        Assert.Equal(["profile/AppData/Roaming/EXILED/Plugins/Exiled.Events.dll",
            "profile/AppData/Roaming/SCP Secret Laboratory/LabAPI/plugins/global/Exiled.Loader.dll"], record.Added);
        Assert.Equal("loader", File.ReadAllText(Path.Combine(_root, "profile", "AppData", "Roaming", "SCP Secret Laboratory", "LabAPI", "plugins", "global", "Exiled.Loader.dll")));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.CharacterDevice)]
    [InlineData(TarEntryType.Fifo)]
    public async Task ATarGzWithALinkOrADeviceIsRefusedBeforeAnythingIsWritten(TarEntryType type)
    {
        var odd = new PaxTarEntry(type, "EXILED/Plugins/odd");
        if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
        {
            odd.LinkName = "C:/Windows";
        }

        Release("v9.15.2", TarGz(File_("EXILED/Plugins/first.dll", "first"), odd), asset: "Exiled.tar.gz");

        Assert.Equal("addon.bad_archive", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_exiled))).Code);
        Assert.False(File.Exists(Path.Combine(_root, "profile", "AppData", "Roaming", "EXILED", "Plugins", "first.dll")));
    }

    [Theory]
    [InlineData("../../../outside.dll")]
    [InlineData("/C:/Windows/evil.dll")]
    public async Task ATarGzCannotWriteOutsideItsPlace(string entry)
    {
        Release("v9.15.2", TarGz(File_(entry, "x")), asset: "Exiled.tar.gz");

        Assert.Equal("addon.bad_archive", (await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(s_exiled))).Code);
    }

    private static PaxTarEntry Folder(string name) => new(TarEntryType.Directory, name);

    private static PaxTarEntry File_(string name, string content) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };

    private static byte[] TarGz(params TarEntry[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                tar.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
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

    /// <summary>Plain file access whose first write of one path fails.</summary>
    private sealed class FailingServerFiles(string failing) : IServerFiles
    {
        private readonly DirectServerFiles _inner = new();
        private bool _failed;

        public Stream? OpenRead(string serverRoot, string relativePath) => _inner.OpenRead(serverRoot, relativePath);

        public void Write(string serverRoot, string relativePath, Stream content)
        {
            if (!_failed && relativePath == failing)
            {
                _failed = true;
                throw new IOException("The disk is full.");
            }

            _inner.Write(serverRoot, relativePath, content);
        }

        public void Delete(string serverRoot, string relativePath) => _inner.Delete(serverRoot, relativePath);

        public void CreateDirectory(string serverRoot, string relativePath) => _inner.CreateDirectory(serverRoot, relativePath);

        public bool DeleteEmptyDirectory(string serverRoot, string relativePath) => _inner.DeleteEmptyDirectory(serverRoot, relativePath);

        public IReadOnlyList<(string Name, DateTimeOffset LastWrite)> ListFiles(string serverRoot, string relativeDirectory, int limit) =>
            _inner.ListFiles(serverRoot, relativeDirectory, limit);
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
