using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

public sealed partial class ServerManagement
{
    /// <summary>Changes only the panel's display name, including while the game or its node is offline.</summary>
    public Task<Result<ServerDetails>> RenameAsync(ServerId id, string? name, CancellationToken cancellationToken) =>
        RenameAsync(id, name, 0, cancellationToken);

    private async Task<Result<ServerDetails>> RenameAsync(ServerId id, string? name, int attempt, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        // Repeat the resource check on the current row when a concurrent state change requires a retry.
        if (request.UserId is { } user)
        {
            var granted = await permissions.GetAsync(user, cancellationToken);
            var target = AccessTarget.ForServer(server.Id, server.NodeId);
            if (!granted.Has(Permissions.Server.SettingsEdit, target))
            {
                return ResourceVisibility.Server.Any(p => granted.Has(p, target)) ? ServerErrors.RenameForbidden : ServerErrors.NotFound;
            }

            if (server.SuspendedAt is not null && !granted.Has(Permissions.Server.Suspend, target))
            {
                return ServerErrors.Suspended;
            }
        }

        if (StringComparer.Ordinal.Equals(server.Name, name?.Trim()))
        {
            return await DetailsAsync(server, cancellationToken);
        }

        var changed = server.Rename(name, clock.GetUtcNow());
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        audit.Record(AuditActions.Server.Renamed, metadata: new { name = server.Name }).WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException) when (attempt < 2)
        {
            // The failed save committed neither the rename nor its audit event. Recheck the fresh resource.
            db.ChangeTracker.Clear();
            return await RenameAsync(id, name, attempt + 1, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServerErrors.Busy;
        }

        return await DetailsAsync(server, cancellationToken);
    }
}
