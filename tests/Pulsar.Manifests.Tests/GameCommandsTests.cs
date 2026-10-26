using System.Text;

namespace Pulsar.Manifests.Tests;

/// <summary>The console commands a manifest declares for the players: broadcasts (Pulsar 1.0.9) and one player's (1.0.11).</summary>
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

    [Theory]
    [InlineData("kick {{player}}", false, "Notch", "kick Notch")]
    [InlineData("kick {{player}}", false, "  Steve_2  ", "kick Steve_2")]
    [InlineData("kick \"{{player}}\"", true, "Big Bob", "kick \"Big Bob\"")]
    [InlineData("ban add \"{{player}}\" 10 years", true, "José.M-1", "ban add \"José.M-1\" 10 years")]
    [InlineData("Kick {{player}}", true, "Ñandú 99", "Kick Ñandú 99")]
    public void APlayersCommandCarriesTheName(string template, bool spaces, string player, string expected)
    {
        Assert.Equal(expected, GameCommands.Player(Players(new PlayerCommandsDefinition(Kick: template, Spaces: spaces)), PlayerAction.Kick, player));
    }

    [Theory]
    // Minecraft's selectors are everyone (@a), quotes end an argument, Source's ';' ends a command, Unturned's '/' starts an argument.
    [InlineData("@a", true)]
    [InlineData("Bob\" ; quit", true)]
    [InlineData("Bob;quit", true)]
    [InlineData("Bob/Griefing/0", true)]
    [InlineData("-ip", true)]
    [InlineData("Bob\nstop", true)]
    [InlineData("Bob‮evil", true)]
    [InlineData("Bob Smith", false)]
    [InlineData("Bob  Smith", true)]
    [InlineData("Bob\tSmith", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\U0001F600", true)]
    public void ANameTheConsoleWouldReadAsMoreIsRefusedNotCleaned(string player, bool spaces)
    {
        Assert.Null(GameCommands.PlayerName(player, spaces));
        Assert.Null(GameCommands.Player(Players(new PlayerCommandsDefinition(Kick: "kick {{player}}", Spaces: spaces)), PlayerAction.Kick, player));
    }

    [Fact]
    public void ANameIsBoundedAndOnlyTheGamesCommandsAreSent()
    {
        Assert.NotNull(GameCommands.PlayerName(new string('a', GameCommands.MaxPlayerLength), false));
        Assert.Null(GameCommands.PlayerName(new string('a', GameCommands.MaxPlayerLength + 1), false));
        var manifest = Players(new PlayerCommandsDefinition(Kick: "kick {{player}}", AdminAdd: "op {{player}}"));
        Assert.Null(GameCommands.Player(manifest, PlayerAction.Ban, "Notch"));
        Assert.Null(GameCommands.Player(Manifest("say {{message}}", null), PlayerAction.Kick, "Notch"));
        Assert.Equal([PlayerAction.Kick, PlayerAction.AdminAdd], GameCommands.Actions(manifest));
    }

    [Theory]
    [InlineData("kick", "commands.players.kick")]
    [InlineData("kick {{player}} {{player}}", "commands.players.kick")]
    [InlineData("kick {{player}} {{rcon.password}}", "commands.players.kick")]
    [InlineData("kick {{player}}\nstop", "commands.players.kick")]
    public void APlayersCommandHasThePlayerOnceAndNothingElse(string kick, string path)
    {
        Assert.Contains(ManifestValidator.Validate(Players(new PlayerCommandsDefinition(Kick: kick))), e => e.Path == path);
    }

    [Fact]
    public void PlayerCommandsNeedACommandAndListsInTheServersFolder()
    {
        Assert.Contains(ManifestValidator.Validate(Players(new PlayerCommandsDefinition(Spaces: true))), e => e.Path == "commands.players");
        var lists = new PlayerListsDefinition(Banned: new PlayerListFile("../banned.json", "name"), Admins: new PlayerListFile("ops.json", "na-me"));
        var errors = ManifestValidator.Validate(Players(new PlayerCommandsDefinition(Kick: "kick {{player}}", Lists: lists)));

        Assert.Contains(errors, e => e.Path == "commands.players.lists.banned.file");
        Assert.Contains(errors, e => e.Path == "commands.players.lists.admins.field");
        Assert.DoesNotContain(ManifestValidator.Validate(Players(new PlayerCommandsDefinition(Kick: "kick {{player}}",
            Lists: new PlayerListsDefinition(Whitelist: new PlayerListFile("whitelist.json", "name"))))), e => e.Path.StartsWith("commands", StringComparison.Ordinal));
    }

    [Fact]
    public void AManifestWithPlayerCommandsGoesOnlyToAgentsThatReadThem()
    {
        Assert.True(ManifestFeatures.UsesPlayerCommands(Players(new PlayerCommandsDefinition(Kick: "kick {{player}}"))));
        Assert.False(ManifestFeatures.UsesPlayerCommands(Manifest("say {{message}}", null)));
    }

    private static GameManifest Players(PlayerCommandsDefinition players) =>
        Manifest(null, null) with { Commands = new CommandsDefinition(Players: players) };

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
