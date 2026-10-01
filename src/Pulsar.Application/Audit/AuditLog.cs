using System.Text.Json;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Audit;

namespace Pulsar.Application.Audit;

/// <summary>
/// Records audit entries for the current request. Entries are added to the unit of work, so they are committed
/// atomically with the change they describe; call SaveChanges yourself when nothing else changes (failed logins).
/// </summary>
public interface IAuditLog
{
    AuditEntry Record(string action, AuditOutcome outcome = AuditOutcome.Success, object? metadata = null);

    /// <summary>Records on behalf of an explicit actor (e.g. the user who just signed in or failed to).</summary>
    AuditEntry Record(string action, AuditOutcome outcome, AuditActor actor, object? metadata = null);
}

internal sealed class AuditLog(IPulsarDbContext db, IRequestContext request, TimeProvider clock) : IAuditLog
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public AuditEntry Record(string action, AuditOutcome outcome = AuditOutcome.Success, object? metadata = null) =>
        Record(action, outcome, request.Actor, metadata);

    public AuditEntry Record(string action, AuditOutcome outcome, AuditActor actor, object? metadata = null)
    {
        // Metadata objects are small, purpose-built anonymous types or records: never pass DTOs or secrets here.
        var entry = AuditEntry.Create(clock.GetUtcNow(), action, outcome, actor, request.RequestInfo);
        if (metadata is not null)
        {
            entry.WithMetadata(JsonSerializer.Serialize(metadata, s_json));
        }

        db.AuditLog.Add(entry);
        return entry;
    }
}
