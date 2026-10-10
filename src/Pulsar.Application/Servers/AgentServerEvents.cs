using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Databases;
using Pulsar.Application.Nodes;
using Pulsar.Application.Notifications;
using Pulsar.Application.Realtime;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

public enum ServerFollowUpKind
{
    /// <summary>The install that follows a successful provisioning.</summary>
    ContinueInstall,

    /// <summary>Retention, after a new backup.</summary>
    PruneBackups,
}

/// <summary>Work the hub must start after it returns (it cannot wait for an agent's answer inside its own call).</summary>
public sealed record ServerFollowUp(ServerId ServerId, UserId? RequestedBy, ServerFollowUpKind Kind = ServerFollowUpKind.ContinueInstall);

/// <summary>
/// What agents report about their servers (docs/architecture/05): state changes, operation progress and results, and
/// the full picture in every Hello (reconciliation: the agent's observation wins). Every event is checked against the
/// reporting node: an agent can only speak about its own servers and operations.
/// </summary>
public sealed partial class AgentServerEvents(IPulsarDbContext db, IAuditLog audit, OperationProgressCache progressCache, IRealtimeNotifier notifier,
    Notifier notifications, ServerDatabases databases, TimeProvider clock, ILogger<AgentServerEvents> logger)
{
    private static readonly TimeSpan s_queuedGrace = TimeSpan.FromMinutes(1);

    public async Task StateChangedAsync(NodeId nodeId, ServerStateChanged change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == new ServerId(change.ServerId), cancellationToken);
            if (server is null || server.NodeId != nodeId)
            {
                LogForeignServer(nodeId, change.ServerId);
                return;
            }

            if (!server.Observe(Map(change.State), Map(change.Reason), change.ExitCode, change.StateSequence, change.At, snapshot: false))
            {
                return;
            }

            switch (change.Reason)
            {
                case StateChangeReason.Crashed or StateChangeReason.CrashLoop or StateChangeReason.MemoryLimit:
                    await notifications.ServerCrashedAsync(server, change.ExitCode, change.Reason == StateChangeReason.CrashLoop, cancellationToken,
                        memoryLimit: change.Reason == StateChangeReason.MemoryLimit);
                    break;
                case StateChangeReason.StartFailed or StateChangeReason.ReadinessTimedOut:
                    await notifications.ServerStartFailedAsync(server, change.Reason == StateChangeReason.ReadinessTimedOut, change.Detail, cancellationToken);
                    break;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A user request changed the server meanwhile: take the change again on the fresh row.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Progress is kept in memory for the API (live) and written to the database only when the stage changes or every
    /// 15 seconds (docs/architecture/02): SteamCMD reports often.
    /// </summary>
    public async Task OperationProgressAsync(NodeId nodeId, OperationProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        // The agent's number is not trusted: not a number (or infinite) is left out, the rest kept within 0–100.
        var percent = progress.Percent is { } p && double.IsFinite(p) ? (decimal?)Math.Round((decimal)Math.Clamp(p, 0, 100), 2) : null;
        var operation = await db.Operations.SingleOrDefaultAsync(o => o.Id == new OperationId(progress.OperationId) && o.NodeId == nodeId,
            cancellationToken);
        if (operation is not { IsActive: true })
        {
            return;
        }

        if (operation.ServerId is { } serverId)
        {
            notifier.OperationProgress(serverId, progress.OperationId, progress.Stage, percent);
        }

        if (!progressCache.Update(progress.OperationId, progress.Stage, percent))
        {
            return;
        }

        operation.Progressed(progress.Stage, percent);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Progress is advisory; the next report or the result supersedes it.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Records an operation's result once (deliveries are retried by the agent) and applies it to its server.</summary>
    public async Task<ServerFollowUp?> OperationCompletedAsync(NodeId nodeId, OperationCompleted completed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completed);
        for (var attempt = 0; ; attempt++)
        {
            var operation = await db.Operations.SingleOrDefaultAsync(o => o.Id == new OperationId(completed.OperationId) && o.NodeId == nodeId,
                cancellationToken);
            if (operation is null)
            {
                LogUnknownOperation(nodeId, completed.OperationId);
                return null;
            }

            var now = clock.GetUtcNow();
            progressCache.Remove(completed.OperationId);
            if (!operation.Complete(Map(completed.Outcome), completed.ErrorCode, completed.Message, now))
            {
                return null;
            }

            var server = operation.ServerId is { } serverId ? await db.Servers.SingleOrDefaultAsync(s => s.Id == serverId, cancellationToken) : null;
            ServerFollowUp? followUp = null;
            if (server is not null)
            {
                server.OperationCompleted(operation, now);
                // Addons installed, updated after a game update, or removed.
                if (operation.Status == OperationStatus.Succeeded && completed.Result is AddonsResult addons)
                {
                    await ServerAddons.ApplyAsync(db, server, operation.Type, addons, now, cancellationToken);
                }

                switch (operation.Type)
                {
                    case OperationType.ServerProvision when operation.Status == OperationStatus.Succeeded:
                        followUp = new ServerFollowUp(server.Id, operation.RequestedBy);
                        break;
                    case OperationType.BackupCreate:
                        if (await BackupEndedAsync(server, operation, completed.Result, now, cancellationToken) && server.BackupLimit is not null)
                        {
                            followUp = new ServerFollowUp(server.Id, operation.RequestedBy, ServerFollowUpKind.PruneBackups);
                        }

                        break;
                    case OperationType.ServerDelete when operation.Status == OperationStatus.Succeeded:
                        // Its allocations become free for other servers.
                        foreach (var allocation in await db.NodeAllocations.Where(a => a.ServerId == server.Id).ToListAsync(cancellationToken))
                        {
                            allocation.Release();
                        }

                        // Its databases go with it, dropped on their hosts first (no new one can come while it is being deleted).
                        await databases.DropAllAsync(server, cancellationToken);
                        db.Servers.Remove(server);
                        // The node did it (the user's request has its own entry, server.delete_requested).
                        var node = await db.Nodes.AsNoTracking().SingleAsync(n => n.Id == nodeId, cancellationToken);
                        audit.Record(AuditActions.Server.Deleted, AuditOutcome.Success, AgentAccess.NodeActor(node),
                                new { server.Name, server.ShortId, requestedBy = operation.RequestedBy?.Value })
                            .WithServer(server.Id, server.NodeId);
                        break;
                }

                // A scheduled update (or install) that failed after the node took it: the task's run said "dispatched".
                if (operation is { RequestedVia: OperationRequester.Schedule, Status: OperationStatus.Failed } && operation.Type != OperationType.BackupCreate)
                {
                    var taskName = await db.ScheduledTaskRuns.AsNoTracking().Where(r => r.OperationId == operation.Id)
                        .Join(db.ScheduledTasks.AsNoTracking(), r => r.TaskId, t => t.Id, (r, t) => t.Name)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (taskName is not null)
                    {
                        await notifications.ScheduledTaskFailedAsync(server, taskName, operation.ErrorMessage ?? operation.ErrorCode, cancellationToken);
                    }
                }
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                LogOperationCompleted(completed.OperationId, operation.Type, operation.Status, completed.ErrorCode);
                return followUp;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// The agent's Hello: every server it reports takes its state (a restarted agent counts sequences from zero again),
    /// and operations the panel thinks are running that the agent does not know are interrupted.
    /// </summary>
    public async Task ReconcileAsync(NodeId nodeId, IReadOnlyList<ServerSnapshot> snapshots, IReadOnlyList<Guid> operationsInFlight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(operationsInFlight);
        var now = clock.GetUtcNow();
        var servers = await db.Servers.Where(s => s.NodeId == nodeId).ToListAsync(cancellationToken);
        var reported = new Dictionary<Guid, ServerSnapshot>();
        foreach (var snapshot in snapshots)
        {
            // A server reported twice: the first counts (a duplicate must not keep the node from being reconciled).
            if (!reported.TryAdd(snapshot.ServerId, snapshot))
            {
                LogDuplicateSnapshot(nodeId, snapshot.ServerId);
            }
        }

        foreach (var server in servers)
        {
            if (reported.Remove(server.Id.Value, out var snapshot))
            {
                server.Observe(Map(snapshot.State), ServerStateReason.Reconciled, snapshot.ExitCode, snapshot.StateSequence, now, snapshot: true);
            }
            else if (server.InstallState == InstallState.Installed)
            {
                LogMissingOnNode(nodeId, server.Id);
            }
        }

        foreach (var unknown in reported.Keys)
        {
            LogUnknownOnNode(nodeId, unknown);
        }

        var inFlight = operationsInFlight.Select(id => new OperationId(id)).ToHashSet();
        var active = await db.Operations.Where(o => o.NodeId == nodeId && (o.Status == OperationStatus.Queued || o.Status == OperationStatus.Running))
            .ToListAsync(cancellationToken);
        // A queued operation younger than a minute may still be on its way to the agent.
        foreach (var operation in active.Where(o => !inFlight.Contains(o.Id) && (o.Status == OperationStatus.Running || now - o.CreatedAt > s_queuedGrace)))
        {
            operation.Complete(OperationStatus.Interrupted, "operation.interrupted", "The agent restarted or lost the operation.", now);
            progressCache.Remove(operation.Id.Value);
            var server = servers.FirstOrDefault(s => s.Id == operation.ServerId);
            server?.OperationCompleted(operation, now);
            if (operation.Type == OperationType.BackupCreate)
            {
                await BackupEndedAsync(server, operation, null, now, cancellationToken);
            }

            LogInterrupted(operation.Id, nodeId);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // An event or a user request changed a server meanwhile; the next Hello reconciles again.
            db.ChangeTracker.Clear();
            LogReconciliationConflict(nodeId);
        }
    }

    /// <summary>The backup its operation was making ends with it; true when it is complete. A failed one is notified.</summary>
    private async Task<bool> BackupEndedAsync(Server? server, Operation operation, CommandResult? result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var backup = await db.Backups.SingleOrDefaultAsync(b => b.OperationId == operation.Id, cancellationToken);
        if (backup is null)
        {
            return false;
        }

        switch (operation.Status)
        {
            case OperationStatus.Succeeded when result is BackupResult made && !Plausible(made):
                backup.Ended(BackupStatus.Failed, "The node reported a negative size or count for the archive.", now);
                break;
            case OperationStatus.Succeeded when result is BackupResult made && Sha256(made.Sha256) is { } hash:
                backup.Completed(made.Size, made.FileCount, made.SkippedFiles, hash, now, CopyMode(made.Capture?.Mode), made.Capture?.SnapshotError,
                    GameSave(made.Capture?.Save));
                if (backup.RemoteStatus == RemoteCopyStatus.Pending)
                {
                    backup.RemoteCopyEnded(made.RemoteCopied, made.RemoteError);
                    if (!made.RemoteCopied && server is not null)
                    {
                        await notifications.BackupFailedAsync(server, backup.Name, backup.RemoteError, remoteCopy: true, cancellationToken);
                    }
                }

                return true;
            case OperationStatus.Succeeded:
                backup.Ended(BackupStatus.Failed, "The node reported no archive.", now);
                break;
            case OperationStatus.Cancelled:
                backup.Ended(BackupStatus.Cancelled, null, now);
                return false;
            default:
                backup.Ended(BackupStatus.Failed, operation.ErrorMessage ?? operation.ErrorCode, now);
                break;
        }

        if (server is not null)
        {
            await notifications.BackupFailedAsync(server, backup.Name, backup.ErrorMessage, remoteCopy: false, cancellationToken);
        }

        return false;
    }

    // Skipped files are not in the archive, so they can outnumber the files in it.
    private static bool Plausible(BackupResult made) => made is { Size: >= 0, FileCount: >= 0, SkippedFiles: >= 0 };

    // A value this panel does not know (a newer agent's) is left unknown.
    private static BackupCopyMode? CopyMode(BackupCaptureMode? mode) => mode switch
    {
        BackupCaptureMode.Stopped => BackupCopyMode.Stopped,
        BackupCaptureMode.Snapshot => BackupCopyMode.Snapshot,
        BackupCaptureMode.Live => BackupCopyMode.Live,
        _ => null,
    };

    private static GameSaveResult? GameSave(GameSaveOutcome? outcome) => outcome switch
    {
        GameSaveOutcome.Confirmed => GameSaveResult.Confirmed,
        GameSaveOutcome.Waited => GameSaveResult.Waited,
        GameSaveOutcome.Unconfirmed => GameSaveResult.Unconfirmed,
        _ => null,
    };

    private static byte[]? Sha256(string hex)
    {
        try
        {
            return hex.Length == 64 ? Convert.FromHexString(hex) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ServerRuntimeState Map(RuntimeState state) => state switch
    {
        RuntimeState.Offline => ServerRuntimeState.Offline,
        RuntimeState.Starting => ServerRuntimeState.Starting,
        RuntimeState.Running => ServerRuntimeState.Running,
        RuntimeState.Stopping => ServerRuntimeState.Stopping,
        RuntimeState.Crashed => ServerRuntimeState.Crashed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static ServerStateReason Map(StateChangeReason reason) => reason switch
    {
        StateChangeReason.Requested => ServerStateReason.Requested,
        StateChangeReason.Ready => ServerStateReason.Ready,
        StateChangeReason.ReadinessTimedOut => ServerStateReason.ReadinessTimedOut,
        StateChangeReason.ExitedByItself => ServerStateReason.ExitedByItself,
        StateChangeReason.Crashed => ServerStateReason.Crashed,
        StateChangeReason.CrashLoop => ServerStateReason.CrashLoop,
        StateChangeReason.StopTimedOut => ServerStateReason.StopTimedOut,
        StateChangeReason.Killed => ServerStateReason.Killed,
        StateChangeReason.AutoRestart => ServerStateReason.AutoRestart,
        StateChangeReason.StartFailed => ServerStateReason.StartFailed,
        StateChangeReason.AgentStopping => ServerStateReason.AgentStopping,
        StateChangeReason.AgentStarted => ServerStateReason.AgentStarted,
        StateChangeReason.MemoryLimit => ServerStateReason.MemoryLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    private static OperationStatus Map(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Succeeded => OperationStatus.Succeeded,
        OperationOutcome.Failed => OperationStatus.Failed,
        OperationOutcome.Cancelled => OperationStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} reported server {ServerId}, which is not one of its servers")]
    private partial void LogForeignServer(NodeId nodeId, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} reported operation {OperationId}, which is not one of its operations")]
    private partial void LogUnknownOperation(NodeId nodeId, Guid operationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Operation {OperationId} ({Type}) ended: {Status} {ErrorCode}")]
    private partial void LogOperationCompleted(Guid operationId, OperationType type, OperationStatus status, string? errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} is installed on node {NodeId} according to the panel, but the agent does not know it")]
    private partial void LogMissingOnNode(NodeId nodeId, ServerId serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} runs server {ServerId}, which the panel does not know; it is left alone")]
    private partial void LogUnknownOnNode(NodeId nodeId, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} reported server {ServerId} more than once at Hello; the first report counts")]
    private partial void LogDuplicateSnapshot(NodeId nodeId, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciling node {NodeId} conflicted with another change; it is retried at its next connection")]
    private partial void LogReconciliationConflict(NodeId nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} on node {NodeId} was interrupted (the agent no longer has it)")]
    private partial void LogInterrupted(OperationId operationId, NodeId nodeId);
}
