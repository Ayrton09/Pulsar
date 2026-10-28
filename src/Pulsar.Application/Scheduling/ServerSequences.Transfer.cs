using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Scheduling;

public sealed partial class ServerSequences
{
    /// <summary>
    /// A server's move to another node (a transfer, 1.0.16), in the operations the step follows in turn, each told by its
    /// type: a backup on the old node; the server switched to the new node (its old allocations kept) and provisioned
    /// there, its install following; the backup restored there, relayed by the panel; the old copy deleted, with that
    /// node's allocations and backups (copies in remote storage stay). Until the old copy is being deleted, a failure moves
    /// the server back as it was, and the new node's copy is deleted.
    /// </summary>
    private static async Task MoveToAsync(IServiceProvider services, IPulsarDbContext db, ServerSequence sequence, Server snapshot, NodeId from,
        NodeId to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var backups = services.GetRequiredService<ServerBackups>();
        if (!sequence.StepBegun)
        {
            var made = await backups.CreateAsync(snapshot.Id, null, OperationRequester.System, cancellationToken,
                started => sequence.Began(now, Observed(snapshot), started));
            if (!made.IsSuccess || !sequence.StepBegun)
            {
                var reason = made.Error ?? ServerErrors.CommandRejected;
                sequence.StepFailed(reason.Code, reason.Message, now);
                return;
            }

            sequence.WaitUntil(now + PollInterval);
            return;
        }

        var operation = await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == sequence.OperationId, cancellationToken);
        if (operation is { IsActive: true })
        {
            sequence.WaitUntil(now + PollInterval);
            return;
        }

