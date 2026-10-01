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
    /// Changing another user's account (disable, delete, reset password): nobody does it to themselves through user
    /// management, and the target may not hold any global permission the actor lacks, so a <c>user.manage</c> holder
    /// cannot lock out an administrator.
    /// </summary>
    public static Result CanManageUser(UserId actor, EffectivePermissions actorPermissions, UserId targetUser, EffectivePermissions targetPermissions)
    {
        ArgumentNullException.ThrowIfNull(actorPermissions);
        ArgumentNullException.ThrowIfNull(targetPermissions);

        if (actor == targetUser)
        {
            return AccessErrors.SelfChange;
        }

        return EnsureHeld(targetPermissions.Global, actorPermissions.HasGlobal);
    }

    private static Result EnsureHeld(IEnumerable<string> permissions, Func<string, bool> holds)
    {
        var missing = permissions.Where(p => !holds(p)).Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0 ? Result.Success() : AccessErrors.Escalation(missing);
    }
}
