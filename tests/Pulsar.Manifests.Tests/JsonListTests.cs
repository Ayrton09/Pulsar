using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>
/// Pulsar 1.0.14's lists in JSON files (Arma Reforger's mods in <c>/game/mods</c>): the panel's ids are merged into the
/// game's array, so the objects users added by hand, and what they pinned on the panel's, stay.
/// </summary>
public sealed class JsonListTests
{
    private const string Mine = "5965550F24A0C152";
    private const string Other = "ABCDEF0123456789";
    private const string Former = "1111111111111111";
    private const string Hand = "2222222222222222";

    [Fact]
    public void ArmaReforgerListsItsWorkshopModsInItsServerFile()
    {
        var manifest = TestManifests.Load("arma-reforger");
        var variable = manifest.VariableList.Single(v => v.Key == "mods");

        Assert.Equal(new ReforgerWorkshopList(), variable.ReforgerWorkshop);
        Assert.Equal(",", variable.ListSeparator);
        Assert.True(ManifestFeatures.UsesCatalogLists(manifest));

        var plan = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234",
            new Dictionary<string, string?> { ["mods"] = $"{Mine},{Other},{Mine.ToLowerInvariant()}" },
            new Dictionary<string, PortAssignment> { ["game"] = new("0.0.0.0", 2001), ["query"] = new("0.0.0.0", 17777) }, Directory: "D:/servers/abcd1234"));

        // Each id once, as the panel's editor writes them.
        var list = Assert.Single(Assert.Single(plan.ConfigFiles).Lists);
        Assert.Equal(("/game/mods", "modId"), (list.ArrayPath, list.Member));
        Assert.Equal([Mine, Other], list.Ids);
    }

    [Fact]
    public void ThePanelsIdsAreMergedIntoTheArrayAndWhatUsersAddedStays()
    {
        const string existing = """
            {
              "game": {
                "name": "Mine",
                "mods": [
                  { "modId": "2222222222222222", "name": "By hand" },
                  { "modId": "1111111111111111", "name": "Taken out in the panel" },
                  { "modId": "5965550f24a0c152", "name": "Pinned", "version": "1.0.1" },
                  "not an object",
                  { "modId": "5965550F24A0C152" }
                ]
              }
            }
            """;

        var written = JsonNode.Parse(ConfigFileWriter.Apply(Listing(Mine, Other), existing,
            new Dictionary<string, IReadOnlyList<string>> { ["/game/mods"] = [Former, Mine] }))!;

        // The pinned object keeps its place and its version (its id written as the user did); the second object of the
        // same id goes; the new id is added at the end; the game's other members stay.
        Assert.Equal(
            """[{"modId":"2222222222222222","name":"By hand"},{"modId":"5965550f24a0c152","name":"Pinned","version":"1.0.1"},"not an object",{"modId":"ABCDEF0123456789"}]""",
            written["game"]!["mods"]!.ToJsonString());
        Assert.Equal("Mine", written["game"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void AnEmptyListTakesOutOnlyThePanelsOwnIds()
    {
        var existing = $$"""{ "game": { "mods": [ { "modId": "{{Hand}}" }, { "modId": "{{Mine}}" } ] } }""";

        var written = JsonNode.Parse(ConfigFileWriter.Apply(Listing(), existing, new Dictionary<string, IReadOnlyList<string>> { ["/game/mods"] = [Mine] }))!;

        Assert.Equal($$"""[{"modId":"{{Hand}}"}]""", written["game"]!["mods"]!.ToJsonString());
    }

    [Fact]
    public void NothingToListAddsNoArrayAndAListMakesItsWay()
    {
        Assert.Equal("{\n  \"game\": {\n    \"name\": \"x\"\n  }\n}\n", ConfigFileWriter.Apply(Listing(), """{ "game": { "name": "x" } }"""));

        Assert.Equal($$$"""{"game":{"mods":[{"modId":"{{{Mine}}}"}]}}""", JsonNode.Parse(ConfigFileWriter.Apply(Listing(Mine), null))!.ToJsonString());
    }

    [Theory]
    [InlineData("""{ "game": { "mods": { "modId": "x" } } }""")]
    [InlineData("""{ "game": "x" }""")]
    public void AnArrayThatIsNotOneIsNotOverwritten(string existing)
    {
        Assert.Throws<LaunchPlanException>(() => ConfigFileWriter.Apply(Listing(Mine), existing));
    }

    [Fact]
    public void AListIsAJsonFilesArrayOfAListVariable()
    {
        Assert.NotNull(TestManifests.Parse(WithList(new JsonObject { ["/game/mods"] = List("mods", "modId") })));

        Assert.Contains(TestManifests.Errors(WithList(new JsonObject { ["/game/mods"] = List("message", "modId") })),
            e => e.Path == "configFiles[0].lists./game/mods.variable"); // not a list
        Assert.Contains(TestManifests.Errors(WithList(new JsonObject { ["/game/mods"] = List("mods", "mod id") })),
            e => e.Path == "configFiles[0].lists./game/mods.key");
        Assert.Contains(TestManifests.Errors(WithList(new JsonObject { ["game/mods"] = List("mods", "modId") })),
            e => e.Path == "configFiles[0].lists.game/mods");
        // The array is the list's own: a key does not write it, into it or around it.
        foreach (var pointer in new[] { "/game/name", "/game/name/0", "/game" })
        {
            Assert.Contains(TestManifests.Errors(WithList(new JsonObject { [pointer] = List("mods", "modId") })),
                e => e.Path == $"configFiles[0].lists.{pointer}");
        }

        Assert.Contains(TestManifests.Errors(WithList(new JsonObject { ["/game/mods"] = List("mods", "modId") }, format: "properties")),
            e => e.Path == "configFiles[0].lists");
    }

    [Theory]
    [InlineData("""{ "key": "count", "label": "Count", "type": "integer", "reforgerWorkshop": {} }""", "variables[1].reforgerWorkshop")]
    [InlineData("""{ "key": "count", "label": "Count", "type": "string", "secret": true, "reforgerWorkshop": {} }""", "variables[1].reforgerWorkshop")]
    [InlineData("""{ "key": "count", "label": "Count", "type": "string", "reforgerWorkshop": { "separator": " " } }""", "variables[1].reforgerWorkshop.separator")]
    [InlineData("""{ "key": "count", "label": "Count", "type": "string", "curseForge": { "gameId": 1 }, "reforgerWorkshop": {} }""", "variables[1].reforgerWorkshop")]
    public void OnlyAStringVariableListsArmaReforgerMods(string variable, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("_test", m => m["variables"]!.AsArray().Insert(1, JsonNode.Parse(variable))));

        Assert.Contains(errors, e => e.Path == path);
    }

    private static RenderedConfigFile Listing(params string[] ids) =>
        new("server.json", ConfigFileFormat.Json, ConfigFileMode.Patch, []) { Lists = [new ConfigList("/game/mods", "modId", ids)] };

    private static JsonObject List(string variable, string key) => new() { ["variable"] = variable, ["key"] = key };

    /// <summary>The test game with a list variable and one file that sets the server's name and lists its mods.</summary>
    private static string WithList(JsonObject lists, string format = "json") => TestManifests.Change("_test", m =>
    {
        m["variables"]!.AsArray().Add(new JsonObject
        {
            ["key"] = "mods",
            ["label"] = "Mods",
            ["type"] = "string",
            ["default"] = "",
            ["reforgerWorkshop"] = new JsonObject(),
        });
        m["configFiles"] = new JsonArray(new JsonObject
        {
            ["path"] = "server.json",
            ["format"] = format,
            ["mode"] = "patch",
            ["set"] = new JsonObject { [format == "json" ? "/game/name" : "name"] = "{{var.message}}" },
            ["lists"] = lists,
        });
    });
}
