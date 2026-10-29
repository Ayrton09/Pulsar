using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Scheduling;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

public sealed partial class ServerManagement
{
    /// <summary>The actor of a transfer in the audit log.</summary>
    public const string TransferActorName = "Transfer";

    /// <summary>
    /// Moves the stopped, installed server to <paramref name="nodeId"/> (Pulsar 1.0.16), as the same server: its link,
    /// access, schedules and history stay; its node and ports change. A transfer sequence backs it up on its node,
    /// installs it on the other one, restores the backup there (relayed by the panel), then deletes the old copy with that
    /// node's backups (copies in remote storage stay); a failure before then leaves it on its old node as it was. Needs
    /// <c>server.backups.create</c>, <c>server.install</c> and <c>server.delete</c> on the server and
    /// <c>server.create</c> on the node.
    /// </summary>
    public async Task<Result<ServerDetails>> TransferAsync(ServerId id, NodeId nodeId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        if (request.UserId is { } user)
        {
            var granted = await permissions.GetAsync(user, cancellationToken);
            var here = AccessTarget.ForServer(server.Id, server.NodeId);
            if (!granted.Has(Permissions.Server.Create, AccessTarget.ForNode(nodeId)))
            {
                return ResourceVisibility.Node.Any(p => granted.Has(p, AccessTarget.ForNode(nodeId))) ? ServerErrors.TransferForbidden : NodeErrors.NotFound;
            }

            if (!granted.Has(Permissions.Server.BackupsCreate, here) || !granted.Has(Permissions.Server.Install, here)
                || !granted.Has(Permissions.Server.Delete, here))
            {
                return ServerErrors.TransferForbidden;
            }
        }

        if (nodeId == server.NodeId)
        {
            return ServerErrors.SameNode;
        }

        if (server.InstallState != InstallState.Installed)
        {
            return ServerErrors.NotInstalled;
        }

        if (server.ActiveOperationId is not null)
        {
            return ServerErrors.Busy;
        }

        if (server.RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        if (Connection(nodeId) is not { } target || Connection(server.NodeId) is not { } from)
        {
            return NodeErrors.NotConnected;
        }

        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var manifest = ServerSpecs.Parse(template);
        if (!AgentReads(target, manifest)
            || (template.Key == Manifests.ManifestValidator.TestGameKey && !Announces(target, AgentCapabilities.TestGame)))
        {
            return ServerErrors.TemplateUnsupported;
        }

        if (!Announces(target, AgentCapabilities.ReceiveBackup) || !Announces(from, AgentCapabilities.SendBackup)
            || !Announces(from, AgentCapabilities.WholeBackup))
        {
            return ServerErrors.CopyUnsupported;
        }

        if (await steamAccounts.LoginForAsync(server, manifest, target, null, cancellationToken) is { IsSuccess: false } login)
        {
            return login.Error!;
        }

        // Its ports must fit on the new node: tried now, on a copy nothing saves, rather than after a backup.
        var now = clock.GetUtcNow();
        var rehearsal = await db.Servers.Include(s => s.Variables).AsNoTracking().SingleAsync(s => s.Id == id, cancellationToken);
        var fits = rehearsal.MoveTo(nodeId, now);
        fits = fits.IsSuccess ? await specs.AssignPortsAsync(rehearsal, manifest, requested: null, cancellationToken) : fits;
        db.ChangeTracker.Clear();
        if (!fits.IsSuccess)
        {
            return fits.Error!;
        }

        server = await db.Servers.SingleAsync(s => s.Id == id, cancellationToken);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var started = await ServerSequences.StartAsync(db, locks, server, SequenceOrigin.Transfer, TransferActorName,
                [new SequenceStep(SequenceStepKind.MoveTo, $"{server.NodeId.Value}:{nodeId.Value}")], now, cancellationToken);
            if (!started.IsSuccess)
            {
                return started.Error!;
            }

            audit.Record(AuditActions.Server.TransferRequested, metadata: new { from = server.NodeId.Value, to = nodeId.Value })
                .WithServer(server.Id, server.NodeId);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>A transfer of the server runs: nothing else may change it (it would start, or change ports, on the wrong node).</summary>
    private Task<bool> TransferringAsync(ServerId id, CancellationToken cancellationToken) =>
        db.ServerSequences.AnyAsync(s => s.ServerId == id && s.Origin == SequenceOrigin.Transfer && s.Status == SequenceStatus.Running, cancellationToken);
}
