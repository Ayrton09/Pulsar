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
        Assert.Equal(new QueryEndpoint(QueryProtocol.CoverAo, PortAssignment.AnyAddress, 7703), plan.Query); // the app's port
    }

    [Fact]
    public void ItsPlayersAreAskedOverHttpByANewerAgent()
    {
        var manifest = TestManifests.Load("ao2027");

        Assert.Equal(new QueryDefinition(QueryProtocol.CoverAo, "game"), manifest.Query);
        Assert.True(ManifestFeatures.UsesCoverAoQuery(manifest));
        Assert.False(ManifestFeatures.UsesCoverAoQuery(TestManifests.Load("minecraft")));
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

    [Fact]
    public void AGivenDatabaseReachesTheGameThroughItsEnvironmentOnly()
    {
        var manifest = TestManifests.Load("ao2027");
        Assert.Equal("postgres", manifest.Database!.Engine);
        Assert.True(ManifestFeatures.UsesGameDatabase(manifest));
        Assert.False(ManifestFeatures.UsesGameDatabase(TestManifests.Load("terraria")));
        var ports = new Dictionary<string, PortAssignment>
        {
            ["base"] = new(PortAssignment.AnyAddress, 7700),
            ["websocket"] = new(PortAssignment.AnyAddress, 7701),
            ["admin"] = new(PortAssignment.AnyAddress, 7702),
            ["game"] = new(PortAssignment.AnyAddress, 7703),
        };
        var binding = new ServerBinding("abcd1234", new Dictionary<string, string?>(), ports, Directory: "D:/servers/abcd1234");

        // Without one, the game keeps its own files: nothing about a database.
        var own = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding);
        Assert.DoesNotContain(own.Environment.Keys, k => k.StartsWith("COVERAO_BASEDEDATOS", StringComparison.Ordinal));

        const string Password = "Pw7GameDatabase24chars0";
        var given = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64,
            binding with { Database = new GameDatabaseBinding("127.0.0.1", 5432, "sabcd1234_game", "sabcd1234_game", Password) });
        Assert.Equal("postgres", given.Environment["COVERAO_BASEDEDATOS_TIPO"]);
        Assert.Equal("127.0.0.1", given.Environment["COVERAO_BASEDEDATOS_SERVIDOR"]);
        Assert.Equal("5432", given.Environment["COVERAO_BASEDEDATOS_PUERTO"]);
        Assert.Equal("sabcd1234_game", given.Environment["COVERAO_BASEDEDATOS_BASE"]);
        Assert.Equal("sabcd1234_game", given.Environment["COVERAO_BASEDEDATOS_USUARIO"]);
        Assert.Equal(Password, given.Environment["COVERAO_BASEDEDATOS_CLAVE"]);
        // Not in its files, its arguments or the console.
        Assert.DoesNotContain(given.ConfigFiles.SelectMany(f => f.Entries), e => e.Value.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(given.Arguments, a => a.Contains(Password, StringComparison.Ordinal));
        Assert.Contains(Password, given.Secrets);
        Assert.DoesNotContain(Password, given.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, new GameDatabaseBinding("h", 1, "n", "u", Password).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "engine": "oracle", "environment": { "DB": "{{database.name}}" } }""", "database.engine")]
    [InlineData("""{ "engine": "mysql", "environment": { "DB": "{{database.name}}" } }""", "database.engine")]
    [InlineData("""{ "engine": "postgres", "environment": {} }""", "database.environment")]
    [InlineData("""{ "engine": "postgres", "environment": { "DB": "{{var.maxUsers}}" } }""", "database.environment.DB")]
    [InlineData("""{ "engine": "postgres", "environment": { "DB": "{{database.secret}}" } }""", "database.environment.DB")]
    [InlineData("""{ "engine": "postgres", "environment": { "DB": "{{database.name" } }""", "database.environment.DB")]
    [InlineData("""{ "engine": "postgres", "environment": { "PATH": "{{database.name}}" } }""", "database.environment.PATH")]
    [InlineData("""{ "engine": "postgres", "environment": { "DOTNET_STARTUP_HOOKS": "{{database.name}}" } }""", "database.environment.DOTNET_STARTUP_HOOKS")]
    [InlineData("""{ "engine": "postgres", "environment": { "DB": "a", "db": "b" } }""", "database.environment")]
    public void TheDatabaseSectionIsChecked(string database, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("ao2027", m => m["database"] = JsonNode.Parse(database)));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void TheDatabasesPlaceholdersGoNowhereElseAndItsNamesAreItsOwn()
    {
        var inArguments = TestManifests.Errors(TestManifests.Change("ao2027",
            m => m["runtime"]!["windows-x64"]!["arguments"] = new JsonArray("--db={{database.password}}")));
        Assert.Contains(inArguments, e => e.Path.StartsWith("runtime.windows-x64.arguments", StringComparison.Ordinal));

        var clash = TestManifests.Errors(TestManifests.Change("ao2027",
            m => m["runtime"]!["windows-x64"]!["environment"] = new JsonObject { ["COVERAO_BASEDEDATOS_TIPO"] = "sqlite" }));
        Assert.Contains(clash, e => e.Path == "database.environment.COVERAO_BASEDEDATOS_TIPO");
    }
}
