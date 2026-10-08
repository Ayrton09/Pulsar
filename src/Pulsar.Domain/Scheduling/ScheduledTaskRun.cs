using Pulsar.Domain.Common;

namespace Pulsar.Domain.Scheduling;

public enum ScheduledRunStatus
{
    /// <summary>Taken by the scheduler; the action is being done.</summary>
    Running,

    /// <summary>Done; for an action that starts an operation (backup, update), the node accepted it and the operation has the outcome.</summary>
    Succeeded,
    Failed,

    /// <summary>Not done on purpose: missed while the panel was down, or the server was not running.</summary>
    Skipped,
}

/// <summary>One run of a scheduled task (docs/architecture/02, "Scheduling"): the history the task's page shows.</summary>
public sealed class ScheduledTaskRun
{
    public const int MaxMessageLength = 1024;
    public const int MaxErrorCodeLength = 64;

    private ScheduledTaskRun()
    {
        // For EF Core materialization.
    }

    private ScheduledTaskRun(ScheduledTask task, DateTimeOffset scheduledFor, ScheduledRunStatus status, int occurrences, DateTimeOffset now)
    {
        Id = ScheduledRunId.New();
        TaskId = task.Id;
        ServerId = task.ServerId;
        ScheduledFor = scheduledFor;
        StartedAt = now;
        Status = status;
        Occurrences = occurrences;
    }

    public ScheduledRunId Id { get; private set; }

    public ScheduledTaskId TaskId { get; private set; }

    /// <summary>The task's server (so a run's changes reach the server's watchers).</summary>
    public ServerId ServerId { get; private set; }

    /// <summary>The occurrence it is for (the first one, for missed runs).</summary>
    public DateTimeOffset ScheduledFor { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public ScheduledRunStatus Status { get; private set; }

    /// <summary>The operation the action started (a backup, an update): it has the outcome.</summary>
    public OperationId? OperationId { get; private set; }

    /// <summary>How many occurrences the row stands for: more than one only for runs missed together.</summary>
    public int Occurrences { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? Message { get; private set; }

    internal static ScheduledTaskRun Start(ScheduledTask task, DateTimeOffset scheduledFor, DateTimeOffset now) =>
        new(task, scheduledFor, ScheduledRunStatus.Running, 1, now);

    internal static ScheduledTaskRun Missed(ScheduledTask task, DateTimeOffset firstScheduledFor, int occurrences, DateTimeOffset now)
    {
        var run = new ScheduledTaskRun(task, firstScheduledFor, ScheduledRunStatus.Skipped, occurrences, now)
        {
            ErrorCode = ScheduleErrors.Missed.Code,
            Message = ScheduleErrors.Missed.Message,
            FinishedAt = now,
        };
        return run;
    }

    public void Succeeded(DateTimeOffset now, string? message = null) => End(ScheduledRunStatus.Succeeded, null, message, now);

    /// <summary>
    /// The action started an operation, which the node accepted, or which failed already (<paramref name="failedCode"/>:
    /// the node refused it).
    /// </summary>
    public void Dispatched(OperationId operationId, DateTimeOffset now, string? failedCode = null, string? failedMessage = null)
    {
        if (Status == ScheduledRunStatus.Running)
        {
            OperationId = operationId;
            End(failedCode is null ? ScheduledRunStatus.Succeeded : ScheduledRunStatus.Failed, failedCode, failedMessage, now);
        }
    }

    public void Failed(string code, string? message, DateTimeOffset now) => End(ScheduledRunStatus.Failed, code, message, now);

    public void Skipped(DomainError reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reason);
        End(ScheduledRunStatus.Skipped, reason.Code, reason.Message, now);
    }

    /// <summary>Records the outcome once; later calls change nothing.</summary>
    private void End(ScheduledRunStatus status, string? code, string? message, DateTimeOffset now)
    {
        if (Status != ScheduledRunStatus.Running)
        {
            return;
        }

        Status = status;
        ErrorCode = code is { Length: > MaxErrorCodeLength } ? code[..MaxErrorCodeLength] : code;
        Message = message is { Length: > MaxMessageLength } ? message[..MaxMessageLength] : message;
        FinishedAt = now;
    }
}
