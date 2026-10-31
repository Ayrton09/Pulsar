using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>A user's quota as they see it: what it allows, what their servers use of it, where and what they may create.</summary>
public sealed record QuotaView(int MaxServers, int MaxMemoryMb, int MaxCpuPercent, QuotaUsage Used, IReadOnlyList<QuotaNodeView> Nodes,
    IReadOnlyList<QuotaGameView> Games);

public sealed record QuotaUsage(int Servers, int MemoryMb, int CpuPercent);

/// <summary>A node of the quota: its name and whether it is there to create a server on now.</summary>
public sealed record QuotaNodeView(Guid Id, string Name, bool Connected);

/// <summary>A game the quota's servers may be: its newest template, and the values a server of it cannot go without.</summary>
public sealed record QuotaGameView(Guid TemplateId, string Key, string Name, string Version, int? MinMemoryMb, IReadOnlyList<TemplateVariable> Variables);

/// <summary>A server a user asks for within their quota.</summary>
public sealed record OwnedServerRequest(Guid NodeId, Guid TemplateId, string Name, int MemoryMb, int CpuPercent,
    IReadOnlyDictionary<string, string?>? Variables);

/// <summary>
/// Servers users create for themselves within their quota (Pulsar 1.0.24, docs/architecture/02 "Quotas"): on the nodes
/// it lists, with caps it counts; they own them (<see cref="PermissionCatalog.OwnerKeys"/>) and their ports are free
/// allocations of the node.
/// </summary>
public sealed class ServerQuotas(IPulsarDbContext db, TemplateCatalog templates, ServerManagement servers, IAgentConnections connections,
    AgentSessionStore sessions)
{
    public async Task<Result<QuotaView>> GetAsync(UserId user, CancellationToken cancellationToken)
    {
        var quota = await db.UserQuotas.AsNoTracking().SingleOrDefaultAsync(q => q.UserId == user, cancellationToken);
        if (quota is null)
        {
            return AccessErrors.QuotaNotFound;
        }

        var nodeIds = quota.NodeIds.ToList();
        var nodes = await db.Nodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id)).OrderBy(n => n.Name)
            .Select(n => new { n.Id, n.Name }).ToListAsync(cancellationToken);
        var games = (await templates.ListAsync(cancellationToken))
            .GroupBy(t => t.Key)
            .Select(g => g.Aggregate((a, b) => GameTemplate.CompareVersions(a.Version, b.Version) >= 0 ? a : b))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var offered = new List<QuotaGameView>();
        foreach (var game in games)
        {
            if (await templates.GetAsync(game.Id, cancellationToken) is { IsSuccess: true } details)
            {
                offered.Add(new QuotaGameView(game.Id, game.Key, game.Name, game.Version, details.Value.MinMemoryMb,
                    [.. details.Value.Variables.Where(v => v is { Required: true, Default: null, UserEditable: true })]));
            }
        }

        return new QuotaView(quota.MaxServers, quota.MaxMemoryMb, quota.MaxCpuPercent, await UsageAsync(user, cancellationToken),
            [.. nodes.Select(n => new QuotaNodeView(n.Id.Value, n.Name, Connected(n.Id)))], offered);
    }

    /// <summary>
    /// Creates <paramref name="input"/> as <paramref name="user"/>'s server, when their quota holds it (counted again with
    /// their row locked) and the node is one of its own.
    /// </summary>
    public async Task<Result<ServerDetails>> CreateAsync(UserId user, OwnedServerRequest input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var node = new NodeId(input.NodeId);
        var quota = await db.UserQuotas.AsNoTracking().SingleOrDefaultAsync(q => q.UserId == user, cancellationToken);
        if (quota is null)
        {
            return AccessErrors.QuotaNotFound;
        }

        if (!quota.NodeIds.Contains(node))
        {
            return AccessErrors.QuotaNodeNotAllowed;
        }

        // Only the values a user may set: protected ones keep their template's default.
        var details = await templates.GetAsync(input.TemplateId, cancellationToken);
        if (!details.IsSuccess)
        {
            return details.Error!;
        }

        var editable = details.Value.Variables.Where(v => v.UserEditable).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        if (input.Variables?.Keys.FirstOrDefault(k => !editable.Contains(k)) is { } locked)
        {
            return ServerErrors.VariableProtected(locked);
        }

        var owned = new OwnedServer(user, input.CpuPercent, input.MemoryMb, async ct =>
        {
            // Read again now that the user's row is locked: an administrator may have just changed it.
            var current = await db.UserQuotas.AsNoTracking().SingleOrDefaultAsync(q => q.UserId == user, ct);
            if (current is null)
            {
                return AccessErrors.QuotaNotFound;
            }

            var used = await UsageAsync(user, ct);
            return current.Fits(node, used.Servers, used.MemoryMb, used.CpuPercent, input.MemoryMb, input.CpuPercent);
        });
        return await servers.CreateAsync(node, input.TemplateId, input.Name, null, input.Variables, null, cancellationToken, owned: owned);
    }

    /// <summary>What the user's servers take of a quota: how many, and their caps added up.</summary>
    public async Task<QuotaUsage> UsageAsync(UserId user, CancellationToken cancellationToken)
    {
        var owned = await db.Servers.AsNoTracking().Where(s => s.OwnerId == user)
            .Select(s => new { s.MemoryLimitMb, s.CpuLimitPercent }).ToListAsync(cancellationToken);
        return new QuotaUsage(owned.Count, owned.Sum(s => s.MemoryLimitMb ?? 0), owned.Sum(s => s.CpuLimitPercent ?? 0));
    }

    private bool Connected(NodeId node) => connections.Find(node) is { } live && sessions.IsActive(live.SessionId);
}
