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

    [Fact]
    public async Task AnUpdateCheckComparesTheInstalledServerWithMojangs()
    {
        var before = await CheckAsync("latest");
        Assert.Equal(new GameBuild(null, "26.3", true), before);

        await InstallAsync("latest");
        Assert.Equal($"26.3\n{Sha1(_jar)}\n", File.ReadAllText(Path.Combine(_root, ".pulsar", "minecraft-version.txt")));
        _http.Requested.Clear();

        Assert.Equal(new GameBuild("26.3", "26.3", false), await CheckAsync("latest"));
        Assert.DoesNotContain(Jar, _http.Requested); // nothing downloaded: the checksum Mojang lists is compared
    }

    [Fact]
    public async Task AServerJarThatIsNotTheRecordedOneIsNotNamed()
    {
        await InstallAsync("26.3");
        // A new release came out: the installed version keeps its name while its file is the one recorded.
        var newer = Encoding.UTF8.GetBytes("the 26.4 server jar");
        var details = Json(new { downloads = new { server = new { url = Jar, sha1 = Sha1(newer), size = newer.Length } } });
        _http.Routes[Details] = () => Ok(details);
        _http.Routes[Manifest] = () => Ok(Json(new
        {
            latest = new { release = "26.4" },
            versions = new[] { new { id = "26.4", type = "release", url = Details, sha1 = Sha1(details) } },
        }));
        Assert.Equal(new GameBuild("26.3", "26.4", true), await CheckAsync("latest"));

        // Replaced by hand (or by the server's account): what it is cannot be told.
        File.WriteAllText(Path.Combine(_root, "server.jar"), "a modded jar");
        Assert.Equal(new GameBuild(null, "26.4", true), await CheckAsync("latest"));
    }

    [Fact]
    public async Task ACheckThatCannotReachMojangFails()
    {
        _http.Routes.Remove(Manifest);

        var error = await Assert.ThrowsAsync<GameProcessException>(() => CheckAsync("latest"));

        Assert.StartsWith("minecraft.", error.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaperIsItsNewestStableBuildAsTheServerJar()
    {
        var paper = Encoding.UTF8.GetBytes("paper 223");
        _http.Routes["https://fill.papermc.io/v3/projects/paper"] = () => Ok(Json(new
        {
            project = new { id = "paper" },
            versions = new Dictionary<string, string[]> { ["26.3"] = ["26.3", "26.3-rc-3"], ["1.21"] = ["1.21.11"] },
        }));
        _http.Routes["https://fill.papermc.io/v3/projects/paper/versions/26.3/builds"] = () => Ok(Json(new object[]
        {
            PaperBuild(224, "BETA", Encoding.UTF8.GetBytes("paper 224")),
            PaperBuild(223, "STABLE", paper),
        }));
        _http.Routes[PaperJar(223)] = () => Ok(paper);

        await InstallAsync(new MinecraftChoice("latest", "paper"));

        Assert.Equal(paper, File.ReadAllBytes(Path.Combine(_root, "server.jar")));
        Assert.Equal("java", File.ReadAllText(Path.Combine(_root, "java", "bin", "java.exe"))); // the runtime 26.3 needs
        Assert.DoesNotContain(PaperJar(224), _http.Requested);
        Assert.Equal(new GameBuild("26.3 (Paper 223)", "26.3 (Paper 223)", false), await CheckAsync(new MinecraftChoice("latest", "paper")));

        // A newer stable build: compared by the checksum Paper lists, nothing downloaded.
        _http.Routes["https://fill.papermc.io/v3/projects/paper/versions/26.3/builds"] = () => Ok(Json(new object[]
        {
            PaperBuild(225, "STABLE", Encoding.UTF8.GetBytes("paper 225")),
            PaperBuild(223, "STABLE", paper),
        }));
        Assert.Equal(new GameBuild("26.3 (Paper 223)", "26.3 (Paper 225)", true), await CheckAsync(new MinecraftChoice("latest", "paper")));
        Assert.DoesNotContain(PaperJar(225), _http.Requested);

        _http.Routes["https://fill.papermc.io/v3/projects/paper/versions/1.21.11/builds"] = () => Ok(Json(new object[]
        {
            PaperBuild(130, "BETA", Encoding.UTF8.GetBytes("paper 130")),
        }));
        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(new MinecraftChoice("1.21.11", "paper")));
        Assert.Equal("minecraft.no_build", error.Code); // no stable build listed for it
        error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(new MinecraftChoice("1.12.2", "paper")));
        Assert.Equal("minecraft.no_build", error.Code); // a version Paper does not list
    }

    [Fact]
    public async Task PurpurIsCheckedAgainstTheMd5ItPublishes()
    {
        var purpur = Encoding.UTF8.GetBytes("purpur 2654");
        _http.Routes["https://api.purpurmc.org/v2/purpur"] = () => Ok(Json(new { metadata = new { current = "26.3" }, versions = s_purpurVersions }));
        _http.Routes["https://api.purpurmc.org/v2/purpur/26.3/latest"] = () => Ok(Json(new { build = "2654", result = "SUCCESS", md5 = Md5(purpur) }));
        _http.Routes["https://api.purpurmc.org/v2/purpur/26.3/2654/download"] = () => Ok(Encoding.UTF8.GetBytes("tampered"));

        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(new MinecraftChoice(null, "purpur")));
        Assert.Equal("minecraft.checksum_mismatch", error.Code);

        _http.Routes["https://api.purpurmc.org/v2/purpur/26.3/2654/download"] = () => Ok(purpur);
        await InstallAsync(new MinecraftChoice(null, "purpur"));
        Assert.Equal(purpur, File.ReadAllBytes(Path.Combine(_root, "server.jar")));
        Assert.Equal(new GameBuild("26.3 (Purpur 2654)", "26.3 (Purpur 2654)", false), await CheckAsync(new MinecraftChoice(null, "purpur")));
    }

    [Fact]
    public async Task FabricIsLaidOutAsItsInstallerDoesEachFileChecked()
    {
        var asm = Encoding.UTF8.GetBytes("asm");
        var intermediary = Encoding.UTF8.GetBytes("intermediary");
        var loader = Encoding.UTF8.GetBytes("loader");
        ServeFabric(asm, intermediary, loader);

        await InstallAsync(new MinecraftChoice("latest", "fabric"));

        Assert.Equal(_jar, File.ReadAllBytes(Path.Combine(_root, "server.jar"))); // Mojang's, checked against its SHA-1
        Assert.Equal(asm, File.ReadAllBytes(Path.Combine(_root, "libraries", "org", "ow2", "asm", "asm", "9.10.1", "asm-9.10.1.jar")));
        Assert.Equal(intermediary, File.ReadAllBytes(Path.Combine(_root, "libraries", "net", "fabricmc", "intermediary", "26.3", "intermediary-26.3.jar")));
        Assert.Equal(loader, File.ReadAllBytes(Path.Combine(_root, "libraries", "net", "fabricmc", "fabric-loader", "0.20.1", "fabric-loader-0.20.1.jar")));
        using (var launcher = ZipFile.OpenRead(Path.Combine(_root, "fabric-server-launch.jar")))
        {
            using var manifest = new StreamReader(launcher.GetEntry("META-INF/MANIFEST.MF")!.Open());
            var text = manifest.ReadToEnd();
            Assert.Contains("Main-Class: net.fabricmc.loader.impl.launch.server.FabricServerLauncher\r\n", text, StringComparison.Ordinal);
            Assert.All(text.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 72, line));
            Assert.Equal("libraries/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar libraries/net/fabricmc/intermediary/26.3/intermediary-26.3.jar "
                + "libraries/net/fabricmc/fabric-loader/0.20.1/fabric-loader-0.20.1.jar",
                text.Split("Class-Path: ")[1].Split("\r\n\r\n")[0].Replace("\r\n ", "", StringComparison.Ordinal));
            using var properties = new StreamReader(launcher.GetEntry("fabric-server-launch.properties")!.Open());
            Assert.Equal("launch.mainClass=net.fabricmc.loader.impl.launch.knot.KnotServer\n", properties.ReadToEnd());
        }

        Assert.Equal(new GameBuild("26.3 (Fabric 0.20.1)", "26.3 (Fabric 0.20.1)", false), await CheckAsync(new MinecraftChoice("latest", "fabric")));
        // A newer loader is an update.
        _http.Routes["https://meta.fabricmc.net/v2/versions/loader"] = () => Ok(Json(new[] { new { version = "0.20.2", stable = true } }));
        Assert.Equal(new GameBuild("26.3 (Fabric 0.20.1)", "26.3 (Fabric 0.20.2)", true), await CheckAsync(new MinecraftChoice("latest", "fabric")));
    }

    [Fact]
    public async Task AFabricLibraryFromElsewhereOrTamperedIsRefused()
    {
        ServeFabric(Encoding.UTF8.GetBytes("asm"), Encoding.UTF8.GetBytes("intermediary"), Encoding.UTF8.GetBytes("loader"),
            libraryUrl: "https://example.com/");
        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(new MinecraftChoice("26.3", "fabric", "0.20.1")));
        Assert.Equal("minecraft.bad_library", error.Code);

        ServeFabric(Encoding.UTF8.GetBytes("asm"), Encoding.UTF8.GetBytes("intermediary"), Encoding.UTF8.GetBytes("loader"));
        _http.Routes[FabricMaven("net/fabricmc/fabric-loader/0.20.1/fabric-loader-0.20.1.jar")] = () => Ok(Encoding.UTF8.GetBytes("evil"));
        error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(new MinecraftChoice("26.3", "fabric", "0.20.1")));
        Assert.Equal("minecraft.checksum_mismatch", error.Code);
    }

    [Fact]
    public async Task ForgeRunsItsInstallerAsTheServerAndStartsFromTheArgumentsItLeaves()
    {
        var installer = Encoding.UTF8.GetBytes("forge installer");
        const string Url = "https://maven.minecraftforge.net/net/minecraftforge/forge/26.3-66.1.0/forge-26.3-66.1.0-installer.jar";
        _http.Routes["https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"] = () => Ok(Json(new
        {
            promos = new Dictionary<string, string> { ["1.21.8-recommended"] = "58.1.26", ["26.3-latest"] = "66.1.1", ["26.3-recommended"] = "66.1.0" },
        }));
        _http.Routes[Url] = () => Ok(installer);
        _http.Routes[$"{Url}.sha256"] = () => Ok(Encoding.ASCII.GetBytes(Hex(SHA256.HashData(installer)) + "  forge-26.3-66.1.0-installer.jar\n"));
        var runs = new List<IReadOnlyList<string>>();
        Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            runs.Add(arguments);
            Assert.Equal(installer, File.ReadAllBytes(Path.Combine(_root, ".pulsar", "minecraft-installer.jar"))); // checked, then run
            Assert.True(File.Exists(Path.Combine(_root, "java", "bin", "java.exe"))); // on the runtime its version needs
            var libraries = Directory.CreateDirectory(Path.Combine(_root, "libraries", "net", "minecraftforge", "forge", "26.3-66.1.0"));
            File.WriteAllText(Path.Combine(libraries.FullName, "win_args.txt"), "-p libraries/a.jar cpw.mods.bootstraplauncher.BootstrapLauncher");
            return Task.CompletedTask;
        }

        await new MinecraftInstaller(new DirectServerFiles(), _http, RunAsync) { StallTimeout = TimeSpan.FromSeconds(5) }
            .InstallAsync(_root, new MinecraftChoice("latest", "forge"), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(["-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-jar", ".pulsar/minecraft-installer.jar", "--installServer"], Assert.Single(runs));
        Assert.Equal("-p libraries/a.jar cpw.mods.bootstraplauncher.BootstrapLauncher", File.ReadAllText(Path.Combine(_root, ".pulsar", "minecraft-args.txt")));
        Assert.False(File.Exists(Path.Combine(_root, ".pulsar", "minecraft-installer.jar")));
        Assert.Equal(new GameBuild("26.3 (Forge 66.1.0)", "26.3 (Forge 66.1.0)", false), await CheckAsync(new MinecraftChoice("latest", "forge")));

        // Without a way to run it as the server's account, Forge is not installed; nor for versions before 1.17.
        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(new MinecraftChoice("26.3", "forge")));
        Assert.Equal("minecraft.installer_unavailable", error.Code);
        error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(new MinecraftChoice("1.16.5", "forge")));
        Assert.Equal("minecraft.loader_unsupported", error.Code);
        error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(new MinecraftChoice("latest", "forge", "66.1.0")));
        Assert.Equal("minecraft.loader_needs_version", error.Code);
    }

    [Fact]
    public async Task NeoForgeTakesItsNewestReleaseForTheVersion()
    {
        var installer = Encoding.UTF8.GetBytes("neoforge installer");
        const string Url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/26.3.0.99/neoforge-26.3.0.99-installer.jar";
        _http.Routes["https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge"] = () => Ok(Json(new
        {
            versions = s_neoForgeVersions,
        }));
        _http.Routes[Url] = () => Ok(installer);
        _http.Routes[$"{Url}.sha256"] = () => Ok(Encoding.ASCII.GetBytes(Hex(SHA256.HashData(installer))));
        Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var libraries = Directory.CreateDirectory(Path.Combine(_root, "libraries", "net", "neoforged", "neoforge", "26.3.0.99"));
            File.WriteAllText(Path.Combine(libraries.FullName, "win_args.txt"), "neoforge arguments");
            return Task.CompletedTask;
        }

        await new MinecraftInstaller(new DirectServerFiles(), _http, RunAsync) { StallTimeout = TimeSpan.FromSeconds(5) }
            .InstallAsync(_root, new MinecraftChoice("latest", "neoforge"), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal("neoforge arguments", File.ReadAllText(Path.Combine(_root, ".pulsar", "minecraft-args.txt")));
        Assert.Equal(new GameBuild("26.3 (NeoForge 26.3.0.99)", "26.3 (NeoForge 26.3.0.99)", false),
            await CheckAsync(new MinecraftChoice("26.3", "neoforge")));
        var error = await Assert.ThrowsAsync<GameProcessException>(() => InstallAsync(new MinecraftChoice("26.3", "neoforge", "21.1.265")));
        Assert.Equal("minecraft.loader_mismatch", error.Code);
    }

    [Theory]
    [InlineData("21.1.265", "1.21.1")]
    [InlineData("21.0.12", "1.21")]
    [InlineData("20.4.237", "1.20.4")]
    [InlineData("26.1.2.124", "26.1.2")]
    [InlineData("26.3.0.99", "26.3")]
    [InlineData("21.11.38-beta", "1.21.11")]
    [InlineData("0.25w14craftmine.3-beta", null)]
    [InlineData("47.1.106", null)]
    public void NeoForgeVersionsNameTheirMinecraftVersion(string neoForge, string? minecraft) =>
        Assert.Equal(minecraft, MinecraftInstaller.NeoForgeMinecraft(neoForge));

    [Theory]
    [InlineData("net.fabricmc:fabric-loader:0.20.1", "net/fabricmc/fabric-loader/0.20.1/fabric-loader-0.20.1.jar")]
    [InlineData("net.fabricmc:sponge-mixin:0.17.5+mixin.0.8.7", "net/fabricmc/sponge-mixin/0.17.5+mixin.0.8.7/sponge-mixin-0.17.5+mixin.0.8.7.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar")]
    public void MavenCoordinatesBecomePaths(string coordinate, string path) => Assert.Equal(path, MinecraftInstaller.MavenPath(coordinate));

    [Theory]
    [InlineData("net.fabricmc:fabric-loader")]
    [InlineData("..:x:1")]
    [InlineData("a:..:1")]
    [InlineData("a:b:1/../../x")]
    [InlineData("a:b:c:d:e")]
    public void OtherCoordinatesAreRefused(string coordinate) =>
        Assert.Equal("minecraft.bad_library", Assert.Throws<GameProcessException>(() => MinecraftInstaller.MavenPath(coordinate)).Code);

    private void ServeFabric(byte[] asm, byte[] intermediary, byte[] loader, string libraryUrl = "https://maven.fabricmc.net/")
    {
        _http.Routes["https://meta.fabricmc.net/v2/versions/game"] = () => Ok(Json(new object[]
        {
            new { version = "26.4-snapshot-5", stable = false }, new { version = "26.3", stable = true },
        }));
        _http.Routes["https://meta.fabricmc.net/v2/versions/loader"] = () => Ok(Json(new[] { new { version = "0.20.1", stable = true } }));
        _http.Routes["https://meta.fabricmc.net/v2/versions/loader/26.3/0.20.1"] = () => Ok(Json(new
        {
            loader = new { maven = "net.fabricmc:fabric-loader:0.20.1", version = "0.20.1" },
            intermediary = new { maven = "net.fabricmc:intermediary:26.3", version = "26.3" },
            launcherMeta = new
            {
                libraries = new
                {
                    client = Array.Empty<object>(),
                    common = new[] { new { name = "org.ow2.asm:asm:9.10.1", url = libraryUrl, sha256 = Hex(SHA256.HashData(asm)) } },
                    server = Array.Empty<object>(),
                },
            },
        }));
        _http.Routes[FabricMaven("org/ow2/asm/asm/9.10.1/asm-9.10.1.jar")] = () => Ok(asm);
        foreach (var (path, content) in new[]
                 {
                     ("net/fabricmc/intermediary/26.3/intermediary-26.3.jar", intermediary),
                     ("net/fabricmc/fabric-loader/0.20.1/fabric-loader-0.20.1.jar", loader),
                 })
        {
            _http.Routes[FabricMaven(path)] = () => Ok(content);
            _http.Routes[FabricMaven(path) + ".sha256"] = () => Ok(Encoding.ASCII.GetBytes(Hex(SHA256.HashData(content))));
        }
    }

    private static readonly string[] s_purpurVersions = ["26.2", "26.3"];

    private static readonly string[] s_neoForgeVersions = ["26.3.0.99", "21.1.265", "26.3.1.1-beta", "26.3.0.98", "0.25w14craftmine.3-beta"];

    /// <summary>Purpur publishes only an MD5.</summary>
    private static string Md5(byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        hash.AppendData(data);
        return Hex(hash.GetHashAndReset());
    }

    private static string FabricMaven(string path) => $"https://maven.fabricmc.net/{path}";

    private static string PaperJar(int build) => $"https://fill-data.papermc.io/v1/objects/{build}/paper-26.3-{build}.jar";

    private static Dictionary<string, object> PaperBuild(int id, string channel, byte[] jar) => new()
    {
        ["id"] = id,
        ["channel"] = channel,
        ["downloads"] = new Dictionary<string, object>
        {
            ["server:default"] = new { name = $"paper-26.3-{id}.jar", checksums = new { sha256 = Hex(SHA256.HashData(jar)) }, url = PaperJar(id) },
        },
    };

    private Task<GameBuild> CheckAsync(MinecraftChoice choice) =>
        new MinecraftInstaller(new DirectServerFiles(), _http) { StallTimeout = TimeSpan.FromSeconds(5) }.CheckAsync(_root, choice,
            TestContext.Current.CancellationToken);

    private Task InstallAsync(MinecraftChoice choice) =>
        new MinecraftInstaller(new DirectServerFiles(), _http) { StallTimeout = TimeSpan.FromSeconds(5) }.InstallAsync(_root, choice,
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

    private Task<GameBuild> CheckAsync(string version) =>
        new MinecraftInstaller(new DirectServerFiles(), _http) { StallTimeout = TimeSpan.FromSeconds(5) }.CheckAsync(_root, version,
            TestContext.Current.CancellationToken);

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
