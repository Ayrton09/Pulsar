using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Application.Realtime;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>Work the hub must start after it returns (it cannot wait for an agent's answer inside its own call).</summary>
public sealed record ServerFollowUp(ServerId ServerId, UserId? RequestedBy);

/// <summary>
/// What agents report about their servers (docs/architecture/05): state changes, operation progress and results, and
/// the full picture in every Hello (reconciliation: the agent's observation wins). Every event is checked against the
/// reporting node: an agent can only speak about its own servers and operations.
/// </summary>
public sealed partial class AgentServerEvents(IPulsarDbContext db, IAuditLog audit, OperationProgressCache progressCache, IRealtimeNotifier notifier, TimeProvider clock,
    ILogger<AgentServerEvents> logger)
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
        var percent = progress.Percent is { } p ? (decimal?)Math.Round((decimal)p, 2) : null;
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
                switch (operation.Type)
                {
                    case OperationType.ServerProvision when operation.Status == OperationStatus.Succeeded:
                        followUp = new ServerFollowUp(server.Id, operation.RequestedBy);
                        break;
                    case OperationType.ServerDelete when operation.Status == OperationStatus.Succeeded:
                        // Its allocations become free for other servers.
                        foreach (var allocation in await db.NodeAllocations.Where(a => a.ServerId == server.Id).ToListAsync(cancellationToken))
                        {
                            allocation.Release();
                        }

                        db.Servers.Remove(server);
                        // The node did it (the user's request has its own entry, server.delete_requested).
                        var node = await db.Nodes.AsNoTracking().SingleAsync(n => n.Id == nodeId, cancellationToken);
                        audit.Record(AuditActions.Server.Deleted, AuditOutcome.Success, AgentAccess.NodeActor(node),
                                new { server.Name, server.ShortId, requestedBy = operation.RequestedBy?.Value })
                            .WithServer(server.Id, server.NodeId);
                        break;
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
        var reported = snapshots.ToDictionary(s => s.ServerId);
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
            servers.FirstOrDefault(s => s.Id == operation.ServerId)?.OperationCompleted(operation, now);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciling node {NodeId} conflicted with another change; it is retried at its next connection")]
    private partial void LogReconciliationConflict(NodeId nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} on node {NodeId} was interrupted (the agent no longer has it)")]
    private partial void LogInterrupted(OperationId operationId, NodeId nodeId);
}
