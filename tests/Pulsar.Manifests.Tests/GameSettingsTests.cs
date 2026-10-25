using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>
/// Pulsar 1.0.5's game settings: number variables (the multipliers games take), options left unset that change nothing
/// in the game's files, and conditions on any variable.
/// </summary>
public sealed class GameSettingsTests
{
    private static readonly VariableDefinition s_rate = new("rate", "Rate", VariableType.Number, Min: 0.1m, Max: 100m);

    [Theory]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("0.1")]
    [InlineData("100")]
    [InlineData("99.999999")]
    [InlineData("+2")]
    public void ANumberIsAPlainDecimalWithinItsRange(string value)
    {
        Assert.Null(VariableValues.Check(s_rate, value));
    }

    [Theory]
    [InlineData("0.05")] // below
    [InlineData("100.5")] // above
    [InlineData("1e2")]
    [InlineData("1,5")]
    [InlineData(" 1.5")]
    [InlineData("1.5 ")]
    [InlineData("0x10")]
    [InlineData("1.1234567")] // seven decimals
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("١")] // not an ASCII digit
    [InlineData("1_000")]
    public void ANumberIsNothingElse(string value)
    {
        Assert.NotNull(VariableValues.Check(s_rate, value));
    }

    [Theory]
    [InlineData("+2.50", "2.50")]
    [InlineData("007.5", "7.5")]
    [InlineData("1", "1")]
    public void ANumberReachesTheGameInItsPlainForm(string value, string rendered)
    {
        var manifest = TestManifests.Parse(TestManifests.Change("_test", m =>
        {
            m["variables"]!.AsArray().Add(new JsonObject { ["key"] = "rate", ["label"] = "Rate", ["type"] = "number", ["min"] = 0.1, ["max"] = 100 });
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("--rate");
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("{{var.rate}}");
        }));

        var plan = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234", new Dictionary<string, string?> { ["rate"] = value }));

        Assert.Equal(["--rate", rendered], plan.Arguments.Skip(plan.Arguments.Count - 2));
    }

    [Theory]
    [InlineData("number", null, 10.0, "variables[5]")] // no min
    [InlineData("number", 0.1234567, 10.0, "variables[5]")] // seven decimals
    [InlineData("number", 10.0, 1.0, "variables[5]")] // min > max
    [InlineData("integer", 0.5, 10.0, "variables[5]")] // an integer's bounds are whole
    public void NumberAndIntegerBoundsAreChecked(string type, double? min, double max, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("_test", m =>
        {
            var variable = new JsonObject { ["key"] = "rate", ["label"] = "Rate", ["type"] = type, ["max"] = max };
            if (min is { } value)
            {
                variable["min"] = value;
            }

            m["variables"]!.AsArray().Add(variable);
        }));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void AnOptionLeftUnsetLeavesItsLineAsTheFileHasIt()
    {
        var manifest = TestManifests.Parse(WithFriendlyFire(keep: true));
        const string existing = "hostname \"Mine\"\nmp_friendlyfire 1\nbot_quota 5\nsv_lan 0\n";

        var unset = Config(manifest, new Dictionary<string, string?>());
        var set = Config(manifest, new Dictionary<string, string?> { ["friendlyFire"] = "0" });

        // Not written at all: the admin's own value stays. A key not kept when empty is removed (bot_quota); what the
        // manifest does not declare stays (sv_lan).
        Assert.DoesNotContain(unset.Entries, e => e.Key == "mp_friendlyfire");
        Assert.Equal("hostname \"hello\"\nmp_friendlyfire 1\nsv_lan 0\n", ConfigFileWriter.Apply(unset, existing));
        Assert.Equal("hostname \"hello\"\nmp_friendlyfire \"0\"\nsv_lan 0\n", ConfigFileWriter.Apply(set, existing));
    }

    [Fact]
    public void AKeyNotKeptWhenEmptyIsRemovedAsBefore()
    {
        var manifest = TestManifests.Parse(WithFriendlyFire(keep: false));

        var unset = Config(manifest, new Dictionary<string, string?>());

        Assert.Equal("hostname \"hello\"\n", ConfigFileWriter.Apply(unset, "mp_friendlyfire 1\n"));
    }

    [Fact]
    public void OnlyKeysOfTheFileCanBeKeptWhenEmpty()
    {
        var errors = TestManifests.Errors(TestManifests.Change("_test", m =>
        {
            m["configFiles"] = new JsonArray(new JsonObject
            {
                ["path"] = "data/server.cfg",
                ["format"] = "source-cfg",
                ["mode"] = "patch",
                ["set"] = new JsonObject { ["hostname"] = "{{var.message}}" },
                ["keepIfEmpty"] = new JsonArray("mp_friendlyfire"),
            });
        }));

        Assert.Contains(errors, e => e.Path == "configFiles[0].keepIfEmpty");
    }

    [Fact]
    public void AConditionOnAChoiceHoldsWhenOneIsMade()
    {
        var manifest = TestManifests.Parse(TestManifests.Change("_test", m =>
        {
            m["variables"]!.AsArray().Add(new JsonObject
            {
                ["key"] = "combat",
                ["label"] = "Combat",
                ["type"] = "enum",
                ["options"] = new JsonArray(new JsonObject { ["value"] = "hard", ["label"] = "Hard" }, new JsonObject { ["value"] = "easy", ["label"] = "Easy" }),
            });
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add(new JsonObject
            {
                ["if"] = "var.combat",
                ["then"] = new JsonArray("-modifier", "combat", "{{var.combat}}"),
            });
        }));

        var unset = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234", new Dictionary<string, string?>()));
        var hard = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64,
            new ServerBinding("abcd1234", new Dictionary<string, string?> { ["combat"] = "hard" }));

        Assert.DoesNotContain("-modifier", unset.Arguments);
        Assert.Equal(["-modifier", "combat", "hard"], hard.Arguments.Skip(hard.Arguments.Count - 3));
        Assert.True(ManifestFeatures.UsesGameSettings(manifest));
    }

    [Fact]
    public void OlderAgentsAreToldApartByWhatAManifestUses()
    {
        Assert.True(ManifestFeatures.UsesMinecraftBedrock(TestManifests.Load("minecraft-bedrock")));
        Assert.False(ManifestFeatures.UsesMinecraftBedrock(TestManifests.Load("minecraft")));
        Assert.False(ManifestFeatures.UsesGameSettings(TestManifests.Load("_test")));
        Assert.True(ManifestFeatures.UsesGameSettings(TestManifests.Parse(WithFriendlyFire(keep: true))));
        Assert.True(ManifestFeatures.UsesGameSettings(TestManifests.Parse(TestManifests.Change("_test", m =>
            m["variables"]!.AsArray().Add(new JsonObject { ["key"] = "rate", ["label"] = "Rate", ["type"] = "number", ["min"] = 0, ["max"] = 2 })))));
        // The colon format (SCP: Secret Laboratory) is new too, without any other of them.
        Assert.True(ManifestFeatures.UsesGameSettings(TestManifests.Parse(TestManifests.Change("_test", m => m["configFiles"] = new JsonArray(new JsonObject
        {
            ["path"] = "data/config.txt",
            ["format"] = "colon",
            ["mode"] = "patch",
            ["set"] = new JsonObject { ["server_name"] = "{{var.message}}" },
        })))));
        // A condition on a boolean or a string is what every agent reads.
        Assert.False(ManifestFeatures.UsesGameSettings(TestManifests.Parse(WithFriendlyFire(keep: false))));
    }

    /// <summary>The test game with a Source-style configuration file: its greeting as the hostname, friendly fire and bots.</summary>
    private static string WithFriendlyFire(bool keep) => TestManifests.Change("_test", m =>
    {
        m["variables"]!.AsArray().Add(new JsonObject
        {
            ["key"] = "friendlyFire",
            ["label"] = "Friendly fire",
            ["type"] = "enum",
            ["options"] = new JsonArray(new JsonObject { ["value"] = "1", ["label"] = "On" }, new JsonObject { ["value"] = "0", ["label"] = "Off" }),
        });
        m["variables"]!.AsArray().Add(new JsonObject { ["key"] = "bots", ["label"] = "Bots", ["type"] = "integer", ["min"] = 0, ["max"] = 32 });
        var file = new JsonObject
        {
            ["path"] = "data/server.cfg",
            ["format"] = "source-cfg",
            ["mode"] = "patch",
            ["set"] = new JsonObject { ["hostname"] = "{{var.message}}", ["mp_friendlyfire"] = "{{var.friendlyFire}}", ["bot_quota"] = "{{var.bots}}" },
        };
        if (keep)
        {
            file["keepIfEmpty"] = new JsonArray("mp_friendlyfire");
        }

        m["configFiles"] = new JsonArray(file);
    });

    private static RenderedConfigFile Config(GameManifest manifest, IReadOnlyDictionary<string, string?> values) =>
        Assert.Single(LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234", values)).ConfigFiles);
}
