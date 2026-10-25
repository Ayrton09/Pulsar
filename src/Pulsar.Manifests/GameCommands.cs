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
    internal static bool HasOnlyMessage(string template)
    {
        var first = template.IndexOf(MessagePlaceholder, StringComparison.Ordinal);
        if (first < 0 || template.IndexOf(MessagePlaceholder, first + MessagePlaceholder.Length, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var rest = template.Remove(first, MessagePlaceholder.Length);
        return !rest.Contains("{{", StringComparison.Ordinal) && !rest.Contains("}}", StringComparison.Ordinal);
    }
}
