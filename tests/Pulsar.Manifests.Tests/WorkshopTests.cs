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
}
