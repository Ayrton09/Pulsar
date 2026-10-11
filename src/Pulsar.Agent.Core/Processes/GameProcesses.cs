using System.Threading.Channels;

namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// Starts game server processes (docs/architecture/06). The Windows implementation creates each one inside its own Job
/// Object, with a pseudo console or redirected pipes, as the server's dedicated account.
/// </summary>
public interface IGameProcessManager
{
    /// <exception cref="GameProcessException">The process could not be started (invalid path, account, OS error).</exception>
    Task<IGameProcess> StartAsync(ProcessLaunchSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// Kills every process running as <paramref name="identity"/>, for a server that is not running. A server's account
    /// can start processes outside its Job through Windows services (a scheduled task, WMI); they would survive the stop
    /// of the server, and even its deletion. Returns how many were killed; nothing for <see cref="ProcessIdentity.CurrentUser"/>.
    /// </summary>
    int TerminateStrayProcesses(ProcessIdentity identity);
}

/// <summary>How the process's console is attached (docs/architecture/04, "Console").</summary>
public enum ConsoleMode
{
    /// <summary>A pseudo console (ConPTY): output is a VT stream, input goes through the console, Ctrl+C works.</summary>
    Pty,

    /// <summary>Redirected stdin/stdout/stderr: a clean line stream, no console API output, no Ctrl+C.</summary>
    Pipes,
}

/// <summary>Code page of the output in <see cref="ConsoleMode.Pipes"/> (a pseudo console always produces UTF-8).</summary>
public enum ConsoleEncoding
{
    Utf8,
    Oem,
    Ansi,
}

/// <summary>Who the process runs as.</summary>
public abstract record ProcessIdentity
{
    private ProcessIdentity()
    {
    }

    /// <summary>The agent's own account: development only (an agent in a console, without elevation).</summary>
    public static ProcessIdentity CurrentUser { get; } = new CurrentUserIdentity();

    /// <summary>The server's dedicated local account (docs/architecture/06, ADR-0005).</summary>
    public static ProcessIdentity Account(string name) => new AccountIdentity(name);

    public sealed record CurrentUserIdentity : ProcessIdentity;

    public sealed record AccountIdentity(string Name) : ProcessIdentity;
}

/// <summary>
/// Everything needed to start one server process. Paths are relative to <see cref="ServerRoot"/> and validated by the
/// manager; arguments are already rendered (one element = one argv entry, never split or re-parsed).
/// </summary>
public sealed record ProcessLaunchSpec(
    Guid ServerId,
    string ServerRoot,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    ConsoleMode ConsoleMode,
    ConsoleEncoding Encoding,
    ProcessIdentity Identity,
    ProcessLimits? Limits = null);

/// <summary>Caps on a process tree (its Job); null fields are not capped.</summary>
/// <param name="CpuPercent">Share of the whole machine, 1–100.</param>
/// <param name="MemoryBytes">Memory committed by the whole tree.</param>
public sealed record ProcessLimits(int? CpuPercent, long? MemoryBytes)
{
    public static ProcessLimits? From(Pulsar.Contracts.ServerLimits? limits) =>
        limits is { CpuPercent: null, MemoryBytes: null } or null ? null : new ProcessLimits(limits.CpuPercent, limits.MemoryBytes);
}

/// <summary>Decoded console output, in arrival order. A pseudo console delivers screen updates, not lines.</summary>
/// <param name="End">
/// For a game in a host process: the offset right after this text in the game's output since it started, from which
/// the agent asks for the rest after a restart (docs/architecture/06, "Server hosts"); -1 otherwise.
/// </param>
public sealed record ConsoleChunk(DateTimeOffset At, string Text, long End = -1);

/// <summary>Resource usage of a server's process tree at one moment.</summary>
/// <param name="CpuTime">CPU time of every process the tree ever had (finished ones included), user and kernel.</param>
/// <param name="MemoryBytes">Working set of the processes alive now.</param>
/// <param name="CommittedBytes">Memory committed by the processes alive now (what a memory limit counts).</param>
public sealed record ProcessUsage(TimeSpan CpuTime, long MemoryBytes, int ProcessCount, long CommittedBytes = 0);

/// <summary>A running server process and the tree it belongs to (its Job).</summary>
public interface IGameProcess : IAsyncDisposable
{
    int ProcessId { get; }

    DateTimeOffset StartedAt { get; }

    /// <summary>Completes with the exit code of the main process (children may outlive it; see <see cref="CountProcesses"/>).</summary>
    Task<int> Exited { get; }

    /// <summary>Console output; completes when the output closes. It must be drained, or the process blocks writing.</summary>
    ChannelReader<ConsoleChunk> Output { get; }

    /// <summary>Whether <see cref="SendInterruptAsync"/> is available (pseudo console only).</summary>
    bool SupportsInterrupt { get; }

    /// <summary>
    /// Writes <paramref name="text"/> to the process input as is (the caller adds the newline). A game that stopped
    /// reading its input keeps the write waiting: cancelling gives up waiting (the text may still reach it later).
    /// </summary>
    /// <exception cref="InvalidOperationException">The process exited or no longer reads its input.</exception>
    ValueTask WriteInputAsync(string text, CancellationToken cancellationToken);

    /// <summary>Sends Ctrl+C (waits as <see cref="WriteInputAsync"/> does).</summary>
    ValueTask SendInterruptAsync(CancellationToken cancellationToken);

    /// <summary>Processes still alive in the tree, the main one included.</summary>
    int CountProcesses();

    /// <summary>Usage of the whole tree now; all zero once disposed.</summary>
    ProcessUsage SampleUsage();

    /// <summary>Kills the whole tree at once (children and grandchildren included).</summary>
    void TerminateTree(int exitCode);

    /// <summary>Replaces the tree's caps while it runs (null removes them).</summary>
    /// <exception cref="GameProcessException">The caps could not be applied.</exception>
    void ApplyLimits(ProcessLimits? limits);

    // A local Job answers at once; a game in a host process answers over its pipe, and these never wait long for it.

    /// <inheritdoc cref="SampleUsage"/>
    ValueTask<ProcessUsage> SampleUsageAsync(CancellationToken cancellationToken) => ValueTask.FromResult(SampleUsage());

    /// <inheritdoc cref="TerminateTree"/>
    ValueTask TerminateTreeAsync(int exitCode, CancellationToken cancellationToken)
    {
        TerminateTree(exitCode);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc cref="ApplyLimits"/>
    ValueTask ApplyLimitsAsync(ProcessLimits? limits, CancellationToken cancellationToken)
    {
        ApplyLimits(limits);
        return ValueTask.CompletedTask;
    }

    /// <summary>Whether a process of the tree was refused memory because of the memory cap since it started.</summary>
    bool ReachedMemoryLimit { get; }
}

/// <summary>A server process could not be started; <see cref="Code"/> is stable and reported to the panel.</summary>
public sealed class GameProcessException : Exception
{
    public GameProcessException()
        : this("process.start_failed", "The process could not be started.")
    {
    }

    public GameProcessException(string message)
        : this("process.start_failed", message)
    {
    }

    public GameProcessException(string message, Exception innerException)
        : this("process.start_failed", message, innerException)
    {
    }

    public GameProcessException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}
