using Pulsar.Domain.Common;

namespace Pulsar.Domain.Updates;

/// <summary>
/// The last update of Pulsar asked of a node (Phase 19), as far as it went: asked, then what its agent reports
/// (downloading, installing) and how it ended (updated, put back, failed). One row per node, replaced by the next request.
/// Each request has its own <see cref="AttemptId"/>, which the agent and the installer send back with what they report:
/// a result of an earlier request (the installer's last result, sent again after every Hello) is never taken for this
/// one's, even for the same version, and its steps are taken in order only (one arriving late changes nothing).
/// </summary>
public sealed class NodeUpdate
{
    public const string Requested = "requested";
    public const string Downloading = "downloading";
    public const string Installing = "installing";
    public const string Succeeded = "succeeded";
    public const string RolledBack = "rolled_back";
    public const string Failed = "failed";
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

    /// <summary>This request's id, sent with the command; null for a row this panel did not ask for (<see cref="FromReport"/>).</summary>
    public Guid? AttemptId { get; private set; }

    public UserId? RequestedBy { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Whether it has not ended yet (and is still waited for).</summary>
    public bool IsUnderWay(DateTimeOffset now) => !IsFinal(Status) && now - UpdatedAt < Deadline;

    public static bool IsFinal(string status) => status is Succeeded or RolledBack or Failed;

    public static NodeUpdate Request(NodeId node, string from, string to, UserId? requestedBy, DateTimeOffset now) => new()
    {
        NodeId = node,
        Status = Requested,
        FromVersion = Bounded(from, MaxVersionLength),
        ToVersion = Bounded(to, MaxVersionLength),
        AttemptId = Guid.NewGuid(),
        RequestedBy = requestedBy,
        RequestedAt = now,
        UpdatedAt = now,
    };

    /// <summary>A new request on the same node replaces the previous one, with an id of its own.</summary>
    public void RequestAgain(string from, string to, UserId? requestedBy, DateTimeOffset now)
    {
        Status = Requested;
        FromVersion = Bounded(from, MaxVersionLength);
        ToVersion = Bounded(to, MaxVersionLength);
        Error = null;
        AttemptId = Guid.NewGuid();
        RequestedBy = requestedBy;
        RequestedAt = now;
        UpdatedAt = now;
    }

    /// <summary>The node refused this request, or did not answer it: the panel ends it with why.</summary>
    public void Refused(string error, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        Status = Failed;
        Error = Bounded(error, MaxErrorLength);
        UpdatedAt = now;
    }

    /// <summary>
    /// What the node reports (a known status, its strings checked by the caller), with the request it is about
    /// (<paramref name="attempt"/>; null from agents and installers older than request ids, and from the setups). A
    /// step of the update under way, or an end, taken only when it is about this update and moves it on: false
    /// otherwise, and when nothing changed.
    /// </summary>
    public bool Reported(string status, string? from, string? to, string? error, Guid? attempt, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(status);
        if (!IsAbout(status, to, attempt, now))
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

    /// <summary>
    /// Whether a report is about this row's update and moves it on. With a request id: this request's own reports only, in
    /// order (a step never goes back, an end is final); any other id is an earlier request's. Without one (an agent or an
    /// installer older than request ids), for a request made here: while it is under way, its version's steps in order,
    /// and an end only once the agent took it up (a failure from downloading on, the installer's result once the
    /// installer was started), since an earlier result sent again after a Hello can come at any moment before; once it
    /// ended, only a result of another version (the setups'), as one of its own version is the one kept or an earlier one.
    /// A row this panel did not ask for takes what comes, as long as an update under way keeps its version. No clock of
    /// the node's is compared with the panel's.
    /// </summary>
    private bool IsAbout(string status, string? to, Guid? attempt, DateTimeOffset now)
    {
        if (AttemptId is not { } current)
        {
            return !(IsUnderWay(now) && to is not null && ToVersion is not null && to != ToVersion);
        }

        if (attempt is { } reported)
        {
            return reported == current && !IsFinal(Status) && Rank(status) >= Rank(Status);
        }

        if (IsUnderWay(now))
        {
            if ((to is not null && to != ToVersion) || Rank(status) < Rank(Status))
            {
                return false;
            }

            return status switch
            {
                Succeeded or RolledBack => Status == Installing,
                Failed => Status is Downloading or Installing,
                _ => true,
            };
        }

        return !IsFinal(Status) || (to is not null && to != ToVersion);
    }

    private static int Rank(string status) => status switch
    {
        Requested => 0,
        Downloading => 1,
        Installing => 2,
        _ => 3,
    };

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
