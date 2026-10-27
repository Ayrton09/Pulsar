using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

public sealed class WorkshopTests
{
    [Fact]
    public void ProjectZomboidListsWorkshopItemsOfTheGame()
    {
        var variable = TestManifests.Load("project-zomboid").VariableList.Single(v => v.Key == "workshopItems");

        Assert.Equal(new WorkshopList(108600), variable.Workshop);
        Assert.Equal(";", variable.Workshop!.Separator);
    }

    [Theory]
    [InlineData("memoryGb", """{ "appId": 108600 }""", "variables[4].workshop")] // an integer
    [InlineData("workshopItems", """{ "appId": 0 }""", "variables[6].workshop.appId")]
    [InlineData("workshopItems", """{ "appId": 108600, "separator": " " }""", "variables[6].workshop.separator")]
    public void OnlyStringVariablesListWorkshopItems(string key, string workshop, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("project-zomboid", m =>
        {
            var variables = m["variables"]!.AsArray();
            variables.Single(v => (string?)v!["key"] == key)!["workshop"] = JsonNode.Parse(workshop);
        }));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void ArkSurvivalAscendedListsCurseForgeModsOfTheGame()
    {
        var manifest = TestManifests.Load("ark-sa");
        var variable = manifest.VariableList.Single(v => v.Key == "mods");

        Assert.Equal(new CurseForgeList(83374), variable.CurseForge);
        Assert.Equal(",", variable.CurseForge!.Separator);
        Assert.True(ManifestFeatures.UsesCatalogLists(manifest));
        Assert.False(ManifestFeatures.UsesCatalogLists(TestManifests.Load("project-zomboid")));
    }

    [Theory]
    [InlineData("""{ "gameId": 0 }""", "curseForge.gameId")]
    [InlineData("""{ "gameId": 83374, "separator": " " }""", "curseForge.separator")]
    public void ACurseForgeListNamesItsGame(string curseForge, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("ark-sa", m =>
            m["variables"]!.AsArray().Single(v => (string?)v!["key"] == "mods")!["curseForge"] = JsonNode.Parse(curseForge)));

        Assert.Contains(errors, e => e.Path.EndsWith(path, StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyAStringVariableThatListsNoWorkshopItemsListsCurseForgeProjects()
    {
        var errors = TestManifests.Errors(TestManifests.Change("ark-sa", m =>
            m["variables"]!.AsArray().Single(v => (string?)v!["key"] == "mods")!["workshop"] = JsonNode.Parse("""{ "appId": 2399830 }""")));

        Assert.Contains(errors, e => e.Path.EndsWith(".curseForge", StringComparison.Ordinal));
    }
}
