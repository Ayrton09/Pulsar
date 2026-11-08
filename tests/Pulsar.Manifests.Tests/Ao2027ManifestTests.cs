using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>
/// AO2027 (Pulsar 1.0.35): a server installed from its GitHub release whose Server.ini an update keeps, its four ports
/// following the base one, and the web port it takes with Windows' HTTP Server API, reserved by the agent.
/// </summary>
public sealed class Ao2027ManifestTests
{
    [Fact]
    public void TheReleaseKeepsServerIniAndTheWebPortIsReserved()
    {
        var manifest = TestManifests.Load("ao2027");
        var release = manifest.Install!.Steps.OfType<GitHubReleaseStep>().Single();

        Assert.Equal("Ayrton09/AO-2027", release.Repo);
        Assert.Equal(release.Repo, manifest.Update!.Steps.OfType<GitHubReleaseStep>().Single().Repo);
        Assert.Equal(["CoverAO-win-x64/Server.ini"], release.KeepList);
        Assert.Equal(release.KeepList, manifest.Update!.Steps.OfType<GitHubReleaseStep>().Single().KeepList);
        Assert.Equal(["game"], manifest.PortList.Where(p => p.Http).Select(p => p.Name));
        Assert.True(ManifestFeatures.UsesHttpUrls(manifest));
        Assert.True(ManifestFeatures.UsesReleaseKeep(manifest));
        Assert.False(ManifestFeatures.UsesHttpUrls(TestManifests.Load("terraria")));
        Assert.False(ManifestFeatures.UsesReleaseKeep(TestManifests.Load("terraria")));
    }

    [Fact]
    public void ThePortsFollowTheBaseOneAndOnlyTheWebClientsIsOpen()
    {
        var manifest = TestManifests.Load("ao2027");
        var plan = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234", new Dictionary<string, string?>(),
            new Dictionary<string, PortAssignment>
            {
                ["base"] = new(PortAssignment.AnyAddress, 7700),
                ["websocket"] = new(PortAssignment.AnyAddress, 7701),
                ["admin"] = new(PortAssignment.AnyAddress, 7702),
                ["game"] = new(PortAssignment.AnyAddress, 7703),
            }, RconPassword: new string('r', 32), Directory: "D:/servers/abcd1234"));

        var open = Assert.Single(plan.Ports);
        Assert.Equal(("game", 7703, true), (open.Name, open.Port, open.Http));
        var ini = Assert.Single(plan.ConfigFiles);
        Assert.Equal(("CoverAO-win-x64/Server.ini", "INIT"), (ini.Path, ini.Section));
        Assert.Contains(ini.Entries, e => e.Key == "StartPort" && e.Value == "7700");
    }

    [Fact]
    public void AnHttpPortListensOverTcp()
    {
        var errors = TestManifests.Errors(TestManifests.Change("ao2027", m => m["ports"]![3]!["protocols"] = new JsonArray("udp")));

        Assert.Contains(errors, e => e.Path == "ports[3].http");
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""["CoverAO-win-x64/Server.ini", "coverao-win-x64/server.ini"]""")]
    [InlineData("""["../Server.ini"]""")]
    [InlineData("""[".pulsar/releases/x.json"]""")]
    public void KeptFilesAreFilesOfTheServersFolderOnce(string keep)
    {
        var errors = TestManifests.Errors(TestManifests.Change("ao2027", m => m["install"]!["steps"]![0]!["keep"] = JsonNode.Parse(keep)));

        Assert.Contains(errors, e => e.Path.StartsWith("install.steps[0].keep", StringComparison.Ordinal));
    }

    [Fact]
    public void AKeptFileIsInsideTheStepsTarget()
    {
        var errors = TestManifests.Errors(TestManifests.Change("ao2027", m => m["install"]!["steps"]![0]!["target"] = "server"));

        Assert.Contains(errors, e => e.Path == "install.steps[0].keep[0]");
    }

    [Fact]
    public void AtMostSixteenFilesAreKept()
    {
        var many = new JsonArray([.. Enumerable.Range(0, GitHubReleaseStep.MaxKeep + 1).Select(i => JsonValue.Create($"CoverAO-win-x64/file{i}.ini"))]);

        var errors = TestManifests.Errors(TestManifests.Change("ao2027", m => m["install"]!["steps"]![0]!["keep"] = many));

        Assert.Contains(errors, e => e.Path == "install.steps[0].keep");
    }
}
