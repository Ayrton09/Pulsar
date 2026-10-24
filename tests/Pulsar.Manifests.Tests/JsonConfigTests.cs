using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>
/// Pulsar 1.0.7's JSON configuration files (Enshrouded, V Rising, Arma Reforger): only the members the panel sets
/// change, the rest of the game's file stays as it was.
/// </summary>
public sealed class JsonConfigTests
{
    [Fact]
    public void PatchChangesOnlyTheDeclaredMembers()
    {
        const string existing = """
            {
                "name": "Old name",
                "slotCount": 16,
                "gameSettings": {
                    "enemyDamageFactor": 1.0,
                    "tombstoneMode": "AddBackpackMaterials"
                },
                "userGroups": [ { "name": "Admin", "password": "own" } ]
            }
            """;

        var written = ConfigFileWriter.Apply(Json(("/name", "Mine", ConfigValueKind.Text), ("/gameSettings/enemyDamageFactor", "2.5", ConfigValueKind.Number),
            ("/gameSettings/pacifyAllEnemies", "true", ConfigValueKind.Boolean)), existing);

        // The members keep their order and their own spelling (1.0 stays 1.0); new ones go at the end of their object; the
        // file keeps its four-space indentation.
        Assert.Equal("""
            {
                "name": "Mine",
                "slotCount": 16,
                "gameSettings": {
                    "enemyDamageFactor": 2.5,
                    "tombstoneMode": "AddBackpackMaterials",
                    "pacifyAllEnemies": true
                },
                "userGroups": [
                    {
                        "name": "Admin",
                        "password": "own"
                    }
                ]
            }

            """.ReplaceLineEndings("\n"), written);
    }

    [Fact]
    public void AMissingFileGetsTheMembersAndTheObjectsOnTheirWay()
    {
        var written = ConfigFileWriter.Apply(Json(("/game/name", "Mine", ConfigValueKind.Text), ("/game/maxPlayers", "32", ConfigValueKind.Number),
            ("/a2s/port", "17777", ConfigValueKind.Number)), null);

        Assert.Equal("{\n  \"game\": {\n    \"name\": \"Mine\",\n    \"maxPlayers\": 32\n  },\n  \"a2s\": {\n    \"port\": 17777\n  }\n}\n", written);
    }

    [Fact]
    public void AnEmptyValueRemovesTheMemberAndAddsNothingOnItsWay()
    {
        var written = ConfigFileWriter.Apply(Json(("/password", "", ConfigValueKind.Text), ("/missing/deeper", "", ConfigValueKind.Text)),
            "{\r\n\t\"password\": \"x\",\r\n\t\"name\": \"kept\"\r\n}\r\n");

        // Tabs and CRLF line ends stay too.
        Assert.Equal("{\r\n\t\"name\": \"kept\"\r\n}\r\n", written);
    }

    [Fact]
    public void TextIsEscapedAndKeepsItsLetters()
    {
        var written = ConfigFileWriter.Apply(Json(("/name", "Ñandú \"quoted\" back\\slash </script>", ConfigValueKind.Text)), "{}");

        Assert.Equal("Ñandú \"quoted\" back\\slash </script>", JsonNode.Parse(written)!["name"]!.GetValue<string>());
        Assert.Contains("Ñandú", written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("12x", ConfigValueKind.Number)]
    [InlineData("1e5", ConfigValueKind.Number)]
    [InlineData("yes", ConfigValueKind.Boolean)]
    [InlineData("True", ConfigValueKind.Boolean)]
    public void AValueThatIsNotItsKindIsRefused(string value, ConfigValueKind kind)
    {
        Assert.Throws<LaunchPlanException>(() => ConfigFileWriter.Apply(Json(("/value", value, kind)), "{}"));
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("{ \"a\": 1, }")] // trailing comma
    [InlineData("{ // a comment\n \"a\": 1 }")]
    [InlineData("{ \"a\": 1, \"a\": 2 }")] // a repeated member
    [InlineData("{ \"a\": ")]
    [InlineData("{ \"game\": \"not an object\" }")] // /game/name goes through a string
    [InlineData("{ \"game\": [] }")]
    public void AFileThatCannotBeKeptAsItIsIsNotOverwritten(string existing)
    {
        Assert.Throws<LaunchPlanException>(() => ConfigFileWriter.Apply(Json(("/game/name", "Mine", ConfigValueKind.Text)), existing));
    }

    [Fact]
    public void AByteOrderMarkIsReadAndNotWrittenBack()
    {
        Assert.Equal("{\n  \"a\": 2\n}\n", ConfigFileWriter.Apply(Json(("/a", "2", ConfigValueKind.Number)), "﻿{\"a\": 1}"));
    }

    [Fact]
    public void ALonePlaceholderGivesItsTypeToTheValue()
    {
        var manifest = TestManifests.Parse(WithJsonFile(new JsonObject
        {
            ["/port"] = "{{port.game}}",
            ["/delay"] = "{{var.readyAfterMs}}",
            ["/quiet"] = "{{var.ignoreQuit}}",
            ["/greeting"] = "{{var.message}}",
            ["/address"] = "{{port.game.ip}}:{{port.game}}",
            ["/literal"] = "16",
        }));

        var file = Assert.Single(LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234",
            new Dictionary<string, string?> { ["readyAfterMs"] = "250" }, new Dictionary<string, PortAssignment> { ["game"] = new("0.0.0.0", 27015) })).ConfigFiles);

