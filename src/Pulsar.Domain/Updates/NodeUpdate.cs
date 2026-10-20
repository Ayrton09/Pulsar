using Pulsar.Domain.Common;

namespace Pulsar.Domain.Updates;

/// <summary>
/// The last update of Pulsar asked of a node (Phase 19), as far as it went: asked, then what its agent reports
/// (downloading, installing) and how it ended (updated, put back, failed). One row per node, replaced by the next request.
/// </summary>
public sealed class NodeUpdate
{
    public const string Requested = "requested";
    public const int MaxErrorLength = 2000;
    public const int MaxStatusLength = 16;
    public const int MaxVersionLength = 32;

    /// <summary>An update that has not reported its end after this long is no longer waited for: another may be asked.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromHours(1);

    private NodeUpdate()
    {
        // For EF Core materialization.
    }

    public NodeId NodeId { get; private set; }

    public string Status { get; private set; } = Requested;

    public string? FromVersion { get; private set; }

    public string? ToVersion { get; private set; }

    public string? Error { get; private set; }

    public UserId? RequestedBy { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Whether it has not ended yet (and is still waited for).</summary>
    public bool IsUnderWay(DateTimeOffset now) => !IsFinal(Status) && now - UpdatedAt < Deadline;

    public static bool IsFinal(string status) => status is "succeeded" or "rolled_back" or "failed";

    public static NodeUpdate Request(NodeId node, string from, string to, UserId? requestedBy, DateTimeOffset now) => new()
    {
        NodeId = node,
        Status = Requested,
        FromVersion = Bounded(from, MaxVersionLength),
        ToVersion = Bounded(to, MaxVersionLength),
        RequestedBy = requestedBy,
        RequestedAt = now,
        UpdatedAt = now,
    };

    /// <summary>A new request on the same node replaces the previous one.</summary>
    public void RequestAgain(string from, string to, UserId? requestedBy, DateTimeOffset now)
    {
        Status = Requested;
        FromVersion = Bounded(from, MaxVersionLength);
        ToVersion = Bounded(to, MaxVersionLength);
        Error = null;
        RequestedBy = requestedBy;
        RequestedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// What the node reports (a known status, its strings checked by the caller). A step of the update under way, or an
    /// end. An end of another update than the one under way (the result of an earlier one, which the agent sends again
    /// after each Hello) does not replace it: false then, and when nothing changed.
    /// </summary>
    public bool Reported(string status, string? from, string? to, string? error, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(status);
        if (IsUnderWay(now) && to is not null && ToVersion is not null && to != ToVersion)
        {
            return false;
        }

        if (Status == status && ToVersion == (to ?? ToVersion) && Error == Bounded(error, MaxErrorLength))
        {
            return false;
        }

        Status = Bounded(status, MaxStatusLength)!;
        FromVersion = from is null ? FromVersion : Bounded(from, MaxVersionLength);
        ToVersion = to is null ? ToVersion : Bounded(to, MaxVersionLength);
        Error = Bounded(error, MaxErrorLength);
        UpdatedAt = now;
        return true;
    }

    /// <summary>The first report of a node that was never asked here (an update from the setups, an earlier panel's).</summary>
    public static NodeUpdate FromReport(NodeId node, string status, string? from, string? to, string? error, DateTimeOffset now) => new()
    {
        NodeId = node,
        Status = Bounded(status, MaxStatusLength)!,
        FromVersion = Bounded(from, MaxVersionLength),
        ToVersion = Bounded(to, MaxVersionLength),
        Error = Bounded(error, MaxErrorLength),
        RequestedAt = now,
        UpdatedAt = now,
    };

    private static string? Bounded(string? text, int length) => text is null ? null : text.Length <= length ? text : text[..length];
}
