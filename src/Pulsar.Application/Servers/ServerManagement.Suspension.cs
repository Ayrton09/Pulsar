using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

public sealed partial class ServerManagement
{
    /// <summary>The actor of the stops the suspension pass asks for, in the logs.</summary>
    public const string SuspensionActorName = "Suspension";

    /// <summary>
    /// Suspends the server (Pulsar 1.0.24, docs/architecture/05 "Suspension"), or changes why when it is already: it is
    /// stopped, its agent no longer starts it by itself, and only users with <c>server.suspend</c> there reach it until
    /// it is unsuspended. A node that is away is told when it comes back (<see cref="StopSuspendedAsync"/>).
    /// </summary>
    public async Task<Result<ServerDetails>> SuspendAsync(ServerId id, string? reason, CancellationToken cancellationToken)
    {
        Server? server = null;
        for (var attempt = 0; ; attempt++)
        {
            server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (server is null)
            {
                return ServerErrors.NotFound;
            }

            var suspended = server.Suspend(reason, clock.GetUtcNow());
            if (!suspended.IsSuccess)
            {
                return suspended.Error!;
            }

            audit.Record(AuditActions.Server.Suspended, metadata: new { reason = server.SuspensionReason }).WithServer(server.Id, server.NodeId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // The agent reported a state meanwhile: suspended on the fresh row.
                db.ChangeTracker.Clear();
            }
        }

        // Saved first: whatever happens to the stop, the server is suspended and nobody starts it again.
        await StopForSuspensionAsync(server, CancellationToken.None);
        await SyncSuspensionSpecAsync(server.Id);
        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>Ends the server's suspension: it stays stopped until someone starts it, and its own start options apply again.</summary>
    public async Task<Result<ServerDetails>> UnsuspendAsync(ServerId id, CancellationToken cancellationToken)
    {
        Server? server = null;
        for (var attempt = 0; ; attempt++)
        {
            server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (server is null)
            {
                return ServerErrors.NotFound;
            }

            if (!server.Unsuspend(clock.GetUtcNow()))
            {
                return await DetailsAsync(server, cancellationToken);
            }

            audit.Record(AuditActions.Server.Unsuspended).WithServer(server.Id, server.NodeId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
            }
        }

        await SyncSuspensionSpecAsync(server.Id);
        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>What another system (a billing system) calls the server from now on (1.0.24); null: nothing.</summary>
    public async Task<Result<ServerDetails>> SetExternalIdAsync(ServerId id, string? externalId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var set = server.SetExternalId(externalId, clock.GetUtcNow());
        if (!set.IsSuccess)
        {
            return set.Error!;
        }

        if (externalId is not null && await db.Servers.AnyAsync(s => s.ExternalId == externalId && s.Id != id, cancellationToken))
        {
            return ServerErrors.ExternalIdTaken;
        }

        audit.Record(AuditActions.Server.ExternalIdChanged, metadata: new { externalId }).WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ServerErrors.ExternalIdTaken; // taken at the same moment (unique index), or the row changed: asked again
        }

        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>
    /// Stops suspended servers their agents report starting or running (a node that was away when they were suspended,
    /// or that started them before it heard): the suspension pass of the sequences' loop. Returns how many it asked.
    /// </summary>
    public async Task<int> StopSuspendedAsync(CancellationToken cancellationToken)
    {
        var running = await db.Servers.AsNoTracking()
            .Where(s => s.SuspendedAt != null && (s.RuntimeState == ServerRuntimeState.Starting || s.RuntimeState == ServerRuntimeState.Running))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        var asked = 0;
        foreach (var id in running)
        {
            // Read again right before its stop: while an earlier node took its time, this one may have been unsuspended
            // (and started again), and is then left alone.
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (server is { SuspendedAt: not null } && await StopForSuspensionAsync(server, cancellationToken))
            {
                // Its agent may still have the start options of before: they follow the suspension from now on.
                await SyncSuspensionSpecAsync(server.Id);
                asked++;
            }
        }

        return asked;
    }

    /// <summary>Asks the server's node to stop it, when it may be running and the node is there; true when it was asked.</summary>
    private async Task<bool> StopForSuspensionAsync(Server server, CancellationToken cancellationToken)
    {
        if (server.RuntimeState is ServerRuntimeState.Offline or ServerRuntimeState.Crashed || Connection(server.NodeId) is null)
        {
            return false;
        }

        try
        {
            var ack = await connections.SendAsync(server.NodeId, new StopServerCommand(server.Id.Value), request.UserId, cancellationToken);
            if (ack?.Status is not (AckStatus.Accepted or AckStatus.AlreadyInState))
            {
                LogSuspensionStopRefused(server.Id, ack?.ErrorCode ?? NodeErrors.NotConnected.Code);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSuspensionStopFailed(ex, server.Id);
            return false;
        }
    }

    /// <summary>Its agent gets the start options the suspension changes (it neither starts the game with itself nor after a crash).</summary>
    private async Task SyncSuspensionSpecAsync(ServerId id)
    {
        try
        {
            if (await SyncSpecAsync(id, CancellationToken.None) is { IsSuccess: false } synced)
            {
                LogSuspensionSpecNotSent(id, synced.Error!.Code);
            }
        }
        catch (Exception ex)
        {
            // The node gets it with the next change; meanwhile the suspension pass stops whatever starts.
            LogSuspensionSpecFailed(ex, id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Suspended server {ServerId}: its node did not take the stop ({Code})")]
    private partial void LogSuspensionStopRefused(ServerId serverId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Suspended server {ServerId}: the stop could not be sent")]
    private partial void LogSuspensionStopFailed(Exception exception, ServerId serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: its suspension's start options were not sent ({Code})")]
    private partial void LogSuspensionSpecNotSent(ServerId serverId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: its suspension's start options could not be sent")]
    private partial void LogSuspensionSpecFailed(Exception exception, ServerId serverId);
}
