using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>The manifest each node is sent: its own platform's, and on Windows nothing an agent before Linux would refuse.</summary>
public sealed class ManifestPlatformsTests
{
    // Members the manifests gained with the Linux agent: an agent released before it refuses a manifest that has one.
    private static readonly string[] s_linuxMembers = ["libraryPath", "linuxAsset", "windowsFirstOnLinux", "preload"];

    public static TheoryData<string> Games() => [.. Directory.GetDirectories(Path.Combine(TestManifests.RepositoryRoot(), "games")).Select(d => Path.GetFileName(d))];

    [Theory]
    [MemberData(nameof(Games))]
    public void AWindowsNodeGetsTheManifestAsBeforeLinux(string game)
    {
        var text = File.ReadAllText(TestManifests.PathOf(game));
        var manifest = TestManifests.Parse(text);
        if (!manifest.RunsOn(ManifestValidator.WindowsX64))
        {
            return;
        }

        var windows = ManifestPlatforms.For(text, ManifestValidator.WindowsX64);
        var node = JsonNode.Parse(windows)!;

        Assert.Equal([ManifestValidator.WindowsX64], TestManifests.Parse(windows).Platforms);
        Assert.DoesNotContain(ManifestValidator.LinuxX64, windows, StringComparison.Ordinal);
        Assert.All(s_linuxMembers, member => Assert.DoesNotContain($"\"{member}\"", windows, StringComparison.Ordinal));
        Assert.All((node["addons"] as JsonArray ?? []).OfType<JsonObject>(), addon => Assert.False(addon.ContainsKey("linux")));
        Assert.All((node["configFiles"] as JsonArray ?? []).OfType<JsonObject>(), file => Assert.False(file.ContainsKey("platforms")));
        Assert.Null(node["runtime"]![ManifestValidator.WindowsX64]!["stop"]);
    }

    [Fact]
    public void ALinuxNodeGetsItsRuntimeAndItsOwnFiles()
    {
        var text = File.ReadAllText(TestManifests.PathOf("palworld"));

        var linux = TestManifests.Parse(ManifestPlatforms.For(text, ManifestValidator.LinuxX64));
        var windows = TestManifests.Parse(ManifestPlatforms.For(text, ManifestValidator.WindowsX64));

        Assert.Equal([ManifestValidator.LinuxX64], linux.Runtime.Keys);
        Assert.Equal("Pal/Saved/Config/LinuxServer/PalWorldSettings.ini", Assert.Single(linux.ConfigFileList).Path);
        Assert.Equal("Pal/Saved/Config/WindowsServer/PalWorldSettings.ini", Assert.Single(windows.ConfigFileList).Path);
    }

    [Fact]
    public void ALinuxNodeKeepsTheLinuxMembers()
    {
        var linux = TestManifests.Parse(ManifestPlatforms.For(File.ReadAllText(TestManifests.PathOf("valheim")), ManifestValidator.LinuxX64));

        Assert.NotNull(Assert.Single(linux.AddonList).Linux);
        Assert.Equal(["linux64"], linux.Runtime[ManifestValidator.LinuxX64].LibraryPath);

        var terraria = TestManifests.Parse(ManifestPlatforms.For(File.ReadAllText(TestManifests.PathOf("terraria")), ManifestValidator.LinuxX64));
        var release = Assert.IsType<GitHubReleaseStep>(terraria.Install!.Steps[0]);
        Assert.Equal("^TShock-.*-linux-x64-Release\\.zip$", release.Source.For(ManifestValidator.LinuxX64).Asset);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"runtime": {"windows-x64": {}}}""")]
    [InlineData("""{"runtime": {"linux-x64": {}}, "configFiles": [{"platforms": [1]}]}""")]
    public void WhatIsNotAManifestForThePlatformIsLeftAsItIs(string text)
    {
        Assert.Equal(text, ManifestPlatforms.For(text, ManifestValidator.LinuxX64));
    }

    [Fact]
    public void BothConfigFilesOfAGameAreItsBackupsToo()
    {
        var palworld = TestManifests.Load("palworld");

        Assert.All(palworld.ConfigFileList, file => Assert.Contains(file.Path, palworld.Backup!.Include!));
    }
}
