namespace Pulsar.Contracts;

/// <summary>
/// A piece of a server's console output (docs/architecture/03, "Console: flow and volume"). Sequences increase by one
/// per entry; an agent that starts again begins above every sequence it used before, so the panel and browsers can
/// tell what they already have.
/// </summary>
/// <param name="Text">Output as the game wrote it: VT sequences included for pseudo consoles, plain text otherwise.</param>
/// <param name="IsGap">
/// Output up to <paramref name="Sequence"/> was lost (it left the agent's buffer before it could be sent, or the agent
/// started again); <paramref name="Text"/> is empty.
/// </param>
public sealed record ConsoleEntry(long Sequence, DateTimeOffset At, string Text, bool IsGap = false);

/// <summary>Consecutive console entries of one server (several per message: output arrives in small pieces).</summary>
public sealed record ServerConsoleOutput(Guid ServerId, IReadOnlyList<ConsoleEntry> Entries);

/// <summary>A console the panel wants output from (browsers are watching it), in the panel's answer to a Hello.</summary>
/// <param name="FromSequence">The last sequence the panel has; null when it has nothing yet.</param>
public sealed record ConsoleSubscription(Guid ServerId, long? FromSequence);

/// <summary>Rules for a console command line, checked by the panel and again by the agent.</summary>
public static class ConsoleInput
{
    public const int MaxLength = 512;

    /// <summary>One line of 1 to <see cref="MaxLength"/> characters, not blank, without control characters.</summary>
    public static bool IsValid(string? line) =>
        line is { Length: > 0 and <= MaxLength } && !string.IsNullOrWhiteSpace(line) && !line.Any(char.IsControl);
}

/// <summary>Resource usage of a running server's process tree (its Job), sampled by the agent.</summary>
/// <param name="CpuPercent">Share of the whole machine since the previous sample (0–100, like the node's CPU).</param>
/// <param name="MemoryBytes">Working set of every process of the server.</param>
/// <param name="Query">What the game last answered to its manifest's query; null without one, or when it does not answer.</param>
/// <param name="CommittedBytes">
/// Memory committed by every process of the server: what a memory limit counts (<see cref="ServerLimits.MemoryBytes"/>);
/// null from older agents.
/// </param>
public sealed record ServerMetrics(Guid ServerId, double CpuPercent, long MemoryBytes, int ProcessCount, TimeSpan Uptime, ServerQueryInfo? Query = null,
    long? CommittedBytes = null);

/// <summary>
/// A running game's answer to its query (A2S, Minecraft's Server List Ping). Every text comes from the game and from
/// its players: untrusted, cleaned by the agent (<see cref="QueryText"/>) and shown as plain text.
/// </summary>
/// <param name="At">When the game answered.</param>
/// <param name="Bots">Bots among <paramref name="Players"/> (A2S); 0 when the game does not say.</param>
/// <param name="PlayerList">Who is playing, as far as the game tells (A2S lists them; Minecraft gives a sample).</param>
public sealed record ServerQueryInfo(
    DateTimeOffset At,
    string? Name,
    string? Map,
    int Players,
    int MaxPlayers,
    int Bots,
    string? Version,
    IReadOnlyList<QueryPlayer> PlayerList);

/// <param name="Score">The game's score (A2S); null when it has none.</param>
/// <param name="Connected">How long the player has been on the server (A2S); null when the game does not say.</param>
public sealed record QueryPlayer(string Name, long? Score, TimeSpan? Connected);

/// <summary>Limits on what a game's query answer may carry, applied by the agent and checked again by the panel.</summary>
public static class QueryText
{
    public const int MaxLength = 128;

    public const int MaxPlayers = 256;

    /// <summary>
    /// Control characters, direction overrides and marks, and colour codes (Minecraft's <c>§a</c>) removed, whitespace trimmed, cut
    /// to <see cref="MaxLength"/>; null when nothing is left.
    /// </summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(Math.Min(text.Length, MaxLength));
        for (var i = 0; i < text.Length && builder.Length < MaxLength; i++)
        {
            var c = text[i];
            if (c == '§')
            {
                i++; // the formatting code after it
                continue;
            }

            // Direction overrides and marks would let a player's name reorder the text shown after it.
            if (char.IsControl(c) || c is >= '\x202A' and <= '\x202E' or >= '\x2066' and <= '\x2069' or '\x200E' or '\x200F' or '\x061C'
                || char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])))
            {
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (builder.Length + 2 > MaxLength)
                {
                    break;
                }

                builder.Append(c).Append(text[++i]);
                continue;
            }

            builder.Append(c);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>Every running server of a node, every few seconds; servers not running are not listed.</summary>
public sealed record MetricsBatch(DateTimeOffset At, IReadOnlyList<ServerMetrics> Servers);
