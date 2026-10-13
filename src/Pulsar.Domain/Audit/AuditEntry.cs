using System.Net;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Audit;

public enum AuditOutcome
{
    Success,
    Failure,
    Denied,
}

public enum AuditActorType
{
    User,
    Node,
    Schedule,
    System,
    Anonymous,

    /// <summary>A user through one of their API keys (the user id is the key's user; the display names both).</summary>
    ApiKey,
}

/// <summary>Who performed an audited action, captured at the time of the event.</summary>
public sealed record AuditActor(AuditActorType Type, UserId? UserId, string? Display)
{
    public static AuditActor System { get; } = new(AuditActorType.System, null, "system");

    public static AuditActor Anonymous { get; } = new(AuditActorType.Anonymous, null, null);

    public static AuditActor ForUser(UserId userId, string? display) => new(AuditActorType.User, userId, display);

    public static AuditActor ForApiKey(UserId userId, string? userName, string keyName) => new(AuditActorType.ApiKey, userId, $"{userName} · {keyName}");
}

/// <summary>Request details recorded with an entry.</summary>
public sealed record AuditRequestInfo(IPAddress? IpAddress, string? UserAgent, string? CorrelationId)
{
    public static AuditRequestInfo None { get; } = new(null, null, null);
}

/// <summary>
/// One append-only audit record (table <c>audit_logs</c>). It has no foreign keys on purpose, so it survives the
/// deletion of the users, nodes and servers it mentions. <see cref="Metadata"/> is sanitized JSON and never holds secrets.
/// </summary>
public sealed class AuditEntry
{
    public const int MaxActionLength = 64;
    public const int MaxActorDisplayLength = 128;
    public const int MaxTargetTypeLength = 32;
    public const int MaxTargetIdLength = 512;
    public const int MaxUserAgentLength = 512;
    public const int MaxCorrelationIdLength = 64;

    private AuditEntry()
    {
        // For EF Core materialization.
        Action = null!;
        Metadata = null!;
    }

    private AuditEntry(DateTimeOffset occurredAt, string action, AuditOutcome outcome, AuditActor actor, AuditRequestInfo request)
    {
        Id = Guid.CreateVersion7(occurredAt);
        OccurredAt = occurredAt;
        Action = action;
        Outcome = outcome;
        ActorType = actor.Type;
        ActorUserId = actor.UserId;
        ActorDisplay = Truncate(actor.Display, MaxActorDisplayLength);
        IpAddress = request.IpAddress;
        UserAgent = Truncate(request.UserAgent, MaxUserAgentLength);
        CorrelationId = Truncate(request.CorrelationId, MaxCorrelationIdLength);
        Metadata = "{}";
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Key from <see cref="AuditActions"/>.</summary>
    public string Action { get; private set; }

    public AuditOutcome Outcome { get; private set; }

    public AuditActorType ActorType { get; private set; }

    public UserId? ActorUserId { get; private set; }

    public string? ActorDisplay { get; private set; }

    public NodeId? NodeId { get; private set; }

    public ServerId? ServerId { get; private set; }

    public string? TargetType { get; private set; }

    public string? TargetId { get; private set; }

    public IPAddress? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public string? CorrelationId { get; private set; }

    public string Metadata { get; private set; }

    public static AuditEntry Create(
        DateTimeOffset occurredAt, string action, AuditOutcome outcome, AuditActor actor, AuditRequestInfo request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(action.Length, MaxActionLength, nameof(action));
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        return new AuditEntry(occurredAt, action, outcome, actor, request);
    }

    public AuditEntry WithTarget(string type, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(type.Length, MaxTargetTypeLength, nameof(type));
        TargetType = type;
        TargetId = Truncate(id, MaxTargetIdLength);
        return this;
    }

    public AuditEntry WithNode(NodeId node)
    {
        NodeId = node;
        return this;
    }

    public AuditEntry WithServer(ServerId server, NodeId node)
    {
        ServerId = server;
        NodeId = node;
        return this;
    }

    /// <summary>Sets the metadata JSON object, already serialized and sanitized by the caller.</summary>
    public AuditEntry WithMetadata(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        Metadata = json;
        return this;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : TextLimits.Truncate(value, maxLength);
}
