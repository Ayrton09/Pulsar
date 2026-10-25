using System.Text;

namespace Pulsar.Manifests.Tests;

/// <summary>The console commands a manifest declares for the players (Pulsar 1.0.9): broadcasts so far.</summary>
public sealed class GameCommandsTests
{
    [Theory]
    [InlineData("say {{message}}", null, "Restarting in 5 min", "say Restarting in 5 min")]
    [InlineData("say \"{{message}}\"", null, "Back in 1 min!", "say \"Back in 1 min!\"")]
    [InlineData("Broadcast {{message}}", "_", "Restarting in 10 s", "Broadcast Restarting_in_10_s")]
    [InlineData("say {{message}}", null, "Reinicio en 5 min por una actualización", "say Reinicio en 5 min por una actualización")]
    public void ABroadcastCarriesTheMessage(string template, string? spaces, string message, string expected)
    {
        Assert.Equal(expected, GameCommands.Broadcast(Manifest(template, spaces), message));
    }

    [Theory]
    // Source's console ends a command at ';', Unturned's arguments start at '/', quotes end an argument, a newline sends a line.
    [InlineData("hi; quit", "hi quit")]
    [InlineData("hi/255/0/0", "hi25500")]
    [InlineData("say \"x\" +rcon_password y", "say x rconpassword y")]
    [InlineData("a\nquit", "a quit")]
    [InlineData("@a {{rcon.password}} $HOME %s", "a rcon.password HOME s")]
    [InlineData("  many    spaces  ", "many spaces")]
    public void AMessageCannotEndTheCommandOrStartAnother(string message, string expected)
    {
        Assert.Equal(expected, GameCommands.CleanMessage(message));
    }

    [Fact]
    public void WhatIsLeftOfAMessageIsBounded()
    {
        Assert.Null(GameCommands.CleanMessage(";;; /// \"\""));
        Assert.Null(GameCommands.Broadcast(Manifest("say {{message}}", null), "‮\u0000"));
        Assert.Null(GameCommands.Broadcast(Manifest(null, null), "hello"));
        Assert.Equal(GameCommands.MaxMessageLength, GameCommands.CleanMessage(new string('x', 500))!.Length);
    }

    [Theory]
    [InlineData("say")]
    [InlineData("say {{message}} {{message}}")]
    [InlineData("say {{message}} {{rcon.password}}")]
    [InlineData("say {{message}}\nquit")]
    public void ABroadcastHasTheMessageOnceAndNothingElse(string broadcast)
    {
        var errors = ManifestValidator.Validate(Manifest(broadcast, null));

        Assert.Contains(errors, e => e.Path == "commands.broadcast");
    }

    [Fact]
    public void OnlySomeCharactersStandForSpaces()
    {
        Assert.Contains(ManifestValidator.Validate(Manifest("Broadcast {{message}}", ";")), e => e.Path == "commands.messageSpaces");
        Assert.DoesNotContain(ManifestValidator.Validate(Manifest("Broadcast {{message}}", "_")), e => e.Path.StartsWith("commands", StringComparison.Ordinal));
    }

    [Fact]
    public void AManifestWithCommandsGoesOnlyToAgentsThatReadThem()
    {
        Assert.True(ManifestFeatures.UsesCommands(Manifest("say {{message}}", null)));
        Assert.False(ManifestFeatures.UsesCommands(Manifest(null, null) with { Commands = null }));
    }

    private static GameManifest Manifest(string? broadcast, string? spaces)
    {
        var parsed = ManifestParser.Parse(Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 1, "key": "pulsar-test", "version": "1.0.0", "name": "Test", "platforms": ["windows-x64"],
              "runtime": { "windows-x64": { "executable": "bin/game.exe", "console": { "mode": "pty" } } },
              "readiness": { "type": "logPattern", "pattern": "^ready", "timeoutSeconds": 60 },
              "stop": { "steps": [ { "type": "consoleCommand", "command": "quit" } ], "timeoutSeconds": 15 }
            }
            """));
        return parsed.Manifest! with { Commands = new CommandsDefinition(broadcast, spaces) };
    }
}