        Assert.Equal(
            [("/port", ConfigValueKind.Number), ("/delay", ConfigValueKind.Number), ("/quiet", ConfigValueKind.Boolean), ("/greeting", ConfigValueKind.Text),
                ("/address", ConfigValueKind.Text), ("/literal", ConfigValueKind.Text)],
            file.Entries.Select(e => (e.Key, e.Kind)));
        var written = JsonNode.Parse(ConfigFileWriter.Apply(file, null))!;
        Assert.Equal(27015, written["port"]!.GetValue<int>());
        Assert.False(written["quiet"]!.GetValue<bool>());
        Assert.Equal("0.0.0.0:27015", written["address"]!.GetValue<string>());
        Assert.Equal("16", written["literal"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("name")] // not a pointer
    [InlineData("/")]
    [InlineData("/a//b")]
    [InlineData("/a/~1b")]
    [InlineData("/a b")]
    [InlineData("/1/2/3/4/5/6/7/8/9")] // nine deep
    public void AKeyIsAJsonPointerToAMember(string key)
    {
        var errors = TestManifests.Errors(WithJsonFile(new JsonObject { [key] = "{{var.message}}" }));

        Assert.Contains(errors, e => e.Path == $"configFiles[0].set.{key}");
    }

    [Fact]
    public void AJsonFileIsPatchedAndItsKeysDoNotSitInsideOneAnother()
    {
        Assert.Contains(TestManifests.Errors(WithJsonFile(new JsonObject { ["/a"] = "{{var.message}}" }, mode: "managed")),
            e => e.Path == "configFiles[0].mode");
        Assert.Contains(TestManifests.Errors(WithJsonFile(new JsonObject { ["/a"] = "{{var.message}}", ["/A/b"] = "{{var.message}}" })),
            e => e.Path == "configFiles[0].set");
        Assert.Contains(TestManifests.Errors(WithJsonFile(new JsonObject { ["/a"] = "{{var.message}}" }, section: "root")), e => e.Path == "configFiles[0]");
        Assert.NotNull(TestManifests.Parse(WithJsonFile(new JsonObject { ["/a/b"] = "{{var.message}}", ["/a/bc"] = "{{var.message}}", ["/x.y/z_1-2"] = "x" })));
    }

    [Fact]
    public void OnlyAgentsThatWriteJsonGetSuchAManifest()
    {
        var manifest = TestManifests.Parse(WithJsonFile(new JsonObject { ["/a"] = "{{var.message}}" }));

        Assert.True(ManifestFeatures.UsesJsonConfig(manifest));
        Assert.False(ManifestFeatures.UsesJsonConfig(TestManifests.Load("_test")));
    }

    private static RenderedConfigFile Json(params (string Key, string Value, ConfigValueKind Kind)[] entries) =>
        new("enshrouded_server.json", ConfigFileFormat.Json, ConfigFileMode.Patch,
            [.. entries.Select(e => new ConfigEntry(e.Key, e.Value, Secret: false) { Kind = e.Kind })]);

    /// <summary>The test game with one JSON file and a game port.</summary>
    private static string WithJsonFile(JsonObject set, string mode = "patch", string? section = null) => TestManifests.Change("_test", m =>
    {
        m["ports"] = new JsonArray(new JsonObject { ["name"] = "game", ["label"] = "Game", ["protocols"] = new JsonArray("udp"), ["default"] = 27015 });
        var file = new JsonObject { ["path"] = "data/server.json", ["format"] = "json", ["mode"] = mode, ["set"] = set };
        if (section is not null)
        {
            file["section"] = section;
        }

        m["configFiles"] = new JsonArray(file);
    });
}
