using System.Globalization;
using System.Text;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Nodes;

public enum NodeStatus
{
    /// <summary>Created in the panel; waiting for an agent to enroll with the token.</summary>
    PendingEnrollment,
    Active,
    /// <summary>Kept with its credential, but the agent cannot connect.</summary>
    Disabled,
    /// <summary>
    /// Enrolled with a reusable token that requires approval (Phase 14): the agent may not connect until an
    /// administrator approves the node.
    /// </summary>
    PendingApproval,
}

/// <summary>Inventory reported by the agent at enrollment and on every connection.</summary>
public sealed record NodeInventorySnapshot(
    string Hostname,
    string OsDescription,
    string OsBuild,
    string? CpuModel,
    int LogicalCores,
    long MemoryTotalBytes,
    string? ServersRoot,
    string AgentVersion,
    int ProtocolVersion);

/// <summary>A Windows machine running the Pulsar agent (docs/architecture/02).</summary>
public sealed class Node
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 512;
    public const int MaxPublicHostLength = 255;

    private Node()
    {
        // For EF Core materialization.
        Name = null!;
        NormalizedName = null!;
    }

    private Node(NodeId id, string name, string? description, string? publicHost, DateTimeOffset now)
    {
        Id = id;
        Name = name;
        NormalizedName = Normalize(name);
        Description = description;
        PublicHost = publicHost;
        Status = NodeStatus.PendingEnrollment;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public NodeId Id { get; private set; }

    public string Name { get; private set; }

    public string NormalizedName { get; private set; }

    public string? Description { get; private set; }

    public NodeStatus Status { get; private set; }

    /// <summary>Host or IP shown to players for this node's servers.</summary>
    public string? PublicHost { get; private set; }

    public NodeInventorySnapshot? Inventory { get; private set; }

    public DateTimeOffset? LastSeenAt { get; private set; }

    /// <summary>
    /// Key ids of the panel keys the agent trusts, as it last reported them (null until it has). Retiring a panel key
    /// is safe once every node pins its successor.
    /// </summary>
    public IReadOnlyList<string>? PinnedPanelKeys { get; private set; }

    /// <summary>
    /// Whether the agent's last connection presented its node key as a TLS client certificate on the agents' endpoint
    /// (Phase 16). Null until it connects to a panel that records it.
    /// </summary>
    public bool? ConnectedWithCertificate { get; private set; }

    /// <summary>Whether its agent can present a client certificate and move to the agents' address, as it last said.</summary>
    public bool? AgentCanUseCertificate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>When the panel told that this node went offline; cleared (and told) when it is back.</summary>
    public DateTimeOffset? OfflineNotifiedAt { get; private set; }

    /// <summary>When the panel told that a drive of this node is low; cleared when every drive has room again.</summary>
    public DateTimeOffset? DiskLowNotifiedAt { get; private set; }

    /// <summary>The reusable enrollment token that created the node, when one did.</summary>
    public Guid? EnrollmentTokenId { get; private set; }

    /// <summary>An administrator must approve the node before its agent may connect (it stays so while disabled).</summary>
    public bool ApprovalRequired { get; private set; }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    /// <summary>Marks the node as told offline; false when it already was.</summary>
    public bool OfflineNotified(DateTimeOffset now)
    {
        if (OfflineNotifiedAt is not null)
        {
            return false;
        }

        OfflineNotifiedAt = now;
        return true;
    }

    /// <summary>Clears the offline mark; true when the node had been told offline (so its return is told too).</summary>
    public bool BackOnline()
    {
        if (OfflineNotifiedAt is null)
        {
            return false;
        }

        OfflineNotifiedAt = null;
        return true;
    }

    /// <summary>Records a low drive; false when it was already told.</summary>
    public bool DiskLowNotified(DateTimeOffset now)
    {
        if (DiskLowNotifiedAt is not null)
        {
            return false;
        }

        DiskLowNotifiedAt = now;
        return true;
    }

    public void DiskSpaceRecovered() => DiskLowNotifiedAt = null;

    public static Result<Node> Create(string name, string? description, string? publicHost, DateTimeOffset now)
    {
        var valid = Validate(name, description, publicHost);
        return valid.IsSuccess
            ? new Node(NodeId.New(), name.Trim(), Clean(description), Clean(publicHost), now)
            : valid.Error!;
    }

    public Result Update(string name, string? description, string? publicHost, DateTimeOffset now)
    {
        var valid = Validate(name, description, publicHost);
        if (!valid.IsSuccess)
        {
            return valid;
        }

        Name = name.Trim();
        NormalizedName = Normalize(name);
        Description = Clean(description);
        PublicHost = Clean(publicHost);
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// A machine enrolled with a reusable token (Phase 14): a new node named after it, enrolled already, and waiting for
    /// approval when the token says so.
    /// </summary>
    public static Result<Node> EnrolledWithToken(string name, NodeInventorySnapshot inventory, Guid tokenId, bool requireApproval, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var created = Create(name, null, null, now);
        if (!created.IsSuccess)
        {
            return created;
        }

        var node = created.Value!;
        node.EnrollmentTokenId = tokenId;
        node.ApprovalRequired = requireApproval;
        node.Inventory = inventory;
        node.Status = requireApproval ? NodeStatus.PendingApproval : NodeStatus.Active;
        node.LastSeenAt = now;
        return node;
    }

    /// <summary>An administrator lets the agent of a node that waited for approval connect.</summary>
    public Result Approve(DateTimeOffset now)
    {
        if (!ApprovalRequired)
        {
            return Result.Success();
        }

        ApprovalRequired = false;
        if (Status == NodeStatus.PendingApproval)
        {
            Status = NodeStatus.Active;
        }

        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>An agent completed enrollment (first time or re-enrollment).</summary>
    public void Enrolled(NodeInventorySnapshot inventory, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        Inventory = inventory;
        if (Status == NodeStatus.PendingEnrollment)
        {
            Status = NodeStatus.Active;
        }

        LastSeenAt = now;
        UpdatedAt = now;
    }

    public void Connected(NodeInventorySnapshot inventory, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        Inventory = inventory;
        LastSeenAt = now;
    }

    public void Seen(DateTimeOffset now) => LastSeenAt = now;

    /// <summary>How the agent connected at its last Hello (docs/architecture/03, "Client certificates").</summary>
    public void ConnectedThrough(bool certificate, bool agentCanUseCertificate)
    {
        ConnectedWithCertificate = certificate;
        AgentCanUseCertificate = agentCanUseCertificate;
    }

    public void PanelKeysPinned(IEnumerable<string> keyIds) => PinnedPanelKeys = [.. keyIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public Result Disable(DateTimeOffset now)
    {
        if (Status == NodeStatus.Disabled)
        {
            return Result.Success();
        }

        Status = NodeStatus.Disabled;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result Enable(DateTimeOffset now)
    {
        if (Status != NodeStatus.Disabled)
        {
            return Result.Success();
        }

        // Back to where it was: enrolled nodes have an inventory, and one waiting for approval still waits.
        Status = ApprovalRequired ? NodeStatus.PendingApproval : Inventory is null ? NodeStatus.PendingEnrollment : NodeStatus.Active;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>An agent may authenticate only while the node is active.</summary>
    public bool AcceptsConnections => Status == NodeStatus.Active;

    private static Result Validate(string name, string? description, string? publicHost)
    {
        // Nothing that would hide in it or break its line (zero-width spaces, direction overrides, line separators): a
        // node's name says which machine it is. Read by code point: tag characters are format characters past the BMP.
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.EnumerateRunes().Any(r => Rune.IsControl(r) || Rune.GetUnicodeCategory(r)
                is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
        {
            return NodeErrors.NameInvalid;
        }

        if (Clean(description)?.Length > MaxDescriptionLength)
        {
            return NodeErrors.DescriptionTooLong;
        }

        var host = Clean(publicHost);
        if (host is not null && (host.Length > MaxPublicHostLength || Uri.CheckHostName(host) == UriHostNameType.Unknown))
        {
            return NodeErrors.PublicHostInvalid;
        }

        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
