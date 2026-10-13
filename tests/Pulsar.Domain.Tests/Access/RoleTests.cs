using Pulsar.Domain.Access;

namespace Pulsar.Domain.Tests.Access;

public sealed class RoleTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateNormalizesNameDescriptionAndPermissions()
    {
        var result = Role.Create("  Moderator ", "  ", PermissionScope.Server,
            [Permissions.Server.Start, Permissions.Server.View, Permissions.Server.Start], s_now);

        var role = result.Value;
        Assert.Equal("Moderator", role.Name);
        Assert.Equal("MODERATOR", role.NormalizedName);
        Assert.Null(role.Description);
        Assert.False(role.IsSystem);
        Assert.Equal([Permissions.Server.Start, Permissions.Server.View], role.Permissions.Order(StringComparer.Ordinal));
        Assert.All(role.Grants, g => Assert.Equal(role.Id, g.RoleId));
        Assert.Equal(s_now, role.CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsEmptyNames(string name)
    {
        var result = Role.Create(name, null, PermissionScope.Global, [], s_now);

        Assert.Equal(AccessErrors.RoleNameInvalid, result.Error);
    }

    [Theory]
    [InlineData("Mod\nerator")]
    [InlineData("Mod\terator")]
    [InlineData("Mod\U0000200Berator")]
    [InlineData("Admin\U0000202Erotca")]
    [InlineData("Mod\U00002028erator")]
    [InlineData("Mod\U000E0041erator")]
    public void CreateAndUpdateRejectNamesWithHiddenOrLineBreakingCharacters(string name)
    {
        Assert.Equal(AccessErrors.RoleNameInvalid, Role.Create(name, null, PermissionScope.Global, [], s_now).Error);

        var role = Role.Create("Moderator", null, PermissionScope.Global, [], s_now).Value;
        Assert.Equal(AccessErrors.RoleNameInvalid, role.Update(name, null, [], s_now).Error);
        Assert.Equal("Moderator", role.Name);
    }

    [Fact]
    public void CreateRejectsLongNamesAndDescriptions()
    {
        Assert.Equal(AccessErrors.RoleNameInvalid,
            Role.Create(new string('a', Role.MaxNameLength + 1), null, PermissionScope.Global, [], s_now).Error);
        Assert.Equal(AccessErrors.RoleDescriptionTooLong,
            Role.Create("ok", new string('a', Role.MaxDescriptionLength + 1), PermissionScope.Global, [], s_now).Error);
    }

    [Fact]
    public void CreateRejectsUnknownPermissions()
    {
        var result = Role.Create("Role", null, PermissionScope.Global, ["server.fly"], s_now);

        Assert.Equal("role.permission_unknown", result.Error?.Code);
    }

    [Fact]
    public void ServerRolesCannotContainNodeOrGlobalPermissions()
    {
        Assert.Equal("role.permission_not_grantable",
            Role.Create("Role", null, PermissionScope.Server, [Permissions.Server.Delete], s_now).Error?.Code);
        Assert.Equal("role.permission_not_grantable",
            Role.Create("Role", null, PermissionScope.Node, [Permissions.User.Manage], s_now).Error?.Code);
    }

    [Fact]
    public void AdministratorHoldsTheWholeCatalogAndIsImmutable()
    {
        var admin = Role.CreateAdministrator(s_now);

        Assert.True(admin.IsSystem);
        Assert.Equal(PermissionScope.Global, admin.Scope);
        Assert.Empty(admin.Grants);
        Assert.Equal(PermissionCatalog.AllKeys.Order(StringComparer.Ordinal), admin.Permissions.Order(StringComparer.Ordinal));
        Assert.Equal(AccessErrors.SystemRoleImmutable, admin.Update("Other", null, [], s_now).Error);
        Assert.Equal(AccessErrors.SystemRoleImmutable, admin.EnsureDeletable().Error);
    }

    [Fact]
    public void UpdateReplacesPermissionsAndKeepsTheScope()
    {
        var role = Role.Create("Viewer", null, PermissionScope.Node, [Permissions.Server.View], s_now).Value;
        var later = s_now.AddHours(1);

        var result = role.Update("Operator", "Can start", [Permissions.Server.Start, Permissions.Node.View], later);

        Assert.True(result.IsSuccess);
        Assert.Equal("Operator", role.Name);
        Assert.Equal("Can start", role.Description);
        Assert.Equal(PermissionScope.Node, role.Scope);
        Assert.Equal([Permissions.Node.View, Permissions.Server.Start], role.Permissions.Order(StringComparer.Ordinal));
        Assert.Equal(later, role.UpdatedAt);
        Assert.Equal(s_now, role.CreatedAt);
    }

    [Fact]
    public void FailedUpdateChangesNothing()
    {
        var role = Role.Create("Viewer", null, PermissionScope.Server, [Permissions.Server.View], s_now).Value;

        var result = role.Update("Renamed", null, [Permissions.Server.Create], s_now.AddHours(1));

        Assert.False(result.IsSuccess);
        Assert.Equal("Viewer", role.Name);
        Assert.Equal([Permissions.Server.View], role.Permissions);
        Assert.Equal(s_now, role.UpdatedAt);
    }

    [Fact]
    public void AnyRoleCanRequireTwoFactorAuthenticationAndItReachesItsUsers()
    {
        var admin = Role.CreateAdministrator(s_now);
        var viewer = Role.Create("Viewer", null, PermissionScope.Global, [Permissions.Server.View], s_now).Value;
        var later = s_now.AddHours(1);

        Assert.True(admin.RequireTwoFactor(true, later));
        Assert.False(admin.RequireTwoFactor(true, later.AddHours(1)));
        Assert.True(admin.RequiresTwoFactor);
        Assert.Equal(later, admin.UpdatedAt);

        Assert.False(new EffectivePermissions.Builder().Add(viewer, AccessTarget.Global).Build().RequiresTwoFactor);
        Assert.True(new EffectivePermissions.Builder().Add(viewer, AccessTarget.Global).Add(admin, AccessTarget.Global).Build().RequiresTwoFactor);
        Assert.False(EffectivePermissions.None.RequiresTwoFactor);
    }
}
