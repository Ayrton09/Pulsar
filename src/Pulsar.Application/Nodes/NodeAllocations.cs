using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Application.Nodes;

public sealed record AllocationView(Guid Id, string IpAddress, int Port, Guid? ServerId, string? ServerName, string? PortName, string? Notes);

/// <summary>
/// The <c>ip:port</c> pairs of a node that servers can use (docs/architecture/02, "node_allocations"). Two allocations
/// of a node never overlap: the same port on the same address, or on any address when one of them is every interface.
/// </summary>
public sealed class NodeAllocations(IPulsarDbContext db, IAuditLog audit, TimeProvider clock)
{
    public const int MaxPortsPerRequest = 1000;

    public async Task<Result<IReadOnlyList<AllocationView>>> ListAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        var allocations = await db.NodeAllocations.AsNoTracking().Where(a => a.NodeId == nodeId)
            .OrderBy(a => a.IpAddress).ThenBy(a => a.Port).ToListAsync(cancellationToken);
        var serverIds = allocations.Where(a => a.ServerId is not null).Select(a => a.ServerId!.Value).Distinct().ToList();
        var names = await db.Servers.AsNoTracking().Where(s => serverIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        return allocations.Select(a => View(a, a.ServerId is { } id ? names.GetValueOrDefault(id) : null)).ToList();
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
        }
        catch (DbUpdateException)
        {
            // Someone added the same pair meanwhile (unique index).
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
        new(a.Id.Value, a.IpAddress, a.Port, a.ServerId?.Value, serverName, a.PortName, a.Notes);
}
