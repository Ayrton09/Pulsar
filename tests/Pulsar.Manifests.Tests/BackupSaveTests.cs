using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulsar.Manifests.Tests;

/// <summary>The console commands that make a running game save before a backup (<c>backup.save</c>, Phase 15).</summary>
public sealed class BackupSaveTests
{
    [Fact]
    public void MinecraftPausesItsAutosaveAroundAFlushedSave()
    {
        var save = TestManifests.Load("minecraft").Backup!.Save!;

        Assert.Equal(["save-off", "save-all flush"], save.Commands);
        Assert.Equal(["save-on"], save.Resume);
        Assert.Matches(Confirmation(save), "[12:00:01] [Server thread/INFO]: Saved the game\r\n");
        Assert.DoesNotMatch(Confirmation(save), "[12:00:01] [Server thread/INFO]: <Steve> Saved the game\r\n");
        Assert.DoesNotMatch(Confirmation(save), "[12:00:00] [Server thread/INFO]: Saving the game (this may take a moment!)\r\n");
    }

    [Fact]
    public void RustConfirmsWithTheEntitiesItSaved()
    {
        var save = TestManifests.Load("rust").Backup!.Save!;

        Assert.Equal(["server.save"], save.Commands);
        Assert.Matches(Confirmation(save), "Saved 183,679 ents, cache(1.49), write(0.06), disk(0.06).\n");
        Assert.DoesNotMatch(Confirmation(save), "Saving complete\n");
    }

    /// <summary>Games whose answer to a save is not documented: the backup sends the command and waits.</summary>
    [Theory]
    [InlineData("ark-sa", "SaveWorld")]
    [InlineData("palworld", "Save")]
    [InlineData("7dtd", "saveworld")]
    [InlineData("project-zomboid", "save")]
    public void OtherGamesSaveAndWait(string game, string command)
    {
        var save = TestManifests.Load(game).Backup!.Save!;

        Assert.Equal([command], save.Commands);
        Assert.Null(save.Confirmation);
        Assert.InRange(save.WaitSeconds, 5, 30);
    }

    [Theory]
    [InlineData("valheim")]
    [InlineData("cs2")]
    [InlineData("css")]
    [InlineData("scpsl")]
    public void GamesWithoutASaveCommandHaveNone(string game) => Assert.Null(TestManifests.Load(game).Backup?.Save);

    [Theory]
    [InlineData("""{"commands": []}""", "backup.save.commands")]
    [InlineData("""{"commands": ["a", "b", "c", "d", "e"]}""", "backup.save.commands")]
    [InlineData("""{"commands": ["save\nquit"]}""", "backup.save.commands[0]")]
    [InlineData("""{"commands": ["save"], "resume": [" "]}""", "backup.save.resume[0]")]
    [InlineData("""{"commands": ["save"], "resume": ["a", "b", "c", "d", "e"]}""", "backup.save.resume")]
    [InlineData("""{"commands": ["save"], "confirmation": "^(a)\\1$"}""", "backup.save.confirmation")]
    [InlineData("""{"commands": ["save"], "waitSeconds": 61}""", "backup.save.waitSeconds")]
    [InlineData("""{"commands": ["save"], "timeoutSeconds": 0}""", "backup.save.timeoutSeconds")]
    [InlineData("""{"commands": ["save"], "timeoutSeconds": 301}""", "backup.save.timeoutSeconds")]
    public void SaveCommandsAreChecked(string save, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("_test", m => m["backup"] = new JsonObject { ["save"] = JsonNode.Parse(save) }));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void ASaveNeedsItsCommands()
    {
        var errors = TestManifests.Errors(TestManifests.Change("_test", m => m["backup"] = new JsonObject { ["save"] = new JsonObject() }));

        Assert.NotEmpty(errors);
    }

    private static Regex Confirmation(GameSaveDefinition save) =>
        new(save.Confirmation!, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.Multiline);
}
