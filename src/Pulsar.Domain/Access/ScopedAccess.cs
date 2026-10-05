using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>
/// A node-scoped role granted to a user on one node (table <c>node_access</c>): it applies to the node and to every
/// server on it. Deleted with the user or the node; a role cannot be deleted while assigned.
/// </summary>
public sealed class NodeAccess
{
    private NodeAccess()
    {
        // For EF Core materialization.
    }

    public NodeAccess(UserId userId, NodeId nodeId, RoleId roleId, UserId? grantedBy, DateTimeOffset grantedAt)
    {
        UserId = userId;
        NodeId = nodeId;
        RoleId = roleId;
        GrantedBy = grantedBy;
        GrantedAt = grantedAt;
    }

    public UserId UserId { get; private set; }

    public NodeId NodeId { get; private set; }

    public RoleId RoleId { get; private set; }

    /// <summary>Always <see cref="PermissionScope.Node"/>; stored so the database can reject other roles here.</summary>
    public PermissionScope RoleScope { get; private set; } = PermissionScope.Node;

    /// <summary>Who granted it; null when that user was deleted.</summary>
    public UserId? GrantedBy { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }
}

/// <summary>
/// A server-scoped role granted to a user on one server (table <c>server_access</c>). Deleted with the user or the
/// server; a role cannot be deleted while assigned.
/// </summary>
public sealed class ServerAccess
{
    private ServerAccess()
    {
        // For EF Core materialization.
    }

    public ServerAccess(UserId userId, ServerId serverId, RoleId roleId, UserId? grantedBy, DateTimeOffset grantedAt)
    {
        UserId = userId;
        ServerId = serverId;
        RoleId = roleId;
        GrantedBy = grantedBy;
        GrantedAt = grantedAt;
    }

    public UserId UserId { get; private set; }

    public ServerId ServerId { get; private set; }

    public RoleId RoleId { get; private set; }

    /// <summary>Always <see cref="PermissionScope.Server"/>; stored so the database can reject other roles here.</summary>
    public PermissionScope RoleScope { get; private set; } = PermissionScope.Server;

    /// <summary>Who granted it; null when that user was deleted.</summary>
    public UserId? GrantedBy { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }
}
