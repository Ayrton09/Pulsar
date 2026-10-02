using System.Security.Cryptography;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

public enum InstallState
{
    NotInstalled,
    Installing,
    Installed,
    Failed,
}

/// <summary>Last state the agent observed (it owns this dimension; docs/architecture/05).</summary>
public enum ServerRuntimeState
{
    Offline,
    Starting,
    Running,
    Stopping,
    Crashed,
}

/// <summary>What the panel (a user, later the scheduler) wants.</summary>
public enum DesiredState
{
    Stopped,
    Running,
}

/// <summary>Why the server last changed state, as the agent reported it (mirrors the protocol's reasons).</summary>
public enum ServerStateReason
{
    Requested,
    Ready,
    ReadinessTimedOut,
    ExitedByItself,
    Crashed,
    CrashLoop,
    StopTimedOut,
    Killed,
    AutoRestart,
    StartFailed,
    AgentStopping,
    AgentStarted,

    /// <summary>Taken from an agent's Hello (reconciliation), not from a change it reported.</summary>
    Reconciled,
}

/// <summary>
/// A game server (docs/architecture/02 and 05). Three independent dimensions: the install state and the desired state
/// belong to the panel, the runtime state is what its agent observed. At most one operation runs on it at a time.
/// </summary>
public sealed class Server
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 512;
    public const int ShortIdLength = 8;

    private const string ShortIdAlphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private Server()
    {
        // For EF Core materialization.
        ShortId = null!;
        Name = null!;
    }

    private Server(ServerId id, string shortId, NodeId nodeId, TemplateId templateId, string name, string? description, DateTimeOffset now)
    {
        Id = id;
        ShortId = shortId;
        NodeId = nodeId;
        TemplateId = templateId;
        Name = name;
        Description = description;
        InstallState = InstallState.NotInstalled;
        RuntimeState = ServerRuntimeState.Offline;
        DesiredState = DesiredState.Stopped;
        RestartOnCrash = true;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public ServerId Id { get; private set; }

    /// <summary>Random lowercase base32: names the server's folder and Windows account on its node.</summary>
    public string ShortId { get; private set; }

    public NodeId NodeId { get; private set; }

    public TemplateId TemplateId { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public InstallState InstallState { get; private set; }

    public ServerRuntimeState RuntimeState { get; private set; }

    public DesiredState DesiredState { get; private set; }

    public ServerStateReason? LastStateReason { get; private set; }

    /// <summary>The agent's sequence of the last state taken; older events are ignored.</summary>
    public long StateSequence { get; private set; }

    public DateTimeOffset? StateObservedAt { get; private set; }

    public OperationId? ActiveOperationId { get; private set; }

    public bool AutoStart { get; private set; }

    public bool RestartOnCrash { get; private set; }

    public int? LastExitCode { get; private set; }

    public DateTimeOffset? LastStartedAt { get; private set; }

    public DateTimeOffset? LastStoppedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static string NewShortId() => RandomNumberGenerator.GetString(ShortIdAlphabet, ShortIdLength);

    public static Result<Server> Create(NodeId nodeId, TemplateId templateId, string name, string? description, string shortId, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return ServerErrors.NameInvalid;
        }

        var cleanDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (cleanDescription?.Length > MaxDescriptionLength)
        {
            return ServerErrors.DescriptionTooLong;
        }

        if (shortId is not { Length: ShortIdLength } || shortId.Any(c => !ShortIdAlphabet.Contains(c, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Invalid short id.", nameof(shortId));
        }

        return new Server(ServerId.New(), shortId, nodeId, templateId, trimmed, cleanDescription, now);
    }

    /// <summary>Takes the server for <paramref name="operation"/>; only one operation runs on a server at a time.</summary>
    public Result BeginOperation(Operation operation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (ActiveOperationId is not null)
        {
            return ServerErrors.Busy;
        }

        if (operation.Type == OperationType.ServerInstall && RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        ActiveOperationId = operation.Id;
        if (operation.Type is OperationType.ServerProvision or OperationType.ServerInstall)
        {
            InstallState = InstallState.Installing;
        }

        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Releases the server from <paramref name="operation"/> and applies its outcome.</summary>
    public void OperationCompleted(Operation operation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (ActiveOperationId != operation.Id)
        {
            return;
        }

        ActiveOperationId = null;
        var succeeded = operation.Status == OperationStatus.Succeeded;
        switch (operation.Type)
        {
            case OperationType.ServerProvision when !succeeded:
            case OperationType.ServerInstall when !succeeded:
                InstallState = InstallState.Failed;
                break;
            case OperationType.ServerInstall:
                InstallState = InstallState.Installed;
                break;
        }

        UpdatedAt = now;
    }

    public Result RequestStart(DateTimeOffset now)
    {
        if (InstallState != InstallState.Installed)
        {
            return ServerErrors.NotInstalled;
        }

        if (ActiveOperationId is not null)
        {
            return ServerErrors.Busy;
        }

        DesiredState = DesiredState.Running;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result RequestRestart(DateTimeOffset now) => RequestStart(now);

    /// <summary>Stop and kill are always allowed: they only ever make things safer.</summary>
    public void RequestStop(DateTimeOffset now)
    {
        DesiredState = DesiredState.Stopped;
        UpdatedAt = now;
    }

    /// <summary>
    /// What the agent reports. Changes older than the last one taken are ignored; a <paramref name="snapshot"/> (the
    /// agent's Hello) is always taken and resets the sequence, since a restarted agent counts from zero again.
    /// </summary>
    public bool Observe(ServerRuntimeState state, ServerStateReason reason, int? exitCode, long sequence, DateTimeOffset at, bool snapshot)
    {
        if (!snapshot && sequence <= StateSequence)
        {
            return false;
        }

        if (state == ServerRuntimeState.Starting && RuntimeState != ServerRuntimeState.Starting)
        {
            LastStartedAt = at;
        }

        if (state is ServerRuntimeState.Offline or ServerRuntimeState.Crashed && RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            LastStoppedAt = at;
        }

        if (state is ServerRuntimeState.Offline or ServerRuntimeState.Crashed)
        {
            LastExitCode = exitCode ?? LastExitCode;
        }

        // The game asked to stop, or it cannot be kept running: the intent follows.
        if (reason is ServerStateReason.ExitedByItself or ServerStateReason.CrashLoop or ServerStateReason.StartFailed)
        {
            DesiredState = DesiredState.Stopped;
        }

        RuntimeState = state;
        LastStateReason = reason;
        StateSequence = sequence;
        StateObservedAt = at;
        UpdatedAt = at;
        return true;
    }
}
