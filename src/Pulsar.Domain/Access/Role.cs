using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>
/// A named set of permissions assignable in one <see cref="PermissionScope"/>. Roles are defined by administrators;
/// the only built-in role is <see cref="AdministratorName"/>, which always holds the whole catalog (docs/architecture/02).
/// </summary>
public sealed class Role
{
    public const string AdministratorName = "Administrator";
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 512;

    private readonly List<RoleGrant> _grants = [];

    private Role()
    {
        // For EF Core materialization.
        Name = null!;
        NormalizedName = null!;
    }

    private Role(RoleId id, string name, string? description, PermissionScope scope, bool isSystem, DateTimeOffset now)
    {
        Id = id;
        Name = name;
        NormalizedName = Normalize(name);
        Description = description;
        Scope = scope;
        IsSystem = isSystem;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public RoleId Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>Upper-cased name used for the case-insensitive uniqueness constraint.</summary>
    public string NormalizedName { get; private set; }

    public string? Description { get; private set; }

    public PermissionScope Scope { get; private set; }

    public bool IsSystem { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Stored permission rows. Empty for system roles, whose permissions are computed.</summary>
    public IReadOnlyCollection<RoleGrant> Grants => _grants;

    /// <summary>
    /// Effective permissions: the stored ones, or the whole catalog for the Administrator role, so permissions added
    /// in future versions are included automatically.
    /// </summary>
    public IReadOnlySet<string> Permissions =>
        IsSystem ? PermissionCatalog.AllKeys : _grants.Select(g => g.Permission).ToHashSet(StringComparer.Ordinal);

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    public static Result<Role> Create(
        string name, string? description, PermissionScope scope, IEnumerable<string> permissions, DateTimeOffset now)
    {
        var validation = Validate(name, description, scope, permissions, out var normalizedPermissions);
        if (!validation.IsSuccess)
        {
            return validation.Error!;
        }

        var role = new Role(RoleId.New(), name.Trim(), NormalizeDescription(description), scope, isSystem: false, now);
        role.ReplaceGrants(normalizedPermissions);
        return role;
    }

    public static Role CreateAdministrator(DateTimeOffset now) =>
        new(RoleId.New(), AdministratorName, "Full access to everything. Built-in role.", PermissionScope.Global, isSystem: true, now);

    /// <summary>Changes name, description and permissions. The scope never changes: existing assignments depend on it.</summary>
    public Result Update(string name, string? description, IEnumerable<string> permissions, DateTimeOffset now)
    {
        if (IsSystem)
        {
            return AccessErrors.SystemRoleImmutable;
        }

        var validation = Validate(name, description, Scope, permissions, out var normalizedPermissions);
        if (!validation.IsSuccess)
        {
            return validation;
        }

        Name = name.Trim();
        NormalizedName = Normalize(name);
        Description = NormalizeDescription(description);
        ReplaceGrants(normalizedPermissions);
        UpdatedAt = now;
        return Result.Success();
    }

    public Result EnsureDeletable() => IsSystem ? AccessErrors.SystemRoleImmutable : Result.Success();

    private static Result Validate(
        string name, string? description, PermissionScope scope, IEnumerable<string> permissions, out SortedSet<string> normalized)
    {
        normalized = new SortedSet<string>(StringComparer.Ordinal);

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return AccessErrors.RoleNameInvalid;
        }

        if (NormalizeDescription(description)?.Length > MaxDescriptionLength)
        {
            return AccessErrors.RoleDescriptionTooLong;
        }

        var valid = ValidatePermissions(scope, permissions);
        if (!valid.IsSuccess)
        {
            return valid;
        }

        normalized.UnionWith(permissions);
        return Result.Success();
    }

    /// <summary>Every permission exists in the catalog and can be part of a role of <paramref name="scope"/>.</summary>
    public static Result ValidatePermissions(PermissionScope scope, IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        foreach (var permission in permissions)
        {
            if (!PermissionCatalog.Exists(permission))
            {
                return AccessErrors.UnknownPermission(permission);
            }

            if (!PermissionCatalog.IsGrantableIn(permission, scope))
            {
                return AccessErrors.PermissionNotGrantable(permission, scope);
            }
        }

        return Result.Success();
    }

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    // Applies only the difference: unchanged grants keep their identity, so persistence touches only changed rows.
    private void ReplaceGrants(SortedSet<string> permissions)
    {
        _grants.RemoveAll(g => !permissions.Contains(g.Permission));
        var existing = _grants.Select(g => g.Permission).ToHashSet(StringComparer.Ordinal);
        _grants.AddRange(permissions.Where(p => !existing.Contains(p)).Select(p => new RoleGrant(Id, p)));
    }
}

/// <summary>
/// One permission granted by a role (table <c>role_permissions</c>). A class, not a record: EF Core tracks entities
/// by reference.
/// </summary>
public sealed class RoleGrant(RoleId roleId, string permission)
{
    public RoleId RoleId { get; private set; } = roleId;

    public string Permission { get; private set; } = permission;
}
