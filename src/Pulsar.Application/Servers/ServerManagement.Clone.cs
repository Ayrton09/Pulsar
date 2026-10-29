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
    /// <summary>The actor of a clone's copy in the audit log.</summary>
    public const string CloneActorName = "Clone";

    /// <summary>
    /// A new server like the installed <paramref name="sourceId"/> (Pulsar 1.0.16) on <paramref name="nodeId"/>, its own
    /// node or another: the same game and template version, values (its secret ones too), extra arguments, restart on
    /// crash, caps and backup limit, new ports; its Steam account only for whoever may choose one on that node. It
    /// installs as a new server does, then a clone sequence copies the source's files into it: a backup of the source,
    /// restored on the new server once it is installed, relayed by the panel. Needs <c>server.create</c> on the node and
    /// <c>server.backups.create</c> and <c>server.files.view</c> on the source (the clone's files are its files); one
    /// copy at a time from a server, and onto a node.
    /// </summary>
    public async Task<Result<ServerDetails>> CloneAsync(ServerId sourceId, NodeId nodeId, string name, CancellationToken cancellationToken)
    {
        var source = await db.Servers.AsNoTracking().Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return ServerErrors.NotFound;
        }

        if (await TransferringAsync(source.Id, cancellationToken))
        {
            return ServerErrors.Transferring;
        }

        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        var mayChooseAccount = true;
        if (request.UserId is { } user)
        {
            var granted = await permissions.GetAsync(user, cancellationToken);
            if (!granted.Has(Permissions.Server.Create, AccessTarget.ForNode(nodeId)))
            {
                return ResourceVisibility.Node.Any(p => granted.Has(p, AccessTarget.ForNode(nodeId))) ? ServerErrors.CreateForbidden : NodeErrors.NotFound;
            }

            // The clone's files are the source's, readable by whoever has it: as reading them on the source.
            if (!granted.Has(Permissions.Server.BackupsCreate, AccessTarget.ForServer(source.Id, source.NodeId))
                || !granted.Has(Permissions.Server.FilesView, AccessTarget.ForServer(source.Id, source.NodeId)))
            {
                return ServerErrors.CloneForbidden;
            }

            mayChooseAccount = granted.Has(Permissions.Server.LimitsEdit, AccessTarget.ForNode(nodeId));
        }

        if (source.InstallState != InstallState.Installed)
        {
            return ServerErrors.NotInstalled;
        }

        // Its files are copied whole: a running game holds some open, and a copy without them is never restored (1.0.17).
        if (source.RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        if (Connection(nodeId) is not { } target || Connection(source.NodeId) is not { } from)
        {
            return NodeErrors.NotConnected;
        }

        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == source.TemplateId, cancellationToken);
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

        // One copy at a time from a server and onto a node: each holds a transfer place on both nodes for as long as it runs.
        if (await db.ServerSequences.AnyAsync(q => q.Origin == SequenceOrigin.Clone && q.Status == SequenceStatus.Running
                && db.Servers.Any(s => s.Id == q.ServerId && s.NodeId == nodeId), cancellationToken)
            || (await db.ServerSequences.AsNoTracking().Where(q => q.Origin == SequenceOrigin.Clone && q.Status == SequenceStatus.Running)
                .ToListAsync(cancellationToken)).Any(q => q.Steps is [{ Text: { } text }, ..] && text == source.Id.Value.ToString()))
        {
            return ServerErrors.CloneRunning;
        }

        string shortId;
        do
        {
            shortId = Server.NewShortId();
        }
        while (await db.Servers.AnyAsync(s => s.ShortId == shortId, cancellationToken));

        var now = clock.GetUtcNow();
        var created = Server.Create(nodeId, template.Id, name, source.Description, shortId, now);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var server = created.Value;
        foreach (var variable in source.Variables)
        {
            if (variable.ProtectedValue is { } secret)
            {
                server.SetSecretVariable(variable.Key, secret, request.UserId, now);
            }
            else
            {
                server.SetVariable(variable.Key, variable.Value, request.UserId, now);
            }
        }

        var copied = server.SetStartup(source.ExtraArguments, autoStart: false, source.RestartOnCrash, now);
        copied = copied.IsSuccess ? server.SetLimits(source.CpuLimitPercent, source.MemoryLimitMb, now) : copied;
        copied = copied.IsSuccess ? server.SetBackupLimit(source.BackupLimit, now) : copied;
        copied = copied.IsSuccess ? server.SetDatabaseLimit(source.DatabaseLimit, now) : copied;
        if (!copied.IsSuccess)
        {
            return copied.Error!;
        }

        if (mayChooseAccount)
        {
            server.SetSteamAccount(source.SteamAccountId, now);
        }

        // The game's Steam branch too: the files copied are that branch's build.
        if (source.SteamBranch is not null)
        {
            if (!Announces(target, AgentCapabilities.SteamBranches))
            {
                return ServerErrors.SteamBranchAgentTooOld;
            }

            server.RestoreSteamBranch(source.SteamBranch, now);
        }

        if (await steamAccounts.LoginForAsync(server, manifest, target, null, cancellationToken) is { IsSuccess: false } login)
        {
            return login.Error!;
        }

        db.Servers.Add(server);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var assigned = await specs.AssignPortsAsync(server, manifest, requested: null, cancellationToken);
        if (!assigned.IsSuccess)
        {
            return assigned.Error!;
        }

        var valid = specs.Check(server, manifest, [.. db.NodeAllocations.Local.Where(a => a.ServerId == server.Id && a.NodeId == server.NodeId)]);
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        var copy = await ServerSequences.StartAsync(db, locks, server, SequenceOrigin.Clone, CloneActorName,
            [new SequenceStep(SequenceStepKind.CopyFrom, source.Id.Value.ToString())], now, cancellationToken);
        if (!copy.IsSuccess)
        {
            return copy.Error!;
        }

        audit.Record(AuditActions.Server.Created, metadata: new { server.Name, template = $"{template.Key}@{template.Version}", clonedFrom = source.Id.Value })
            .WithServer(server.Id, nodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // An allocation was taken by another server meanwhile (version check or unique index).
            return ServerErrors.AllocationUnavailable;
        }

        await RunOperationAsync(server, OperationType.ServerProvision,
            new ProvisionServerCommand(server.Id.Value, await specs.BuildAsync(server, template, cancellationToken)), request.UserId, OperationRequester.User,
            cancellationToken);
        return await DetailsAsync(server, cancellationToken);
    }
}
