using Microsoft.EntityFrameworkCore;
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
    /// <summary>
    /// Makes a stopped server another game, or its own again from nothing (Pulsar 1.0.16): its template becomes
    /// <paramref name="templateId"/>, with <paramref name="variables"/> as its first values (as at its creation); what belonged to the old game goes
    /// (its values, mods, addons, modpack, update watch; its scheduled tasks are paused), and the node deletes its files and account before it provisions
    /// and installs it again, empty. Its name, access, limits, backups and Steam account stay; its allocations are taken
    /// first for the new game's ports. All of it is saved with the provisioning's operation, in one save: should the node
    /// refuse it, the change can be asked again. Needs the permission to delete the server too.
    /// </summary>
    public async Task<Result<ServerDetails>> ChangeGameAsync(ServerId id, Guid templateId, IReadOnlyDictionary<string, string?>? variables,
        CancellationToken cancellationToken)
    {
        var server = await db.Servers.Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (request.UserId is { } user
            && !await permissions.HasAsync(user, Permissions.Server.Delete, AccessTarget.ForServer(server.Id, server.NodeId), cancellationToken))
        {
            return ServerErrors.ChangeGameForbidden;
        }

        if (Connection(server.NodeId) is not { } connection)
        {
            return NodeErrors.NotConnected;
        }

        if (!Announces(connection, AgentCapabilities.ChangeGame))
        {
            return ServerErrors.ChangeGameUnsupported;
        }

        var current = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        if (await templates.FindOfferedAsync(templateId, cancellationToken) is not { } next)
        {
            return ServerErrors.TemplateNotFound;
        }

        var manifest = ServerSpecs.Parse(next);
        if (!AgentReads(connection, manifest)
            || (next.Key == Manifests.ManifestValidator.TestGameKey && !Announces(connection, AgentCapabilities.TestGame)))
        {
            return ServerErrors.TemplateUnsupported;
        }

        var now = clock.GetUtcNow();
        var changed = server.ChangeGame(current, next, now);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        if (await steamAccounts.LoginForAsync(server, manifest, connection, null, cancellationToken) is { IsSuccess: false } login)
        {
            return login.Error!;
        }

        // The values only whoever edits the server's settings may set, as on its Settings tab.
        var canEditProtected = request.UserId is not { } editor
            || await permissions.HasAsync(editor, Permissions.Server.SettingsEdit, AccessTarget.ForServer(server.Id, server.NodeId), cancellationToken);
        var configured = specs.SetVariables(server, manifest, variables ?? new Dictionary<string, string?>(), canEditProtected, request.UserId, now);
        if (!configured.IsSuccess)
        {
            return configured.Error!;
        }

        // Its allocations are free for the new game's ports, and taken first.
        var mine = await db.NodeAllocations.Where(a => a.ServerId == server.Id).ToListAsync(cancellationToken);
        foreach (var allocation in mine)
        {
            allocation.Release();
        }

        var ports = await specs.AssignPortsAsync(server, manifest, requested: null, cancellationToken, mine.Select(a => a.Id).ToHashSet());
        if (!ports.IsSuccess)
        {
            return ports.Error!;
        }

        var valid = specs.Check(server, manifest, [.. db.NodeAllocations.Local.Where(a => a.ServerId == server.Id)]);
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        db.ServerMods.RemoveRange(await db.ServerMods.Where(m => m.ServerId == server.Id).ToListAsync(cancellationToken));
        db.ServerAddons.RemoveRange(await db.ServerAddons.Where(a => a.ServerId == server.Id).ToListAsync(cancellationToken));
        db.ServerModpacks.RemoveRange(await db.ServerModpacks.Where(p => p.ServerId == server.Id).ToListAsync(cancellationToken));
        db.GameUpdateWatches.RemoveRange(await db.GameUpdateWatches.Where(w => w.ServerId == server.Id).ToListAsync(cancellationToken));
        // Their commands and steps were the old game's: they wait until someone looks at them again.
        foreach (var task in await db.ScheduledTasks.Where(t => t.ServerId == server.Id && t.IsEnabled).ToListAsync(cancellationToken))
        {
            task.Pause(now);
        }

        audit.Record(AuditActions.Server.GameChanged, metadata: new { from = $"{current.Key}@{current.Version}", to = $"{next.Key}@{next.Version}" })
            .WithServer(server.Id, server.NodeId);
        var operation = Operation.Create(OperationType.ServerProvision, server.NodeId, server.Id, request.UserId, OperationRequester.User, now);
        Result<Operation> run;
        try
        {
            run = await operations.RunAsync(server, operation,
                new ProvisionServerCommand(server.Id.Value, await specs.BuildAsync(server, next, cancellationToken), Clean: true), beforeSave: null,
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The agent reported a change of the server's state, or someone else changed it, in between.
            return ServerErrors.Busy;
        }
        catch (DbUpdateException)
        {
            // An allocation was taken by another server meanwhile (version check or unique index).
            return ServerErrors.AllocationUnavailable;
        }

        return run.IsSuccess ? await DetailsAsync(server, cancellationToken) : run.Error!;
    }
}
