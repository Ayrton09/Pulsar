using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Tests.Access;

public sealed class AccessRulesTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly UserId s_actor = UserId.New();
    private static readonly UserId s_target = UserId.New();
    private static readonly NodeId s_node = NodeId.New();
    private static readonly NodeId s_otherNode = NodeId.New();
    private static readonly ServerId s_server = ServerId.New();

    [Fact]
    public void ServerPermissionsComeFromGlobalNodeAndServerAssignments()
    {
        var permissions = new EffectivePermissions.Builder()
            .Add(Role("Global", PermissionScope.Global, Permissions.Audit.View), AccessTarget.Global)
            .Add(Role("Node", PermissionScope.Node, Permissions.Server.Start), AccessTarget.ForNode(s_node))
            .Add(Role("Server", PermissionScope.Server, Permissions.Server.ConsoleView), AccessTarget.ForServer(s_server, s_node))
            .Build();

        var onServer = AccessTarget.ForServer(s_server, s_node);
        Assert.True(permissions.Has(Permissions.Audit.View, onServer));
        Assert.True(permissions.Has(Permissions.Server.Start, onServer));
        Assert.True(permissions.Has(Permissions.Server.ConsoleView, onServer));

        // Narrower assignments never apply to wider or unrelated targets.
        Assert.False(permissions.Has(Permissions.Server.ConsoleView, AccessTarget.ForNode(s_node)));
        Assert.False(permissions.Has(Permissions.Server.Start, AccessTarget.Global));
        Assert.False(permissions.Has(Permissions.Server.Start, AccessTarget.ForNode(s_otherNode)));
        Assert.False(permissions.Has(Permissions.Server.ConsoleView, AccessTarget.ForServer(ServerId.New(), s_node)));
    }

    [Fact]
    public void AddingARoleAtTheWrongScopeIsABug()
    {
        var serverRole = Role("Server", PermissionScope.Server, Permissions.Server.View);

        Assert.Throws<ArgumentException>(() => new EffectivePermissions.Builder().Add(serverRole, AccessTarget.Global));
    }

    [Fact]
    public void AdministratorCanGrantAnything()
    {
        var actor = new EffectivePermissions.Builder().Add(Domain.Access.Role.CreateAdministrator(s_now), AccessTarget.Global).Build();
        var role = Role("Ops", PermissionScope.Node, Permissions.Node.Manage, Permissions.Server.Delete);

        Assert.True(AccessRules.CanChangeAssignment(s_actor, actor, s_target, role, AccessTarget.ForNode(s_node)).IsSuccess);
    }

    [Fact]
    public void NobodyChangesTheirOwnAssignments()
    {
        var actor = new EffectivePermissions.Builder().Add(Domain.Access.Role.CreateAdministrator(s_now), AccessTarget.Global).Build();

        var result = AccessRules.CanChangeAssignment(s_actor, actor, s_actor,
            Role("Viewer", PermissionScope.Global, Permissions.Audit.View), AccessTarget.Global);

        Assert.Equal(AccessErrors.SelfChange, result.Error);
    }

    [Fact]
    public void RoleScopeMustMatchTheTarget()
    {
        var actor = new EffectivePermissions.Builder().Add(Domain.Access.Role.CreateAdministrator(s_now), AccessTarget.Global).Build();

        var result = AccessRules.CanChangeAssignment(s_actor, actor, s_target,
            Role("Node role", PermissionScope.Node, Permissions.Server.View), AccessTarget.ForServer(s_server, s_node));

        Assert.Equal("access.scope_mismatch", result.Error?.Code);
    }

    [Fact]
    public void ActorCannotGrantPermissionsTheyDoNotHoldAtTheTarget()
    {
        var actor = new EffectivePermissions.Builder()
            .Add(Role("Users", PermissionScope.Global, Permissions.User.Manage), AccessTarget.Global)
            .Add(Role("Node ops", PermissionScope.Node, Permissions.Server.Start), AccessTarget.ForNode(s_node)).Build();
        var role = Role("Starter", PermissionScope.Server, Permissions.Server.Start, Permissions.Server.Kill);

        var result = AccessRules.CanChangeAssignment(s_actor, actor, s_target, role, AccessTarget.ForServer(s_server, s_node));

        Assert.Equal("access.escalation", result.Error?.Code);
        Assert.Contains(Permissions.Server.Kill, result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Permissions.Server.Start, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NodePermissionsAllowGrantingOnThatNodeOnly()
    {
        var actor = new EffectivePermissions.Builder()
            .Add(Role("Node ops", PermissionScope.Node, Permissions.Server.Start), AccessTarget.ForNode(s_node)).Build();
        var role = Role("Starter", PermissionScope.Server, Permissions.Server.Start);

        Assert.True(AccessRules.CanChangeAssignment(s_actor, actor, s_target, role, AccessTarget.ForServer(s_server, s_node)).IsSuccess);
        Assert.False(AccessRules.CanChangeAssignment(s_actor, actor, s_target, role, AccessTarget.ForServer(s_server, s_otherNode)).IsSuccess);
    }

    [Fact]
    public void GrantingTheAdministratorRoleRequiresTheWholeCatalog()
    {
        var almostAll = PermissionCatalog.AllKeys.Where(p => p != Permissions.Settings.Manage).ToArray();
        var actor = new EffectivePermissions.Builder().Add(Role("Almost", PermissionScope.Global, almostAll), AccessTarget.Global).Build();

        var result = AccessRules.CanChangeAssignment(s_actor, actor, s_target,
            Domain.Access.Role.CreateAdministrator(s_now), AccessTarget.Global);

        Assert.Equal("access.escalation", result.Error?.Code);
    }

    [Fact]
    public void RolesCanOnlyBeDefinedWithGloballyHeldPermissions()
    {
        var actor = new EffectivePermissions.Builder()
            .Add(Role("Roles", PermissionScope.Global, Permissions.Role.Manage, Permissions.Server.View), AccessTarget.Global)
            .Add(Role("Node", PermissionScope.Node, Permissions.Server.Start), AccessTarget.ForNode(s_node)).Build();

        Assert.True(AccessRules.CanDefineRole(actor, [Permissions.Server.View]).IsSuccess);
        Assert.Equal("access.escalation", AccessRules.CanDefineRole(actor, [Permissions.Server.Start]).Error?.Code);
    }

    private static Role Role(string name, PermissionScope scope, params string[] permissions) =>
        Domain.Access.Role.Create(name, null, scope, permissions, s_now).Value;
}
