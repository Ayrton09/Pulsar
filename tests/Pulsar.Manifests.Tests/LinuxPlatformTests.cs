using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>Manifests for the Linux agent (<c>linux-x64</c>): its own rules beside Windows' ones.</summary>
public sealed class LinuxPlatformTests
{
    [Fact]
    public void TheTestGameRunsOnBothPlatforms()
    {
        var manifest = TestManifests.Load("_test");

        Assert.Equal([ManifestValidator.WindowsX64, ManifestValidator.LinuxX64], manifest.Platforms);
        Assert.True(manifest.RunsOn(ManifestValidator.LinuxX64));
        Assert.Equal("bin/Pulsar.TestGameServer", manifest.Runtime[ManifestValidator.LinuxX64].Executable);
        Assert.Same(manifest.Runtime[ManifestValidator.WindowsX64], manifest.FirstRuntime);
    }

    [Fact]
    public void AWindowsOnlyManifestDoesNotRunOnLinux()
    {
        var manifest = TestManifests.Parse(WindowsOnly());

        Assert.False(manifest.RunsOn(ManifestValidator.LinuxX64));
        Assert.True(manifest.RunsOn(ManifestValidator.WindowsX64));
    }

    [Fact]
    public void ALinuxOnlyManifestIsValid()
    {
        var manifest = TestManifests.Parse(TestManifests.ChangeWithLinux("_test", m =>
        {
            m["platforms"] = new JsonArray("linux-x64");
            m["runtime"]!.AsObject().Remove("windows-x64");
        }));

        Assert.Equal([ManifestValidator.LinuxX64], manifest.Platforms);
        Assert.False(manifest.RunsOn(ManifestValidator.WindowsX64));
    }

