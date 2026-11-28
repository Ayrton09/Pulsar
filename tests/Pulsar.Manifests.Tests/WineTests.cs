using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>Games whose dedicated server has no Linux build, run on Linux nodes with Wine (<c>runtime.linux-x64.wine</c>).</summary>
public sealed class WineTests
{
    [Fact]
    public void AWineGameRunsItsWindowsProgramOnLinux()
    {
        var manifest = TestManifests.Load("vrising");

        Assert.True(manifest.RunsOn(ManifestValidator.LinuxX64));
        Assert.True(manifest.RunsWithWine(ManifestValidator.LinuxX64));
        Assert.False(manifest.RunsWithWine(ManifestValidator.WindowsX64));
        Assert.Equal("VRisingServer.exe", manifest.Runtime[ManifestValidator.LinuxX64].Executable);
        Assert.True(ManifestFeatures.UsesWine(manifest, ManifestValidator.LinuxX64));
        Assert.False(ManifestFeatures.UsesWine(manifest, ManifestValidator.WindowsX64));
    }

    [Fact]
    public void ANativeLinuxGameDoesNotRunWithWine()
    {
        Assert.False(TestManifests.Load("valheim").RunsWithWine(ManifestValidator.LinuxX64));
    }

    [Fact]
    public void ThePlanSaysSoOnLinuxOnly()
    {
        var manifest = TestManifests.Load("vrising");
        var binding = new ServerBinding("abcd1234", new Dictionary<string, string?>(),
            manifest.PortList.ToDictionary(p => p.Name, p => new PortAssignment(PortAssignment.AnyAddress, p.Default)), RconPassword: "rcon-secret-1");

        var linux = LaunchPlanRenderer.Render(manifest, ManifestValidator.LinuxX64, binding);
        var windows = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding);

        Assert.True(linux.Wine);
        Assert.Equal("VRisingServer.exe", linux.Executable);
        Assert.Equal(windows.Arguments, linux.Arguments);
        Assert.False(windows.Wine);
    }

    [Fact]
    public void OnlyALinuxRuntimeRunsWithWine()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("vrising", m => m["runtime"]!["windows-x64"]!["wine"] = true));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.wine");
        Assert.False(SchemaTests.Validate(TestManifests.ChangeWithLinux("vrising", m => m["runtime"]!["windows-x64"]!["wine"] = true)).IsValid);
    }

    [Theory]
    [InlineData("VRisingServer")]
    [InlineData("start.sh")]
    [InlineData("VRisingServer.dll")]
    public void WineStartsAnExeOnly(string executable)
    {
        var json = TestManifests.ChangeWithLinux("vrising", m => m["runtime"]!["linux-x64"]!["executable"] = executable);

        Assert.Contains(TestManifests.Errors(json), e => e.Path == "runtime.linux-x64.executable");
        Assert.False(SchemaTests.Validate(json).IsValid);
    }

    [Fact]
    public void AnExeOnLinuxNeedsWine()
    {
        var json = TestManifests.ChangeWithLinux("vrising", m => m["runtime"]!["linux-x64"]!.AsObject().Remove("wine"));

        Assert.Contains(TestManifests.Errors(json), e => e.Path == "runtime.linux-x64.executable");
        Assert.False(SchemaTests.Validate(json).IsValid);
    }

    [Theory]
    [InlineData("libraryPath", """["bin"]""")]
    [InlineData("userProfile", "\"profile\"")]
    [InlineData("environment", """{"WINEPREFIX": "/tmp/prefix"}""")]
    public void TheAgentOwnsWhatWineNeeds(string member, string value)
    {
        var json = TestManifests.ChangeWithLinux("vrising", m => m["runtime"]!["linux-x64"]![member] = JsonNode.Parse(value));

        Assert.Contains(TestManifests.Errors(json), e => e.Path.StartsWith($"runtime.linux-x64.{member}", StringComparison.Ordinal));
        Assert.False(SchemaTests.Validate(json).IsValid);
    }

    [Fact]
    public void WinesOtherVariablesAreTheManifests()
    {
        var json = TestManifests.ChangeWithLinux("vrising", m => m["runtime"]!["linux-x64"]!["environment"] = JsonNode.Parse("""{"SteamAppId": "1604030", "WINEDEBUG": "err+all"}"""));

        Assert.Empty(TestManifests.ErrorsOrNone(json));
        Assert.True(SchemaTests.Validate(json).IsValid);
    }

    [Theory]
    [InlineData("""{"type": "steamcmd", "appId": 1829350, "windowsFirstOnLinux": true}""", "install.steps[0].windowsFirstOnLinux")]
    [InlineData("""{"type": "steamcmd", "appId": 1829350, "linuxAppId": 1829351}""", "install.steps[0].linuxAppId")]
    [InlineData("""{"type": "steamcmd", "appId": 1829350, "steamClient": "."}""", "install.steps[0].steamClient")]
    [InlineData("""{"type": "dotnetRuntime", "channel": "9.0"}""", "install.steps[0]")]
    [InlineData("""{"type": "githubRelease", "repo": "a/b", "asset": "^a\\.zip$", "linuxAsset": "^b\\.zip$"}""", "install.steps[0]")]
    public void ItInstallsWithSteamCmdAndTheFileStepsOnly(string step, string path)
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("vrising", m => m["install"]!["steps"]![0] = JsonNode.Parse(step)));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void ItHasNoAddons()
    {
        var errors = TestManifests.Errors(TestManifests.ChangeWithLinux("vrising", m => m["addons"] = JsonNode.Parse("""
            [{"key": "x1", "name": "X", "license": "MIT", "url": "https://example.com/", "target": "", "source": {"type": "githubRelease", "repo": "a/b", "asset": "^x\\.zip$"}}]
            """)));

        Assert.Contains(errors, e => e.Path == "addons");
    }

    [Fact]
    public void AWindowsNodeGetsTheManifestWithoutWine()
    {
        var json = File.ReadAllText(TestManifests.PathOf("vrising"));

        var windows = ManifestPlatforms.For(json, ManifestValidator.WindowsX64);
        var linux = ManifestPlatforms.For(json, ManifestValidator.LinuxX64);

        Assert.DoesNotContain("\"wine\"", windows, StringComparison.Ordinal);
        Assert.True(TestManifests.Parse(linux).RunsWithWine(ManifestValidator.LinuxX64));
    }
}
