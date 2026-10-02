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
public sealed record ServerMetrics(Guid ServerId, double CpuPercent, long MemoryBytes, int ProcessCount, TimeSpan Uptime);

/// <summary>Every running server of a node, every few seconds; servers not running are not listed.</summary>
public sealed record MetricsBatch(DateTimeOffset At, IReadOnlyList<ServerMetrics> Servers);
