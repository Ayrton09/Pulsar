using System.Globalization;

namespace Pulsar.Manifests.Tests;

/// <summary>
/// The bundled games' settings as their admins expect them (docs/architecture/04, "Configuration files"): Pulsar patches
/// a game's files, never writes them anew, an option left unset in the panel leaves its line as the file has it, and an
/// option that is set reaches the game.
/// </summary>
public sealed class BundledGameSettingsTests
{
    private const string Rcon = "Rc0nPassw0rdRc0nPassw0rdRc0nPass";

    public static TheoryData<string> Games() =>
    [
        .. Directory.EnumerateDirectories(Path.Combine(TestManifests.RepositoryRoot(), "games"))
            .Select(Path.GetFileName)
            .Where(name => name is not null && name != "_test" && File.Exists(TestManifests.PathOf(name)))
            .Select(name => name!),
    ];

    [Theory]
    [MemberData(nameof(Games))]
    public void EveryConfigurationFileIsPatchedNeverWrittenAnew(string game)
    {
        Assert.All(TestManifests.Load(game).ConfigFileList, file => Assert.Equal(ConfigFileMode.Patch, file.Mode));
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AnOptionLeftUnsetChangesNoLineAndOneThatIsSetReachesTheGame(string game)
    {
        var manifest = TestManifests.Load(game);
        var required = manifest.VariableList.Where(v => v.Required && v.Default is null)
            .ToDictionary(v => v.Key, v => (string?)Sample(v), StringComparer.Ordinal);
        var unset = Render(manifest, required);

        var options = manifest.VariableList.Where(v => !v.Required && !v.Secret && (v.Default is null || v.Type == VariableType.Boolean)).ToList();
        Assert.NotEmpty(options);
        foreach (var option in options)
        {
            var value = option.Type == VariableType.Boolean ? Flip(option) : Sample(option);
            var set = Render(manifest, new Dictionary<string, string?>(required) { [option.Key] = value });

            Assert.True(Text(set) != Text(unset), $"{game}: setting {option.Key} to {value} changes nothing the game reads.");
            if (option.Default is not null)
            {
                continue; // a boolean: always written, true or false
            }

            // Unset, the lines it would set are not written: the admin's own values stay.
            foreach (var file in manifest.ConfigFileList)
            {
                foreach (var key in file.Set.Where(e => e.Value.Contains($"{{{{var.{option.Key}", StringComparison.Ordinal)).Select(e => e.Key))
                {
                    Assert.True(file.KeepIfEmpty?.Contains(key, StringComparer.OrdinalIgnoreCase) == true,
                        $"{game}: {file.Path} '{key}' would be removed while {option.Key} is unset.");
                }
            }
        }
    }

    [Fact]
    public void PalworldsChoicesThatAreWordsAreWrittenWithoutQuotesAndItsTextsWithThem()
    {
        // Its own file and documentation have DeathPenalty=All; a server name that happens to read "All" is still text.
        var plan = Render(TestManifests.Load("palworld"), new Dictionary<string, string?> { ["name"] = "All", ["deathPenalty"] = "All", ["randomizer"] = "Region" });
        var file = plan.ConfigFiles.Single(f => f.Path.EndsWith("PalWorldSettings.ini", StringComparison.Ordinal));

        var line = ConfigFileWriter.Apply(file, null).Split('\n').Single(l => l.StartsWith("OptionSettings=", StringComparison.Ordinal));

        Assert.Matches(@"[(,]DeathPenalty=All[,)]", line);
        Assert.Matches(@"[(,]RandomizerType=Region[,)]", line);
        Assert.Contains("ServerName=\"All\"", line, StringComparison.Ordinal);
    }

    private static LaunchPlan Render(GameManifest manifest, IReadOnlyDictionary<string, string?> values)
    {
        // Every port, optional ones too, on its usual number.
        var ports = manifest.PortList.ToDictionary(p => p.Name, p => new PortAssignment(PortAssignment.AnyAddress, p.Default), StringComparer.Ordinal);
        return LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64,
            new ServerBinding("abcd1234", values, ports, RconPassword: Rcon, Directory: @"D:\Pulsar\servers\abcd1234"));
    }

    /// <summary>What the game reads: its arguments and the lines written into its files.</summary>
    private static string Text(LaunchPlan plan) =>
        string.Join('\n', plan.Arguments.Concat(plan.ConfigFiles.SelectMany(f => f.Entries.Select(e => $"{f.Path}|{e.Key}={e.Value}"))));

    private static string Flip(VariableDefinition variable) =>
        VariableValues.FromJson(variable.Default!.Value) == "true" ? "false" : "true";

    /// <summary>A value the variable accepts, as an admin could set it.</summary>
    private static string Sample(VariableDefinition variable)
    {
        var candidates = variable.Type switch
        {
            VariableType.Enum => [variable.Options![0].Value],
            VariableType.Integer or VariableType.Number => [(variable.Max == variable.Min ? variable.Min!.Value : variable.Min!.Value + 1).ToString(CultureInfo.InvariantCulture)],
            VariableType.Boolean => ["true"],
            _ => new[] { "Pulsar test 1", "pulsar1", "Passw0rd-123", "monthly,vanilla", "https://example.com/pulsar", "12345678", "admin@example.com",
                "76561197960287930", "0123456789abcdef0123456789ABCDEF", "0123456789abcdef0123456789ABCDEF01234567" },
        };
        return candidates.FirstOrDefault(c => VariableValues.Check(variable, c) is null)
            ?? throw new InvalidOperationException($"No sample value for {variable.Key}.");
    }
}
