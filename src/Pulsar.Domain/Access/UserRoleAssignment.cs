using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>
/// A global role granted to a user (table <c>user_roles</c>). Node and server assignments get their own tables once
/// nodes and servers exist, so each one has real foreign keys and cascading deletes.
/// </summary>
public sealed class UserRoleAssignment
{
    private UserRoleAssignment()
    {
        // For EF Core materialization.
    }

    public UserRoleAssignment(UserId userId, RoleId roleId, UserId? grantedBy, DateTimeOffset grantedAt)
    {
        UserId = userId;
        RoleId = roleId;
        GrantedBy = grantedBy;
        GrantedAt = grantedAt;
    }

    public UserId UserId { get; private set; }

    public RoleId RoleId { get; private set; }

    /// <summary>Always <see cref="PermissionScope.Global"/>; stored so the database can reject non-global roles here.</summary>
    public PermissionScope RoleScope { get; private set; } = PermissionScope.Global;

    /// <summary>Who granted it; null for the initial setup or when that user was deleted.</summary>
    public UserId? GrantedBy { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }
}