    [Fact]
    public void AnUnknownPlatformIsRefused()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["platforms"] = new JsonArray("windows-x64", "macos-arm64")));

        Assert.Contains(errors, e => e.Path == "platforms");
    }

    [Fact]
    public void ADeclaredPlatformNeedsItsRuntime()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!.AsObject().Remove("linux-x64")));

        Assert.Contains(errors, e => e.Path == "runtime" && e.Message.Contains("linux-x64", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bin/start.sh")]
    [InlineData("bin/server.py")]
    [InlineData("bin/Server.exe")]
    [InlineData("bin/server.jar")]
    [InlineData("bin/run.BASH")]
    public void LinuxStartsProgramsOnly(string executable)
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["executable"] = executable));

        Assert.Contains(errors, e => e.Path == "runtime.linux-x64.executable");
    }

    [Theory]
    [InlineData("bin/srcds_linux")]
    [InlineData("game/bin/linuxsteamrt64/cs2")]
    [InlineData("valheim_server.x86_64")]
    public void LinuxProgramsNeedNoExtension(string executable)
    {
        var errors = TestManifests.ErrorsOrNone(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["executable"] = executable));

        Assert.DoesNotContain(errors, e => e.Path == "runtime.linux-x64.executable");
    }

    [Fact]
    public void WindowsStillStartsExeFilesOnly()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["windows-x64"]!["executable"] = "bin/srcds_linux"));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.executable");
    }

    [Fact]
    public void ALinuxGameMayHaveAPseudoTerminal()
    {
        // Unturned reads its commands only from a terminal on Linux.
        var errors = TestManifests.ErrorsOrNone(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["console"]!["mode"] = "pty"));

        Assert.DoesNotContain(errors, e => e.Path == "runtime.linux-x64.console.mode");
    }

    [Fact]
    public void LinuxConsolesAreUtf8()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["console"]!["encoding"] = "oem"));

        Assert.Contains(errors, e => e.Path == "runtime.linux-x64.console.encoding");
    }

    [Theory]
    [InlineData("LD_PRELOAD")]
    [InlineData("LD_LIBRARY_PATH")]
    [InlineData("ld_audit")]
    [InlineData("XDG_RUNTIME_DIR")]
    public void EnvironmentNamesThatMakeTheLoaderLoadCodeAreRefused(string name)
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["environment"]![name] = "x"));

        Assert.Contains(errors, e => e.Path == $"runtime.linux-x64.environment.{name}");
    }

    [Theory]
    [InlineData("JAVA_TOOL_OPTIONS")]
    [InlineData("_JAVA_OPTIONS")]
    [InlineData("MONO_ENV_OPTIONS")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("PYTHONPATH")]
    [InlineData("GCONV_PATH")]
    [InlineData("GLIBC_TUNABLES")]
    [InlineData("BASH_ENV")]
    public void EnvironmentNamesThatMakeOtherRuntimesLoadCodeAreRefused(string name)
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["environment"]![name] = "x"));

        Assert.Contains(errors, e => e.Path == $"runtime.linux-x64.environment.{name}");
    }

    [Fact]
    public void EachPlatformsGameMustReceiveTheRconPassword()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m =>
        {
            var rcon = new JsonObject { ["protocol"] = "source", ["port"] = "game" };
            m["ports"] = new JsonArray(new JsonObject { ["name"] = "game", ["label"] = "Game", ["protocols"] = new JsonArray("udp"), ["default"] = 27015 });
            foreach (var platform in (string[])["windows-x64", "linux-x64"])
            {
                m["runtime"]![platform]!["console"] = new JsonObject { ["mode"] = "pipes", ["rcon"] = rcon.DeepClone() };
            }

            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("+rcon_password");
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("{{rcon.password}}");
        }));

        Assert.Contains(errors, e => e.Path == "runtime" && e.Message.Contains("on linux-x64", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Message.Contains("on windows-x64", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("USER")]
    [InlineData("TMPDIR")]
    [InlineData("SHELL")]
    public void TheAgentsOwnLinuxEnvironmentNamesAreReserved(string name)
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["windows-x64"]!["environment"]![name] = "x"));

        Assert.Contains(errors, e => e.Path == $"runtime.windows-x64.environment.{name}");
    }

    [Fact]
    public void WhatThePanelReadsOfARuntimeIsTheSameOnEveryPlatform()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m =>
        {
            m["runtime"]!["linux-x64"]!["allowExtraArguments"] = true;
            m["runtime"]!["linux-x64"]!["console"]!["sensitiveCommandPrefixes"] = new JsonArray("rcon_password");
        }));

        Assert.Contains(errors, e => e.Path == "runtime.linux-x64.allowExtraArguments");
        Assert.Contains(errors, e => e.Path == "runtime.linux-x64.console.sensitiveCommandPrefixes");
    }

    [Fact]
    public void TheSameCommandsKeptOutOfLogsInAnotherOrderAreTheSame()
    {
        var errors = TestManifests.ErrorsOrNone(TestManifests.ChangeWithLinux("_test", m =>
        {
            m["runtime"]!["windows-x64"]!["console"]!["sensitiveCommandPrefixes"] = new JsonArray("a", "b");
            m["runtime"]!["linux-x64"]!["console"]!["sensitiveCommandPrefixes"] = new JsonArray("b", "a");
        }));

        Assert.DoesNotContain(errors, e => e.Path.EndsWith(".sensitiveCommandPrefixes", StringComparison.Ordinal));
    }

    [Fact]
    public void AReleaseOfALinuxGameNamesItsLinuxAsset()
    {
        static JsonObject Release() => new() { ["type"] = "githubRelease", ["repo"] = "Pryaxis/TShock", ["asset"] = "^TShock-.*-win-x64-Release\\.zip$" };

        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["install"]!["steps"]!.AsArray().Add(Release())));
        var named = TestManifests.ErrorsOrNone(TestManifests.ChangeWithLinux("_test", m =>
        {
            var release = Release();
            release["linuxAsset"] = "^TShock-.*-linux-x64-Release\\.zip$";
            m["install"]!["steps"]!.AsArray().Add(release);
            m["install"]!["steps"]!.AsArray().Add(new JsonObject { ["type"] = "dotnetRuntime", ["channel"] = "9.0" });
        }));

        Assert.Contains(errors, e => e.Path == "install.steps[2].linuxAsset");
        Assert.DoesNotContain(named, e => e.Path.StartsWith("install.steps[2]", StringComparison.Ordinal) || e.Path.StartsWith("install.steps[3]", StringComparison.Ordinal));
    }

    [Fact]
    public void AReleaseOfAWindowsOnlyGameNeedsNoLinuxAsset()
    {
        var errors = TestManifests.ErrorsOrNone(TestManifests.ChangeWithLinux("_test", m =>
        {
            m["platforms"] = new JsonArray("windows-x64");
            m["runtime"]!.AsObject().Remove("linux-x64");
            m["install"]!["steps"]!.AsArray().Add(new JsonObject { ["type"] = "githubRelease", ["repo"] = "Pryaxis/TShock", ["asset"] = "^a\\.zip$" });
        }));

        Assert.DoesNotContain(errors, e => e.Path.StartsWith("install.steps[2]", StringComparison.Ordinal));
    }

    [Fact]
    public void ALinuxGameNamesTheFoldersOfItsLibraries()
    {
        var manifest = TestManifests.Parse(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["libraryPath"] = new JsonArray(".", "bin", "bin/linux64")));

        var plan = LaunchPlanRenderer.Render(manifest, ManifestValidator.LinuxX64, new ServerBinding("abcd1234", new Dictionary<string, string?>()));

        Assert.Equal([".", "bin", "bin/linux64"], plan.LibraryPath);
        Assert.Equal(ManifestValidator.LinuxX64, plan.Platform);
        Assert.Empty(LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234", new Dictionary<string, string?>())).LibraryPath);
    }

    [Fact]
    public void OnlyALinuxRuntimeHasALibraryPath()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["windows-x64"]!["libraryPath"] = new JsonArray("bin")));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.libraryPath");
    }

    [Theory]
    [InlineData("../elsewhere")]
    [InlineData("/usr/lib")]
    [InlineData("bin:/tmp")]
    [InlineData("bin;x")]
    [InlineData(".pulsar/steamcmd")]
    public void ALibraryFolderIsOneOfTheServersOwn(string folder)
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["libraryPath"] = new JsonArray(folder)));

        Assert.Contains(errors, e => e.Path.StartsWith("runtime.linux-x64.libraryPath", StringComparison.Ordinal));
    }

    [Fact]
    public void ALibraryPathNamesAFewFolders()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["libraryPath"] = new JsonArray()));

        Assert.Contains(errors, e => e.Path == "runtime.linux-x64.libraryPath");
    }

    [Fact]
    public void AnAddonOfALinuxGameNamesItsLinuxAsset()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("cs2", m => m["addons"]![0]!["source"]!.AsObject().Remove("linuxAsset")));

        Assert.Contains(errors, e => e.Path == "addons[0].source.linuxAsset");
    }

    [Fact]
    public void EachPlatformDownloadsItsOwnAsset()
    {
        var metamod = Assert.IsType<GitHubReleaseSource>(TestManifests.Load("cs2").AddonList.Single(a => a.Key == "metamod").Source);

        Assert.Matches(metamod.For(ManifestValidator.WindowsX64).Asset, "mmsource-2.0.0-git1491-windows.zip");
        Assert.Matches(metamod.For(ManifestValidator.LinuxX64).Asset, "mmsource-2.0.0-git1491-linux.tar.gz");
        Assert.DoesNotMatch(metamod.For(ManifestValidator.LinuxX64).Asset, "mmsource-2.0.0-git1491-windows.zip");
        Assert.Equal("tar.gz", ManifestFeatures.AddonFormat(metamod.For(ManifestValidator.LinuxX64)));
    }

    [Fact]
    public void ALinuxAssetIsAnArchiveToo()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("cs2", m => m["addons"]![0]!["source"]!["linuxAsset"] = "^mmsource-linux$"));

        Assert.Contains(errors, e => e.Path == "addons[0].source.linuxAsset");
    }

    [Fact]
    public void AnInstalledAddonsLoaderIsPreloadedOnLinuxOnly()
    {
        var manifest = TestManifests.Load("valheim");
        var binding = new ServerBinding("abcd1234", new Dictionary<string, string?> { ["password"] = "secret12" },
            new Dictionary<string, PortAssignment> { ["game"] = new(PortAssignment.AnyAddress, 2456), ["query"] = new(PortAssignment.AnyAddress, 2457) }, Directory: "/srv/x");

        var installed = LaunchPlanRenderer.Render(manifest, ManifestValidator.LinuxX64, binding with { Addons = ["bepinex"] });
        var without = LaunchPlanRenderer.Render(manifest, ManifestValidator.LinuxX64, binding);
        var windows = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding with { Addons = ["bepinex"] });

        Assert.Equal(["doorstop_libs/libdoorstop_x64.so"], installed.Preload);
        Assert.Equal("1", installed.Environment["DOORSTOP_ENABLED"]);
        Assert.Empty(without.Preload);
        Assert.False(without.Environment.ContainsKey("DOORSTOP_ENABLED"));
        Assert.Empty(windows.Preload);
        Assert.False(windows.Environment.ContainsKey("DOORSTOP_ENABLED"));
    }

    [Theory]
    [InlineData("""{"preload": ["../other/libx.so"]}""", "addons[0].linux.preload[0]")]
    [InlineData("""{"preload": ["/usr/lib/libx.so"]}""", "addons[0].linux.preload[0]")]
    [InlineData("""{"preload": ["libs/a.so:libs/b.so"]}""", "addons[0].linux.preload[0]")]
    [InlineData("""{"preload": ["libs/loader.dll"]}""", "addons[0].linux.preload[0]")]
    [InlineData("""{"environment": {"LD_PRELOAD": "/tmp/x.so"}}""", "addons[0].linux.environment.LD_PRELOAD")]
    [InlineData("""{"environment": {"HOME": "/root"}}""", "addons[0].linux.environment.HOME")]
    [InlineData("""{"environment": {"DOORSTOP_ENABLED": "{{var.world}}"}}""", "addons[0].linux.environment.DOORSTOP_ENABLED")]
    public void AnAddonsLinuxRunTakesOnlyTheServersLibrariesAndPlainVariables(string linux, string path)
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("valheim", m => m["addons"]![0]!["linux"] = JsonNode.Parse(linux)));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void ARuntimesOwnStopReplacesTheManifestsOnItsPlatform()
    {
        var manifest = TestManifests.Parse(TestManifests.ChangeWithLinux("unturned", m =>
            m["runtime"]!["linux-x64"]!["stop"] = JsonNode.Parse("""{"steps": [{"type": "ctrlC"}], "timeoutSeconds": 60}""")));
        var binding = new ServerBinding("abcd1234", new Dictionary<string, string?>(),
            manifest.PortList.ToDictionary(p => p.Name, p => new PortAssignment(PortAssignment.AnyAddress, p.Default)));

        Assert.IsType<CtrlCStop>(Assert.Single(LaunchPlanRenderer.Render(manifest, ManifestValidator.LinuxX64, binding).Stop.Steps));
        Assert.Equal("Shutdown", Assert.IsType<ConsoleCommandStop>(Assert.Single(LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding).Stop.Steps)).Command);
    }

    [Fact]
    public void ARuntimesOwnStopSuitsItsConsole()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("unturned", m =>
            m["runtime"]!["linux-x64"]!["stop"] = JsonNode.Parse("""{"steps": [{"type": "rconCommand", "command": "quit"}], "timeoutSeconds": 0}""")));

        Assert.Contains(errors, e => e.Path == "runtime.linux-x64.stop.timeoutSeconds");
        Assert.Contains(errors, e => e.Path.StartsWith("runtime.linux-x64.stop.steps[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void AStopTwoRuntimesShareIsReportedOnce()
    {
        // Counter-Strike: Source's two runtimes have the same console (log file and RCON): the manifest's stop is checked once.
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("css", m => m["stop"]!["timeoutSeconds"] = 0));

        Assert.Single(errors, e => e.Path == "stop.timeoutSeconds");
    }

    private static string WindowsOnly() => TestManifests.ChangeWithLinux("_test", m =>
    {
        m["platforms"] = new JsonArray("windows-x64");
        m["runtime"]!.AsObject().Remove("linux-x64");
    });
}
