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
    public void LinuxHasNoPseudoConsole()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m => m["runtime"]!["linux-x64"]!["console"]!["mode"] = "pty"));

        Assert.Contains(errors, e => e.Path == "runtime.linux-x64.console.mode");
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
    public void StepsThatDownloadWindowsProgramsAreRefusedOnLinux()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("_test", m =>
            m["install"]!["steps"]!.AsArray().Add(new JsonObject { ["type"] = "dotnetRuntime", ["channel"] = "9.0" })));

        Assert.Contains(errors, e => e.Path == "install.steps[2]" && e.Message.Contains("linux-x64", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameStepsStayValidForWindowsOnly()
    {
        var errors = TestManifests.ErrorsOrNone(TestManifests.ChangeWithLinux("_test", m =>
        {
            m["platforms"] = new JsonArray("windows-x64");
            m["runtime"]!.AsObject().Remove("linux-x64");
            m["install"]!["steps"]!.AsArray().Add(new JsonObject { ["type"] = "dotnetRuntime", ["channel"] = "9.0" });
        }));

        Assert.DoesNotContain(errors, e => e.Path == "install.steps[2]");
    }

    private static string WindowsOnly() => TestManifests.ChangeWithLinux("_test", m =>
    {
        m["platforms"] = new JsonArray("windows-x64");
        m["runtime"]!.AsObject().Remove("linux-x64");
    });
}
