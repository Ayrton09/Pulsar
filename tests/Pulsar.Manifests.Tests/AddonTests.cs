using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

public sealed class AddonTests
{
    private const string Metamod = """
        { "key": "metamod", "name": "Metamod:Source", "license": "zlib", "url": "https://www.metamodsource.net/",
          "source": { "type": "githubRelease", "repo": "alliedmodders/metamod-source", "asset": "^mmsource-.*-windows\\.zip$", "prerelease": true },
          "target": "game/csgo",
          "lineEdits": [ { "path": "game/csgo/gameinfo.gi", "after": "Game_LowViolence csgo_lv", "line": "Game\tcsgo/addons/metamod" } ] }
        """;

    private const string Sharp = """
        { "key": "counterstrikesharp", "name": "CounterStrikeSharp", "license": "GPL-3.0", "url": "https://docs.cssharp.dev/",
          "source": { "type": "githubRelease", "repo": "roflmuffin/CounterStrikeSharp", "asset": "^counterstrikesharp-with-runtime-windows-.*\\.zip$" },
          "target": "game/csgo", "requires": ["metamod"] }
        """;

    [Fact]
    public void AddonsAreReadAndTheirLinesFollowWhatIsInstalled()
    {
        var manifest = TestManifests.Parse(WithAddons(Metamod, Sharp));

        Assert.Equal(["metamod", "counterstrikesharp"], manifest.AddonList.Select(a => a.Key));
        Assert.Equal(["metamod"], manifest.AddonList[1].RequiresList);
        var binding = new ServerBinding("abcd1234", new Dictionary<string, string?>(),
            new Dictionary<string, PortAssignment> { ["game"] = new("0.0.0.0", 27015) }, RconPassword: "secret");
        Assert.Empty(LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding).LineEdits);
        Assert.Equal("Game\tcsgo/addons/metamod",
            Assert.Single(LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding with { Addons = ["metamod"] }).LineEdits).Line);
    }

    [Theory]
    [InlineData("requires", """["nothing"]""", "addons[1]")]
    [InlineData("conflicts", """["counterstrikesharp"]""", "addons[1]")]
    [InlineData("url", "\"http://insecure.example\"", "addons[1].url")]
    [InlineData("target", "\"../outside\"", "addons[1].target")]
    [InlineData("key", "\"Bad Key\"", "addons[1].key")]
    public void BadAddonsAreRefused(string property, string value, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("cs2", m =>
        {
            var sharp = JsonNode.Parse(Sharp)!.AsObject();
            sharp[property] = JsonNode.Parse(value);
            m["addons"] = new JsonArray(JsonNode.Parse(Metamod), sharp);
        }));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Theory]
    [InlineData("""{ "type": "githubRelease", "repo": "not a repo", "asset": "x" }""", "addons[0].source.repo")]
    [InlineData("""{ "type": "githubRelease", "repo": "a/b", "asset": "(a+)+$\\1" }""", "addons[0].source.asset")]
    [InlineData("""{ "type": "githubRelease", "repo": "a/b", "asset": "x", "tag": "v1", "prerelease": true }""", "addons[0].source.tag")]
    public void BadSourcesAreRefused(string source, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("cs2", m =>
        {
            var addon = JsonNode.Parse(Metamod)!.AsObject();
            addon["source"] = JsonNode.Parse(source);
            m["addons"] = new JsonArray(addon);
        }));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void ALineGoesAfterItsAnchorOnceWithItsIndentationAndComesOutAgain()
    {
        var edit = new LineEdit("game/csgo/gameinfo.gi", "Game_LowViolence csgo_lv", "Game\tcsgo/addons/metamod");
        const string gameinfo = "\t\tSearchPaths\r\n\t\t{\r\n\t\t\tGame_LowViolence\tcsgo_lv // Perfect World content override\r\n\t\t\tGame\tcsgo\r\n";

        var edited = LineEditor.Apply(gameinfo, edit);

        Assert.Equal("\t\tSearchPaths\r\n\t\t{\r\n\t\t\tGame_LowViolence\tcsgo_lv // Perfect World content override\r\n\t\t\tGame\tcsgo/addons/metamod\r\n\t\t\tGame\tcsgo\r\n",
            edited);
        Assert.Equal(edited, LineEditor.Apply(edited, edit with { Line = "Game  csgo/addons/metamod" })); // the same line, spaced differently
        Assert.Equal(gameinfo, LineEditor.Remove(edited, edit));
        Assert.Equal(gameinfo, LineEditor.Remove(gameinfo, edit));
        Assert.Throws<LaunchPlanException>(() => LineEditor.Apply("Game csgo\n", edit));
    }

    private static string WithAddons(params string[] addons) =>
        TestManifests.Change("cs2", m => m["addons"] = new JsonArray([.. addons.Select(a => JsonNode.Parse(a))]));
}
