namespace Pulsar.Contracts;

/// <summary>What a server process is doing, as observed by its agent (docs/architecture/05).</summary>
public enum RuntimeState
{
    Offline,
    Starting,
    Running,
    Stopping,
    Crashed,
}

/// <summary>Why a server changed state; the panel derives its desired state and alerts from it.</summary>
public enum StateChangeReason
{
    /// <summary>A start, stop, restart or kill from the panel.</summary>
    Requested,

    /// <summary>The readiness check passed.</summary>
    Ready,

    /// <summary>Still alive but the readiness check never passed: running, with an alert.</summary>
    ReadinessTimedOut,

    /// <summary>Exit code 0 without a stop request (someone typed <c>quit</c>): a stop the game asked for.</summary>
    ExitedByItself,

    /// <summary>Unexpected exit with a non-zero code.</summary>
    Crashed,

    /// <summary>Too many crashes in a short time: no more automatic restarts until someone starts it.</summary>
    CrashLoop,

    /// <summary>A graceful stop did not finish in time; the process tree was killed.</summary>
    StopTimedOut,

    /// <summary>Killed on request.</summary>
    Killed,

    /// <summary>Restarted by the crash policy.</summary>
    AutoRestart,

    /// <summary>The process could not be started at all (missing executable, account, OS error).</summary>
    StartFailed,

    /// <summary>The agent is stopping (service stop, update, reboot) and stops its servers.</summary>
    AgentStopping,

    /// <summary>Started by the agent itself after it started (auto-start, or running before an orderly agent stop).</summary>
    AgentStarted,
}

/// <summary>
/// How a server runs, sent by the panel at provisioning (docs/architecture/03): the exact manifest text and the values
/// to render it with. The agent renders and validates the command line itself; it never receives one.
/// </summary>
public sealed record ServerRuntimeSpec(
    string ShortId,
    string Manifest,
    IReadOnlyDictionary<string, string?> Variables,
    IReadOnlyList<string> ExtraArguments,
    bool AutoStart,
    bool RestartOnCrash);

/// <summary>A server's state at the time of an agent's Hello: the panel takes it as the truth (reconciliation).</summary>
public sealed record ServerSnapshot(Guid ServerId, RuntimeState State, long StateSequence, int? ExitCode, DateTimeOffset? StartedAt);

/// <param name="StateSequence">Increases with every change of the server while the agent runs; older events are dropped.</param>
/// <param name="Detail">Error code or short explanation (a failed start, a readiness timeout).</param>
public sealed record ServerStateChanged(
    Guid ServerId,
    RuntimeState State,
    RuntimeState Previous,
    DateTimeOffset At,
    long StateSequence,
    int? ExitCode,
    StateChangeReason Reason,
    string? Detail);

public enum OperationOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Progress of long work started by a command carrying an operation id.</summary>
public sealed record OperationProgress(Guid OperationId, string Stage, double? Percent, string? Message);

/// <summary>The end of an operation. Kept by the agent and sent again until the panel has received it.</summary>
public sealed record OperationCompleted(Guid OperationId, Guid? ServerId, OperationOutcome Outcome, string? ErrorCode, string? Message);
