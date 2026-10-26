using System.Text;

namespace Pulsar.Manifests;

/// <summary>The console commands a manifest declares for the game's players (<see cref="CommandsDefinition"/>), filled in.</summary>
public static class GameCommands
{
    public const string MessagePlaceholder = "{{message}}";

    /// <summary>The longest message a broadcast carries; a longer one is cut.</summary>
    public const int MaxMessageLength = 128;

    /// <summary>
    /// The command telling everyone playing <paramref name="message"/>; null when the game has none, or nothing of the
    /// message is left once cleaned (<see cref="CleanMessage"/>).
    /// </summary>
    public static string? Broadcast(GameManifest manifest, string message)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Commands?.Broadcast is not { } template || CleanMessage(message) is not { } clean)
        {
            return null;
        }

        return template.Replace(MessagePlaceholder, manifest.Commands.MessageSpaces is { } spaces ? clean.Replace(" ", spaces, StringComparison.Ordinal) : clean,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Letters, digits, spaces and <c>. , ! ? : ' ( ) -</c> of <paramref name="message"/>, on one line, its runs of spaces
    /// made one, cut to <see cref="MaxMessageLength"/>; null when nothing is left. What a game's console reads as more than
    /// text (Source's <c>;</c> ends a command, Unturned's <c>/</c> starts an argument, quotes end one) never gets through.
    /// </summary>
    public static string? CleanMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var clean = new StringBuilder(Math.Min(message.Length, MaxMessageLength));
        foreach (var c in message)
        {
            if (clean.Length >= MaxMessageLength)
            {
                break;
            }

            if (char.IsWhiteSpace(c))
            {
                if (clean.Length > 0 && clean[^1] != ' ')
                {
                    clean.Append(' ');
                }
            }
            else if (char.IsLetterOrDigit(c) || c is '.' or ',' or '!' or '?' or ':' or '\'' or '(' or ')' or '-')
            {
                clean.Append(c);
            }
        }

        var text = clean.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The template has <see cref="MessagePlaceholder"/> once and no other placeholder.</summary>
    internal static bool HasOnlyMessage(string template) => HasOnly(template, MessagePlaceholder);

    public const string PlayerPlaceholder = "{{player}}";

    /// <summary>The longest player name or id the panel takes.</summary>
    public const int MaxPlayerLength = 64;

    /// <summary>
    /// The command doing <paramref name="action"/> to <paramref name="player"/>; null when the game has none for it, or the
    /// player is not one the panel sends (<see cref="PlayerName"/>).
    /// </summary>
    public static string? Player(GameManifest manifest, PlayerAction action, string? player)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Commands?.Players is not { } players || Template(players, action) is not { } template
            || PlayerName(player, players.Spaces) is not { } name)
        {
            return null;
        }

        return template.Replace(PlayerPlaceholder, name, StringComparison.Ordinal);
    }

    /// <summary>What the game can do to a player, in <see cref="PlayerAction"/>'s order.</summary>
    public static IReadOnlyList<PlayerAction> Actions(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Commands?.Players is { } players
            ? [.. Enum.GetValues<PlayerAction>().Where(a => Template(players, a) is not null)]
            : [];
    }

    /// <summary>
    /// <paramref name="player"/> without the spaces around it, when it is 1 to <see cref="MaxPlayerLength"/> letters, digits
    /// and <c>_ - .</c>, not starting with <c>-</c>, and single spaces between them if <paramref name="spaces"/>; null
    /// otherwise. A name is never cleaned: one the console would read as more than a name (quotes, Source's <c>;</c>,
    /// Unturned's <c>/</c>, Minecraft's <c>@a</c>, which is everyone, an option such as Project Zomboid's <c>-ip</c>) is
    /// refused, as one the panel would change into another player's.
    /// </summary>
    public static string? PlayerName(string? player, bool spaces)
    {
        var name = player?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxPlayerLength || name[0] == '-')
        {
            return null;
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var allowed = char.IsLetterOrDigit(c) || c is '_' or '-' or '.' || (spaces && c == ' ' && name[i - 1] != ' ');
            if (!allowed)
            {
                return null;
            }
        }

        return name;
    }

    /// <summary>The game's command for <paramref name="action"/>, null when it has none.</summary>
    public static string? Template(PlayerCommandsDefinition players, PlayerAction action)
    {
        ArgumentNullException.ThrowIfNull(players);
        return action switch
        {
            PlayerAction.Kick => players.Kick,
            PlayerAction.Ban => players.Ban,
            PlayerAction.Unban => players.Unban,
            PlayerAction.WhitelistAdd => players.WhitelistAdd,
            PlayerAction.WhitelistRemove => players.WhitelistRemove,
            PlayerAction.AdminAdd => players.AdminAdd,
            PlayerAction.AdminRemove => players.AdminRemove,
            _ => null,
        };
    }

    /// <summary>The game's file listing <paramref name="kind"/>, null when it names none.</summary>
    public static PlayerListFile? List(GameManifest manifest, PlayerListKind kind)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var lists = manifest.Commands?.Players?.Lists;
        return kind switch
        {
            PlayerListKind.Banned => lists?.Banned,
            PlayerListKind.Whitelist => lists?.Whitelist,
            PlayerListKind.Admins => lists?.Admins,
            _ => null,
        };
    }

    /// <summary>The template has <see cref="PlayerPlaceholder"/> once and no other placeholder.</summary>
    internal static bool HasOnlyPlayer(string template) => HasOnly(template, PlayerPlaceholder);

    private static bool HasOnly(string template, string placeholder)
    {
        var first = template.IndexOf(placeholder, StringComparison.Ordinal);
        if (first < 0 || template.IndexOf(placeholder, first + placeholder.Length, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var rest = template.Remove(first, placeholder.Length);
        return !rest.Contains("{{", StringComparison.Ordinal) && !rest.Contains("}}", StringComparison.Ordinal);
    }
}
