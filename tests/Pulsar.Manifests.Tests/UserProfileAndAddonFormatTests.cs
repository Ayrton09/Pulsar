using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>A game's user folders in the server's folder and addons that are .tar.gz files (Pulsar 1.0.12), through SCP: Secret Laboratory.</summary>
public sealed class UserProfileAndAddonFormatTests
{
    [Fact]
    public void ThePlanCarriesTheFolderTheGameTakesForItsUsers()
    {
        var plan = LaunchPlanRenderer.Render(TestManifests.Load("scpsl"), ManifestValidator.WindowsX64,
            new ServerBinding("abcd1234", new Dictionary<string, string?> { ["eula"] = "accepted" },
                new Dictionary<string, PortAssignment> { ["game"] = new(PortAssignment.AnyAddress, 7777) }, Directory: "D:/servers/abcd1234"));

        Assert.Equal("profile", plan.UserProfile);
        Assert.True(ManifestFeatures.UsesUserProfile(TestManifests.Load("scpsl")));
        Assert.False(ManifestFeatures.UsesUserProfile(TestManifests.Load("css")));
    }

    [Theory]
    [InlineData(".pulsar")]
    [InlineData("../elsewhere")]
    [InlineData("C:/Users/Public")]
    public void TheFolderIsTheServersOwn(string folder)
    {
        var errors = TestManifests.Errors(TestManifests.Change("scpsl", m => m["runtime"]!["windows-x64"]!["userProfile"] = folder));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.userProfile");
    }

    [Fact]
    public void AnAddonsAssetSaysWhichArchiveItIs()
    {
        var exiled = (GitHubReleaseSource)TestManifests.Load("scpsl").AddonList.Single(a => a.Key == "exiled").Source;

        Assert.Equal("tar.gz", ManifestFeatures.AddonFormat(exiled));
        Assert.Equal("zip", ManifestFeatures.AddonFormat(new GitHubReleaseSource("OxideMod/Oxide.Rust", @"^Oxide\.Rust\.zip$")));
        Assert.True(ManifestFeatures.UsesTarAddons(TestManifests.Load("scpsl")));
        Assert.False(ManifestFeatures.UsesTarAddons(TestManifests.Load("rust")));
    }

    [Theory]
    [InlineData("^Exiled$")]
    [InlineData(@"^Exiled\.rar$")]
    [InlineData(@"^Exiled\.tar\.gz")]
    public void AnAssetOfAnUnknownArchiveIsRefused(string asset)
    {
        var errors = TestManifests.Errors(TestManifests.Change("scpsl", m => m["addons"]![0]!["source"]!["asset"] = JsonValue.Create(asset)));

        Assert.Contains(errors, e => e.Path == "addons[0].source.asset");
    }
}
