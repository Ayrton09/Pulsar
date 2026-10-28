using Pulsar.Domain.Common;

namespace Pulsar.Domain.Scheduling;

public enum SequenceStepKind
{
    /// <summary>Sends a console command (skipped while the server is not running).</summary>
    Command,

    /// <summary>Tells everyone playing a message, with the game's broadcast command (skipped without one, or while not running).</summary>
    Broadcast,

    Wait,

    /// <summary>Stops the server and waits until it is stopped.</summary>
    Stop,

    /// <summary>Starts the server and waits until it is running.</summary>
    Start,

    /// <summary>Restarts the server and waits until it is running again.</summary>
    Restart,

    /// <summary>Takes a backup and waits for it.</summary>
    Backup,

    /// <summary>Updates the game (the server must be stopped) and waits for it.</summary>
    Update,

    /// <summary>
    /// Moves the server to a newer version of its game's template (<see cref="SequenceStep.Text"/>: its id; the server must
    /// be stopped) and waits until it is installed again with it. The panel's own: scheduled tasks cannot have it.
    /// </summary>
    UpgradeTemplate,

    /// <summary>
    /// Copies another server's files into this one (a clone, Pulsar 1.0.16; <see cref="SequenceStep.Text"/>: the other
    /// server's id): a backup of it, then, once this server is installed, that backup restored here, relayed by the panel
    /// from the other server's node. The panel's own: scheduled tasks cannot have it.
    /// </summary>
    CopyFrom,
}

/// <param name="Text">The console command, or the message to broadcast.</param>
/// <param name="Seconds">How long a wait lasts.</param>
/// <param name="Validate">An update verifies every file.</param>
/// <param name="OnlyIfWasRunning">A start happens only when the server was running as the sequence began: it is put back as it was.</param>
/// <param name="Always">Runs even after an earlier step failed (the start that puts the server back online after an update).</param>
public sealed record SequenceStep(SequenceStepKind Kind, string? Text = null, int Seconds = 0, bool Validate = false, bool OnlyIfWasRunning = false,
    bool Always = false)
{
    public const int MaxTextLength = 512;

    /// <summary>What a game's broadcast command carries (Pulsar.Manifests' GameCommands cuts there too).</summary>
    public const int MaxMessageLength = 128;

    public const int MaxWaitSeconds = 24 * 60 * 60;
}

public enum SequenceStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>What started a sequence.</summary>
public enum SequenceOrigin
{
    /// <summary>A scheduled task of several steps.</summary>
    Schedule,

    /// <summary>An automatic game update (warn the players, stop, update, start again).</summary>
    GameUpdate,

    /// <summary>An automatic move to a newer version of the server's template (warn, stop, move, start again).</summary>
    TemplateUpdate,

    /// <summary>A change of the game's Steam branch (Pulsar 1.0.14): a backup, then the chosen branch installed.</summary>
    SteamBranch,

    /// <summary>A new server's copy of another one's files (a clone, Pulsar 1.0.16).</summary>
    Clone,
}

/// <summary>
/// Steps done one after the other on a server by the panel (Pulsar 1.0.9): warn the players, wait, stop, update, start
/// again. Each step's state is stored, so a panel that restarts carries on where it was; the panel's sequence runner
/// takes the due ones (<see cref="WakeAt"/>). At most one runs on a server at a time.
/// </summary>
public sealed class ServerSequence
{
    public const int MaxSteps = 32;
    public const int MaxNameLength = 128;
    public const int MaxErrorCodeLength = 64;
    public const int MaxMessageLength = 1024;

    private ServerSequence()
    {
        // For EF Core materialization.
        Name = null!;
        Steps = null!;
    }

    private ServerSequence(ServerId serverId, SequenceOrigin origin, string name, IReadOnlyList<SequenceStep> steps, bool wasRunning, DateTimeOffset now)
    {
        Id = ServerSequenceId.New();
        ServerId = serverId;
        Origin = origin;
        Name = name;
        Steps = steps;
        WasRunning = wasRunning;
        Status = SequenceStatus.Running;
        WakeAt = now;
        CreatedAt = now;
    }

    public ServerSequenceId Id { get; private set; }

    public ServerId ServerId { get; private set; }

    public SequenceOrigin Origin { get; private set; }

    /// <summary>Who acts, for the audit log: the scheduled task's name, or the game updates'.</summary>
    public string Name { get; private set; }

    public IReadOnlyList<SequenceStep> Steps { get; private set; }

    /// <summary>The step being done (<see cref="Steps"/>.Count once over).</summary>
    public int StepIndex { get; private set; }

    /// <summary>The current step did what it does (sent the stop, started the update) and waits for its outcome.</summary>
    public bool StepBegun { get; private set; }

    public DateTimeOffset? StepStartedAt { get; private set; }

    /// <summary>When the runner looks at it again (UTC).</summary>
    public DateTimeOffset WakeAt { get; private set; }

    /// <summary>The operation the current step started (a backup, an update): it has the outcome.</summary>
    public OperationId? OperationId { get; private set; }

    /// <summary>
    /// The server's state as the current step began (its agent's sequence and when the panel saw it): a start is over
    /// when the server reports running after that, whatever the node's clock says.
    /// </summary>
    public long? ObservedStateSequence { get; private set; }

    public DateTimeOffset? ObservedStateAt { get; private set; }

