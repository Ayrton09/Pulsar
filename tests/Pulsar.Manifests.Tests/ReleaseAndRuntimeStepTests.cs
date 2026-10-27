using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>
/// A game server from a GitHub release, the .NET runtime it runs on in the server's folder, and a log named by a pattern
/// (Pulsar 1.0.12), through Terraria (TShock) and Space Engineers.
/// </summary>
public sealed class ReleaseAndRuntimeStepTests
{
    private static readonly Dictionary<string, PortAssignment> s_ports = new() { ["game"] = new(PortAssignment.AnyAddress, 7777) };

    [Fact]
    public void TerrariaInstallsTShocksReleaseAndItsRuntime()
    {
        var manifest = TestManifests.Load("terraria");
        var release = manifest.Install!.Steps.OfType<GitHubReleaseStep>().Single();
        var runtime = manifest.Install.Steps.OfType<DotnetRuntimeStep>().Single();

        Assert.Equal("Pryaxis/TShock", release.Repo);
        Assert.Equal("pryaxis_tshock", release.RecordKey);
        Assert.Equal("zip", ManifestFeatures.AddonFormat(release.Source));
        Assert.Equal("9.0", runtime.Channel);
        Assert.True(ManifestFeatures.UsesGitHubReleaseInstall(manifest));
        Assert.True(ManifestFeatures.UsesDotnetRuntime(manifest));
        Assert.False(ManifestFeatures.UsesGitHubReleaseInstall(TestManifests.Load("css")));
        Assert.False(ManifestFeatures.UsesDotnetRuntime(TestManifests.Load("css")));
    }

    [Fact]
    public void TheAgentPointsTheGameAtTheRuntimeInItsFolder()
    {
        var plan = Render(TestManifests.Load("terraria"));

        Assert.Equal("D:/servers/abcd1234/dotnet", plan.Environment[LaunchPlan.DotnetRootVariable]);
        Assert.Equal("D:/servers/abcd1234/dotnet", plan.Environment["DOTNET_ROOT_X64"]);
        Assert.DoesNotContain(Render(TestManifests.Load("css")).Environment.Keys, k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AManifestStillCannotNameDotnetVariablesItself()
    {
        var errors = TestManifests.Errors(TestManifests.Change("terraria",
            m => m["runtime"]!["windows-x64"]!["environment"] = new JsonObject { ["DOTNET_ROOT"] = "C:/elsewhere" }));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.environment.DOTNET_ROOT");
    }

    [Theory]
    [InlineData("repo", "Pryaxis")]
    [InlineData("repo", "../TShock")]
    [InlineData("asset", "^TShock$")]
    [InlineData("asset", @"^TShock\.rar$")]
    [InlineData("tag", "..")]
    [InlineData("target", ".pulsar/releases")]
    [InlineData("target", "../elsewhere")]
    [InlineData("target", "dotnet")]
    [InlineData("target", "dotnet/shared")]
    public void AReleaseStepIsCheckedAsAnAddonsReleaseIs(string field, string value)
    {
        var errors = TestManifests.Errors(TestManifests.Change("terraria", m => m["install"]!["steps"]![0]![field] = JsonValue.Create(value)));

        Assert.Contains(errors, e => e.Path == $"install.steps[0].{field}");
    }

    [Fact]
    public void ARepositoryIsInstalledByOneStep()
    {
        var errors = TestManifests.Errors(TestManifests.Change("terraria",
            m => m["install"]!["steps"]!.AsArray().Add(JsonNode.Parse("""{ "type": "githubRelease", "repo": "pryaxis/tshock", "asset": "^x\\.zip$" }"""))));

        Assert.Contains(errors, e => e.Path == "install.steps[0].repo");
    }

    [Theory]
    [InlineData("9")]
    [InlineData("9.1")]
    [InlineData("4.0")]
    [InlineData("9.0/../10.0")]
    [InlineData("")]
    public void ARuntimeChannelIsAMajorVersion(string channel)
    {
        var errors = TestManifests.Errors(TestManifests.Change("terraria", m => m["install"]!["steps"]![1]!["channel"] = channel));

        Assert.Contains(errors, e => e.Path == "install.steps[1].channel");
    }

    [Theory]
    [InlineData("8.0")]
    [InlineData("10.0")]
    public void ChannelsOfOtherVersionsAreTaken(string channel)
    {
        var manifest = TestManifests.Parse(TestManifests.Change("terraria", m => m["install"]!["steps"]![1]!["channel"] = channel));

        Assert.Equal(channel, manifest.Install!.Steps.OfType<DotnetRuntimeStep>().Single().Channel);
    }

    [Fact]
    public void OneRuntimeStep()
    {
        var errors = TestManifests.Errors(TestManifests.Change("terraria",
            m => m["install"]!["steps"]!.AsArray().Add(JsonNode.Parse("""{ "type": "dotnetRuntime", "channel": "10.0" }"""))));

        Assert.Contains(errors, e => e.Path == "install.steps[1]");
    }

    [Fact]
    public void SpaceEngineersFollowsTheNewestLogItsPatternMatches()
    {
        var manifest = TestManifests.Load("space-engineers");

        Assert.Equal("instance/SpaceEngineersDedicated_*.log", manifest.Runtime[ManifestValidator.WindowsX64].Console.LogFile);
        Assert.True(ManifestValidator.IsLogFilePattern("instance/SpaceEngineersDedicated_*.log"));
        Assert.False(ManifestValidator.IsLogFilePattern("KFGame/Logs/Launch.log"));
        Assert.True(ManifestFeatures.UsesLogFileConsole(manifest));
    }

    [Theory]
    [InlineData("instance/*/server.log")]
    [InlineData("instance/a*b*.log")]
    [InlineData("../logs/*.log")]
    [InlineData(".pulsar/*.log")]
    public void ALogPatternHasOneStarInItsFileName(string logFile)
    {
        var errors = TestManifests.Errors(TestManifests.Change("space-engineers", m => m["runtime"]!["windows-x64"]!["console"]!["logFile"] = logFile));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.console.logFile");
    }

    [Fact]
    public void ALogPatternGoesOnlyToAgentsThatFollowOne()
    {
        // A pattern in logfile+rcon mode, which agents before 1.0.12 read only by its name.
        var manifest = TestManifests.Load("rust");
        var console = manifest.Runtime[ManifestValidator.WindowsX64].Console;
        Assert.Equal(ManifestConsoleMode.LogfileRcon, console.Mode);
        var patterned = manifest with
        {
            Runtime = new Dictionary<string, RuntimeDefinition>
            {
                [ManifestValidator.WindowsX64] = manifest.Runtime[ManifestValidator.WindowsX64] with { Console = console with { LogFile = "logs/server_*.log" } },
            },
        };

        Assert.False(ManifestFeatures.UsesLogFileConsole(manifest));
        Assert.True(ManifestFeatures.UsesLogFileConsole(patterned));
    }

    private static LaunchPlan Render(GameManifest manifest) =>
        LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64,
            new ServerBinding("abcd1234", new Dictionary<string, string?>(), manifest.PortList.ToDictionary(p => p.Name, p => new PortAssignment(PortAssignment.AnyAddress, p.Default)),
                RconPassword: new string('r', 32), Directory: "D:/servers/abcd1234"));
}