        var failed = operation is null || operation.Status != OperationStatus.Succeeded;
        switch (operation?.Type)
        {
            case OperationType.BackupCreate when failed:
                sequence.StepFailed(operation!.ErrorCode ?? "operation.cancelled", operation.ErrorMessage ?? "The backup did not complete.", now);
                return;
            case OperationType.BackupCreate:
                await SwitchAsync(services, db, sequence, snapshot.Id, from, to, now, cancellationToken);
                return;
            case OperationType.ServerProvision when !failed:
                // Its install follows by itself: the backup is restored once it is installed.
                if (snapshot.InstallState == InstallState.Installing || snapshot.ActiveOperationId is not null)
                {
                    sequence.WaitUntil(now + PollInterval);
                    return;
                }

                if (snapshot.InstallState != InstallState.Installed)
                {
                    await MoveBackAsync(services, db, sequence, snapshot.Id, from, to, ServerErrors.NotInstalled.Code,
                        "The server could not be installed on the new node.", now, cancellationToken);
                    return;
                }

                await RestoreMovedAsync(services, db, backups, sequence, snapshot, from, to, now, cancellationToken);
                return;
            case OperationType.BackupRestore when !failed:
                await DeleteOldCopyAsync(services, db, sequence, snapshot.Id, from, now, cancellationToken);
                return;
            case OperationType.ServerDelete:
                // The server is the new node's: an old copy left behind is said, not undone; that node's allocations are
                // freed all the same (the files there are the administrator's to delete).
                if (failed)
                {
                    foreach (var allocation in await db.NodeAllocations.Where(a => a.ServerId == snapshot.Id && a.NodeId == from).ToListAsync(cancellationToken))
                    {
                        allocation.Release();
                    }

                    sequence.StepFailed(ServerErrors.OldCopyLeft.Code, $"{ServerErrors.OldCopyLeft.Message} {operation!.ErrorMessage ?? operation.ErrorCode}".Trim(), now);
                }
                else
                {
                    sequence.StepDone(now);
                }

                return;
            default:
                // A provisioning, an install or a restore on the new node that did not complete (or an operation gone).
                await MoveBackAsync(services, db, sequence, snapshot.Id, from, to, operation?.ErrorCode ?? "operation.not_found",
                    operation?.ErrorMessage ?? "The operation did not complete.", now, cancellationToken);
                return;
        }
    }

    /// <summary>The server becomes the new node's, gets allocations there and is provisioned there, all saved with the operation.</summary>
    private static async Task SwitchAsync(IServiceProvider services, IPulsarDbContext db, ServerSequence sequence, ServerId id, NodeId from, NodeId to,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var specs = services.GetRequiredService<ServerSpecs>();
        var server = await db.Servers.Include(s => s.Variables).SingleAsync(s => s.Id == id, cancellationToken);
        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var manifest = ServerSpecs.Parse(template);
        var moved = server.MoveTo(to, now);
        if (!moved.IsSuccess)
        {
            sequence.StepFailed(moved.Error!.Code, moved.Error.Message, now);
            return;
        }

        var ports = await specs.AssignPortsAsync(server, manifest, requested: null, cancellationToken);
        var valid = ports.IsSuccess
            ? specs.Check(server, manifest, [.. db.NodeAllocations.Local.Where(a => a.ServerId == server.Id && a.NodeId == to)])
            : ports;
        DomainError? error = valid.Error;
        if (valid.IsSuccess)
        {
            var operation = Operation.Create(OperationType.ServerProvision, to, server.Id, null, OperationRequester.System, now);
            var run = await services.GetRequiredService<ServerOperations>().RunAsync(server, operation,
                new ProvisionServerCommand(server.Id.Value, await specs.BuildAsync(server, template, cancellationToken)),
                () => sequence.Began(now, Observed(server), operation.Id), cancellationToken);
            error = !run.IsSuccess ? run.Error : sequence.OperationId == operation.Id ? null : ServerErrors.CommandRejected;
        }

        if (error is not null)
        {
            // Nothing saved: back as it was before anything is.
            foreach (var allocation in db.NodeAllocations.Local.Where(a => a.ServerId == server.Id && a.NodeId == to).ToList())
            {
                allocation.Release();
            }

            server.MoveBack(from, now);
            sequence.StepFailed(error.Code, error.Message, now);
            return;
        }

        sequence.WaitUntil(now + PollInterval);
    }

    /// <summary>The backup made on the old node, restored on the new one (relayed by the panel).</summary>
    private static async Task RestoreMovedAsync(IServiceProvider services, IPulsarDbContext db, ServerBackups backups, ServerSequence sequence, Server server,
        NodeId from, NodeId to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var backup = await db.Backups.AsNoTracking()
            .Where(b => b.ServerId == server.Id && b.NodeId == from && b.Status == BackupStatus.Completed && b.CreatedAt >= sequence.CreatedAt)
            .OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var restore = backup is null
            ? ServerErrors.BackupNotFound
            : await backups.RestoreFromAsync(server.Id, server.Id, backup.Id, OperationRequester.System, cancellationToken,
                started => sequence.Began(now, Observed(server), started));
        if (!restore.IsSuccess || sequence.OperationId != restore.Value.Id)
        {
            // Its files are still the old node's: back there, rather than left on a new node with none of them.
            await MoveBackAsync(services, db, sequence, server.Id, from, to, restore.Error?.Code ?? ServerErrors.CommandRejected.Code,
                restore.Error?.Message ?? ServerErrors.CommandRejected.Message, now, cancellationToken);
            return;
        }

        sequence.WaitUntil(now + PollInterval);
    }

    /// <summary>
    /// The move is done: the old node's backups the server keeps no remote copy of are forgotten, and its old copy, with
    /// them, is deleted on that node (whose allocations its end frees).
    /// </summary>
    private static async Task DeleteOldCopyAsync(IServiceProvider services, IPulsarDbContext db, ServerSequence sequence, ServerId id, NodeId from,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleAsync(s => s.Id == id, cancellationToken);
        db.Backups.RemoveRange(await db.Backups.Where(b => b.ServerId == id && b.NodeId == from && b.RemoteStatus != RemoteCopyStatus.Copied)
            .ToListAsync(cancellationToken));
        var operation = Operation.Create(OperationType.ServerDelete, from, id, null, OperationRequester.System, now);
        var run = await services.GetRequiredService<ServerOperations>().RunAsync(server, operation, new DeleteServerCommand(id.Value),
            () => sequence.Began(now, Observed(server), operation.Id), cancellationToken);
        if (!run.IsSuccess || sequence.OperationId != run.Value.Id)
        {
            var reason = run.IsSuccess ? ServerErrors.CommandRejected : run.Error!;
            sequence.StepFailed(ServerErrors.OldCopyLeft.Code, $"{ServerErrors.OldCopyLeft.Message} {reason.Message}", now);
            return;
        }

        sequence.WaitUntil(now + PollInterval);
    }

    /// <summary>
    /// A move that failed before it was done: the server is the old node's again, installed and stopped, its allocations
    /// there kept; the new node's are freed and its copy there deleted (that operation is not followed).
    /// </summary>
    private static async Task MoveBackAsync(IServiceProvider services, IPulsarDbContext db, ServerSequence sequence, ServerId id, NodeId from, NodeId to,
        string code, string message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleAsync(s => s.Id == id, cancellationToken);
        if (server.NodeId == to && server.ActiveOperationId is null)
        {
            db.Backups.RemoveRange(await db.Backups.Where(b => b.ServerId == id && b.NodeId == to).ToListAsync(cancellationToken));
            // The new node's allocations are free at once, whether or not its copy can be deleted now.
            foreach (var allocation in await db.NodeAllocations.Where(a => a.ServerId == id && a.NodeId == to).ToListAsync(cancellationToken))
            {
                allocation.Release();
            }

            var cleanup = Operation.Create(OperationType.ServerDelete, to, id, null, OperationRequester.System, now);
            server.MoveBack(from, now);
            // Sent from the old node's side: the server is the old node's again, the delete only the new node's copy's.
            await services.GetRequiredService<ServerOperations>().RunAsync(server, cleanup, new DeleteServerCommand(id.Value), beforeSave: null,
                cancellationToken);
        }

        sequence.StepFailed(code, message, now);
    }
}
