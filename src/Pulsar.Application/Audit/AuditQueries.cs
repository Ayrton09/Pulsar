using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Audit;

public sealed record AuditEntryView(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Action,
    AuditOutcome Outcome,
    AuditActorType ActorType,
    Guid? ActorUserId,
    string? ActorDisplay,
    string? TargetType,
    string? TargetId,
    string? IpAddress,
    JsonElement Metadata);

/// <summary>A page of entries, newest first. Pass <see cref="NextBefore"/> as <c>before</c> to get the next page.</summary>
public sealed record AuditPage(IReadOnlyList<AuditEntryView> Entries, Guid? NextBefore);

public sealed class AuditQueries(IPulsarDbContext db)
{
    public const int MaxPageSize = 200;

    /// <summary>
    /// Keyset pagination on the UUIDv7 id, which is ordered by time: stable while new entries arrive, and cheap at
    /// any depth (no OFFSET).
    /// </summary>
    public async Task<AuditPage> ListAsync(Guid? before, int limit, string? actionPrefix, Guid? actorUserId, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var query = db.AuditLog.AsNoTracking();
        if (before is { } cursor)
        {
            query = query.Where(e => e.Id < cursor);
        }

        if (!string.IsNullOrWhiteSpace(actionPrefix))
        {
            query = query.Where(e => e.Action.StartsWith(actionPrefix));
        }

        if (actorUserId is { } actor)
        {
            var actorId = new UserId(actor);
            query = query.Where(e => e.ActorUserId == actorId);
        }

        var rows = await query.OrderByDescending(e => e.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var page = rows.Take(limit).Select(ToView).ToList();
        return new AuditPage(page, rows.Count > limit ? page[^1].Id : null);
    }

    private static AuditEntryView ToView(AuditEntry e)
    {
        using var metadata = JsonDocument.Parse(e.Metadata);
        return new AuditEntryView(e.Id, e.OccurredAt, e.Action, e.Outcome, e.ActorType, e.ActorUserId?.Value, e.ActorDisplay,
            e.TargetType, e.TargetId, e.IpAddress?.ToString(), metadata.RootElement.Clone());
    }
}