    /// <summary>The server was running when the sequence began (a start that puts it back as it was).</summary>
    public bool WasRunning { get; private set; }

    public SequenceStatus Status { get; private set; }

    /// <summary>Why the sequence failed (the first step that did); kept while the steps that always run go on.</summary>
    public string? ErrorCode { get; private set; }

    public string? Message { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>A user cancelled it: the steps that always run are done, then it ends as cancelled.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>The step to do now; null once the sequence ended.</summary>
    public SequenceStep? Current => Status == SequenceStatus.Running && StepIndex < Steps.Count ? Steps[StepIndex] : null;

    public static Result<ServerSequence> Start(ServerId serverId, SequenceOrigin origin, string name, IReadOnlyList<SequenceStep> steps, bool wasRunning,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || !DisplayNames.IsDisplayable(trimmed))
        {
            return ScheduleErrors.NameInvalid;
        }

        // A move to another template is the template updates' own step, a copy of another server's files a clone's.
        if (steps.Count is 0 or > MaxSteps || steps.Any(s => !IsValid(s))
            || (origin != SequenceOrigin.TemplateUpdate && steps.Any(s => s.Kind == SequenceStepKind.UpgradeTemplate))
            || (origin != SequenceOrigin.Clone && steps.Any(s => s.Kind == SequenceStepKind.CopyFrom)))
        {
            return ScheduleErrors.StepsInvalid;
        }

        return new ServerSequence(serverId, origin, trimmed, [.. steps], wasRunning, now);
    }

    public static bool IsValid(SequenceStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var text = step.Text?.Trim();
        return step.Kind switch
        {
            SequenceStepKind.Command or SequenceStepKind.Broadcast => !string.IsNullOrEmpty(text) && !text.Any(char.IsControl)
                && text.Length <= (step.Kind == SequenceStepKind.Command ? SequenceStep.MaxTextLength : SequenceStep.MaxMessageLength),
            SequenceStepKind.Wait => step.Seconds is > 0 and <= SequenceStep.MaxWaitSeconds,
            SequenceStepKind.UpgradeTemplate => Guid.TryParse(text, out var template) && template != Guid.Empty,
            SequenceStepKind.CopyFrom => Guid.TryParse(text, out var source) && source != Guid.Empty,
            _ => Enum.IsDefined(step.Kind),
        };
    }

    /// <summary>The current step did what it does; <paramref name="operation"/> is the one it started.</summary>
    /// <param name="observed">The server's state sequence and when it was seen, before the step acted.</param>
    public void Began(DateTimeOffset now, (long Sequence, DateTimeOffset? At) observed, OperationId? operation = null)
    {
        StepBegun = true;
        StepStartedAt ??= now;
        OperationId = operation;
        (ObservedStateSequence, ObservedStateAt) = observed;
    }

    /// <summary>The current step waits before it acts (keeping when it was first tried, for its timeout).</summary>
    public void Unbegin()
    {
        StepBegun = false;
        OperationId = null;
        (ObservedStateSequence, ObservedStateAt) = (null, null);
    }

    /// <summary>The server reported a state since the current step began.</summary>
    public bool ChangedSince(long stateSequence, DateTimeOffset? observedAt) =>
        StepBegun && (stateSequence != ObservedStateSequence || observedAt != ObservedStateAt);

    /// <summary>The runner looks again at <paramref name="at"/> (a wait, or the outcome of a step).</summary>
    public void WaitUntil(DateTimeOffset at) => WakeAt = at;

    /// <summary>The current step is over: the next one is done now (after a failure, only those that always run).</summary>
    public void StepDone(DateTimeOffset now)
    {
        var next = StepIndex + 1;
        while (next < Steps.Count && ErrorCode is not null && !Steps[next].Always)
        {
            next++;
        }

        StepIndex = next;
        StepBegun = false;
        StepStartedAt = null;
        OperationId = null;
        (ObservedStateSequence, ObservedStateAt) = (null, null);
        WakeAt = now;
        if (StepIndex >= Steps.Count)
        {
            Status = CancelledAt is not null ? SequenceStatus.Cancelled : ErrorCode is null ? SequenceStatus.Succeeded : SequenceStatus.Failed;
            FinishedAt = now;
        }
    }

    /// <summary>The current step failed: the sequence fails, once the steps that always run are done.</summary>
    public void StepFailed(string code, string? message, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        if (ErrorCode is null)
        {
            ErrorCode = code.Length <= MaxErrorCodeLength ? code : code[..MaxErrorCodeLength];
            Message = message is { Length: > MaxMessageLength } ? message[..MaxMessageLength] : message;
        }

        StepDone(now);
    }

    /// <summary>
    /// Cancelled by a user: what is left is not done but the steps that always run (the start that puts the server back as
    /// it was), and it ends as cancelled. The operation the current step started, if any, is the caller's to cancel.
    /// </summary>
    public Result Cancel(DateTimeOffset now)
    {
        if (Status != SequenceStatus.Running || CancelledAt is not null)
        {
            return ScheduleErrors.SequenceOver;
        }

        CancelledAt = now;
        ErrorCode ??= ScheduleErrors.Cancelled.Code;
        Message ??= ScheduleErrors.Cancelled.Message;
        // The current step is not finished, unless it is one of those that always run.
        if (Current is not { Always: true })
        {
            StepDone(now);
        }

        return Result.Success();
    }
}
