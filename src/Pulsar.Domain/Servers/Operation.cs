using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

public enum OperationType
{
    ServerProvision,
    ServerInstall,
    ServerUpdate,
    ServerDelete,
    BackupCreate,
    BackupRestore,
}

public enum OperationStatus
{
    /// <summary>Created; the command has not been accepted by the agent yet.</summary>
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>The agent no longer knows it (it restarted or lost it): the outcome is unknown.</summary>
    Interrupted,
}

public enum OperationRequester
{
    User,
    Schedule,
    System,
}

/// <summary>Long work an agent does for the panel (docs/architecture/02, "Operations"), reported through events.</summary>
public sealed class Operation
{
    public const int MaxErrorMessageLength = 1024;
    public const int MaxStageLength = 64;

    private Operation()
    {
        // For EF Core materialization.
    }

    private Operation(OperationId id, OperationType type, NodeId nodeId, ServerId? serverId, UserId? requestedBy, OperationRequester requestedVia,
        DateTimeOffset now)
    {
        Id = id;
        Type = type;
        Status = OperationStatus.Queued;
        NodeId = nodeId;
        ServerId = serverId;
        RequestedBy = requestedBy;
        RequestedVia = requestedVia;
        CreatedAt = now;
    }

    public OperationId Id { get; private set; }

    public OperationType Type { get; private set; }

    public OperationStatus Status { get; private set; }

    public NodeId NodeId { get; private set; }

    public ServerId? ServerId { get; private set; }

    public UserId? RequestedBy { get; private set; }

    public OperationRequester RequestedVia { get; private set; }

    public string? ProgressStage { get; private set; }

    public decimal? ProgressPercent { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When a user asked to cancel it (the agent then reports it as cancelled).</summary>
    public DateTimeOffset? CancelRequestedAt { get; private set; }

    public bool IsCancellable => IsActive && Type is OperationType.ServerInstall or OperationType.ServerUpdate or OperationType.BackupCreate;

    public bool IsActive => Status is OperationStatus.Queued or OperationStatus.Running;

    public static Operation Create(OperationType type, NodeId nodeId, ServerId? serverId, UserId? requestedBy, OperationRequester requestedVia,
        DateTimeOffset now) => new(OperationId.New(), type, nodeId, serverId, requestedBy, requestedVia, now);

    /// <summary>The agent accepted the command.</summary>
    public void Started(DateTimeOffset now)
    {
        if (Status == OperationStatus.Queued)
        {
            Status = OperationStatus.Running;
            StartedAt = now;
        }
    }

    public void RequestCancel(DateTimeOffset now) => CancelRequestedAt ??= now;

    public void Progressed(string stage, decimal? percent)
    {
        if (IsActive)
        {
            ProgressStage = Truncate(stage, MaxStageLength);
            ProgressPercent = percent is { } p ? Math.Clamp(p, 0, 100) : null;
        }
    }

    /// <summary>Records the outcome once; later reports of the same operation (a retried delivery) change nothing.</summary>
    public bool Complete(OperationStatus outcome, string? errorCode, string? errorMessage, DateTimeOffset now)
    {
        if (outcome is OperationStatus.Queued or OperationStatus.Running)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a final status.");
        }

        if (!IsActive)
        {
            return false;
        }

        Status = outcome;
        ErrorCode = errorCode is null ? null : Truncate(errorCode, 64);
        ErrorMessage = errorMessage is null ? null : Truncate(errorMessage, MaxErrorMessageLength);
        CompletedAt = now;
        if (outcome == OperationStatus.Succeeded)
        {
            ProgressPercent = 100;
        }

        return true;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
