using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>
/// Anti-escalation rules (docs/architecture/02). Whether the actor may call the operation at all
/// (<c>user.manage</c>, <c>role.manage</c>) is checked by the endpoint; these rules limit what they can do with it.
/// </summary>
public static class AccessRules
{
    /// <summary>
    /// Granting or revoking <paramref name="role"/> for <paramref name="targetUser"/> at <paramref name="target"/>:
    /// nobody changes their own assignments, the role must match the target's scope, and the actor must hold every
    /// permission of the role at that target. Revoking follows the same rule, so nobody can strip a more powerful user.
    /// </summary>
    public static Result CanChangeAssignment(
        UserId actor, EffectivePermissions actorPermissions, UserId targetUser, Role role, AccessTarget target)
    {
        ArgumentNullException.ThrowIfNull(actorPermissions);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(target);

        if (actor == targetUser)
        {
            return AccessErrors.SelfChange;
        }

        if (role.Scope != target.Scope)
        {
            return AccessErrors.ScopeMismatch(role.Scope, target.Scope);
        }

        return EnsureHeld(role.Permissions, p => actorPermissions.Has(p, target));
    }

    /// <summary>
    /// Creating, editing or deleting a role: the actor must hold every permission involved globally (roles are
    /// templates that can later be assigned anywhere). For an edit, pass both the old and the new permissions.
    /// </summary>
    public static Result CanDefineRole(EffectivePermissions actorPermissions, IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(actorPermissions);
        return EnsureHeld(permissions, actorPermissions.HasGlobal);
    }

    /// <summary>
    /// Changing another user's account (disable, delete, reset the password or two-factor, revoke API keys, change
    /// roles): nobody does it to themselves through user management, and the target may not hold any permission the
    /// actor lacks where the target holds it (globally, on a node, on a server), so a <c>user.manage</c> holder can
    /// neither lock out an administrator nor sign in as someone who can do more somewhere.
    /// </summary>
    /// <param name="serverNodes">The node of each server the target has a role on; a server missing here needs the permissions globally.</param>
    public static Result CanManageUser(UserId actor, EffectivePermissions actorPermissions, UserId targetUser, EffectivePermissions targetPermissions,
        IReadOnlyDictionary<ServerId, NodeId> serverNodes)
    {
        ArgumentNullException.ThrowIfNull(actorPermissions);
        ArgumentNullException.ThrowIfNull(targetPermissions);
        ArgumentNullException.ThrowIfNull(serverNodes);

        if (actor == targetUser)
        {
            return AccessErrors.SelfChange;
        }

        var missing = targetPermissions.Global.Where(p => !actorPermissions.HasGlobal(p))
            .Concat(targetPermissions.Nodes.SelectMany(n => n.Value.Where(p => !actorPermissions.Has(p, AccessTarget.ForNode(n.Key)))))
            .Concat(targetPermissions.Servers.SelectMany(s => s.Value.Where(p => serverNodes.TryGetValue(s.Key, out var node)
                ? !actorPermissions.Has(p, AccessTarget.ForServer(s.Key, node))
                : !actorPermissions.HasGlobal(p))));
        return Refuse(missing);
    }

    private static Result EnsureHeld(IEnumerable<string> permissions, Func<string, bool> holds) => Refuse(permissions.Where(p => !holds(p)));

    private static Result Refuse(IEnumerable<string> missing)
    {
        var sorted = missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return sorted.Count == 0 ? Result.Success() : AccessErrors.Escalation(sorted);
    }
}
