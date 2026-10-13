using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Application.Nodes;

/// <summary>
/// An allocation. <see cref="InUse"/> says a server holds it; <see cref="ServerId"/>, <see cref="ServerName"/> and
/// <see cref="PortName"/> are left out when the caller cannot see that server.
/// </summary>
public sealed record AllocationView(Guid Id, string IpAddress, int Port, Guid? ServerId, string? ServerName, string? PortName, string? Notes,
    bool InUse);

/// <summary>
/// The <c>ip:port</c> pairs of a node that servers can use (docs/architecture/02, "node_allocations"). Two allocations
/// of a node never overlap: the same port on the same address, or on any address when one of them is every interface.
/// </summary>
public sealed class NodeAllocations(IPulsarDbContext db, IRowLocks locks, VisibleResources visible, IAuditLog audit, TimeProvider clock)
{
    public const int MaxPortsPerRequest = 1000;

    /// <summary>
    /// Every allocation of the node, so the ports in use are known to whoever adds or removes some. One held by a server
    /// the user cannot see says only that it is taken: its holder is that server's business.
    /// </summary>
    public async Task<Result<IReadOnlyList<AllocationView>>> ListAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        var sees = await visible.ServersAsync(cancellationToken);
        var allocations = await db.NodeAllocations.AsNoTracking().Where(a => a.NodeId == nodeId)
            .OrderBy(a => a.IpAddress).ThenBy(a => a.Port).ToListAsync(cancellationToken);
        var serverIds = allocations.Where(a => a.ServerId is { } server && (sees is null || sees(server, nodeId))).Select(a => a.ServerId!.Value)
            .Distinct().ToList();
        var names = await db.Servers.AsNoTracking().Where(s => serverIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        return allocations.Select(a => a.ServerId is not { } id ? View(a, null)
                : names.TryGetValue(id, out var name) ? View(a, name)
                : View(a, null) with { ServerId = null, PortName = null })
            .ToList();
    }

    /// <summary>Adds allocations for <paramref name="ports"/> ("27015", "27015-27030", "27015,27020") on one address.</summary>
    public async Task<Result<IReadOnlyList<AllocationView>>> CreateAsync(NodeId nodeId, string ipAddress, string ports, string? notes,
        CancellationToken cancellationToken)
    {
        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        var parsed = ParsePorts(ports);
        if (!parsed.IsSuccess)
        {
            return parsed.Error!;
        }

        var now = clock.GetUtcNow();
        var created = new List<NodeAllocation>();
        foreach (var port in parsed.Value)
        {
            var allocation = NodeAllocation.Create(nodeId, ipAddress, port, notes, now);
            if (!allocation.IsSuccess)
            {
                return allocation.Error!;
            }

            created.Add(allocation.Value);
        }

        // Checked with the node's row locked: the unique index sees the same address only, not every interface against
        // one address, so two requests at the same moment must take turns.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockNodeAsync(nodeId, cancellationToken);
        var portList = parsed.Value;
        var existing = await db.NodeAllocations.AsNoTracking().Where(a => a.NodeId == nodeId && portList.Contains(a.Port)).ToListAsync(cancellationToken);
        if (created.Any(c => existing.Any(e => e.Overlaps(c.IpAddress, c.Port))))
        {
            return NodeErrors.AllocationTaken;
        }

        db.NodeAllocations.AddRange(created);
        audit.Record(AuditActions.Node.AllocationsCreated, metadata: new { address = created[0].IpAddress, ports, count = created.Count })
            .WithNode(nodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Someone added the same pair meanwhile (unique index), without taking the lock.
            return NodeErrors.AllocationTaken;
        }

        return created.Select(a => View(a, null)).ToList();
    }

    public async Task<Result> DeleteAsync(NodeId nodeId, AllocationId id, CancellationToken cancellationToken)
    {
        var allocation = await db.NodeAllocations.SingleOrDefaultAsync(a => a.Id == id && a.NodeId == nodeId, cancellationToken);
        if (allocation is null)
        {
            return NodeErrors.AllocationNotFound;
        }

        if (allocation.IsAssigned)
        {
            return NodeErrors.AllocationInUse;
        }

        db.NodeAllocations.Remove(allocation);
        audit.Record(AuditActions.Node.AllocationDeleted, metadata: new { address = allocation.IpAddress, allocation.Port }).WithNode(nodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Assigned to a server meanwhile.
            return NodeErrors.AllocationInUse;
        }

        return Result.Success();
    }

    internal static Result<IReadOnlyList<int>> ParsePorts(string? ports)
    {
        var result = new SortedSet<int>();
        foreach (var part in (ports ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length is 0 or > 2 || !bounds.All(b => int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                return NodeErrors.AllocationPortInvalid;
            }

            var first = int.Parse(bounds[0], CultureInfo.InvariantCulture);
            var last = bounds.Length == 2 ? int.Parse(bounds[1], CultureInfo.InvariantCulture) : first;
            if (first is < 1 or > 65535 || last is < 1 or > 65535 || last < first)
            {
                return NodeErrors.AllocationPortInvalid;
            }

            if (last - first + 1 + result.Count > MaxPortsPerRequest)
            {
                return NodeErrors.AllocationRangeTooLarge;
            }

            for (var port = first; port <= last; port++)
            {
                result.Add(port);
            }
        }

        return result.Count == 0 ? NodeErrors.AllocationPortInvalid : result.ToList();
    }

    internal static AllocationView View(NodeAllocation a, string? serverName) =>
        new(a.Id.Value, a.IpAddress, a.Port, a.ServerId?.Value, serverName, a.PortName, a.Notes, a.IsAssigned);
}
