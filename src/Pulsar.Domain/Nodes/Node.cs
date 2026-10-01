using Pulsar.Domain.Common;

namespace Pulsar.Domain.Nodes;

public enum NodeStatus
{
    /// <summary>Created in the panel; waiting for an agent to enroll with the token.</summary>
    PendingEnrollment,
    Active,
    /// <summary>Kept with its credential, but the agent cannot connect.</summary>
    Disabled,
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

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

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

        // Back to where it was: enrolled nodes have an inventory.
        Status = Inventory is null ? NodeStatus.PendingEnrollment : NodeStatus.Active;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>An agent may authenticate only while the node is active.</summary>
    public bool AcceptsConnections => Status == NodeStatus.Active;

    private static Result Validate(string name, string? description, string? publicHost)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
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
