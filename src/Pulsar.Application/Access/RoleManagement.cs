using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Access;

public sealed record RoleSummary(
    Guid Id,
    string Name,
    string? Description,
    PermissionScope Scope,
    bool IsSystem,
    bool RequiresTwoFactor,
    IReadOnlyList<string> Permissions,
    int AssignedUsers,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RoleInput(string Name, string? Description, PermissionScope Scope, IReadOnlyList<string> Permissions);

/// <summary>Role administration with the anti-escalation rules of docs/architecture/02. Requires a signed-in actor.</summary>
public sealed class RoleManagement(
    IPulsarDbContext db, IPermissionEvaluator evaluator, IRequestContext request, IAuditLog audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.NormalizedName).ToListAsync(cancellationToken);
        var counts = await CountAssignmentsAsync(cancellationToken);
        return [.. roles.Select(r => Summarize(r, counts.GetValueOrDefault(r.Id)))];
    }

    public async Task<Result<RoleSummary>> GetAsync(RoleId id, CancellationToken cancellationToken)
    {
        var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role is null)
        {
            return AccessErrors.RoleNotFound;
        }

        var count = await db.UserRoles.CountAsync(a => a.RoleId == id, cancellationToken);
        return Summarize(role, count);
    }

    public async Task<Result<RoleSummary>> CreateAsync(RoleInput input, CancellationToken cancellationToken)
    {
        // Invalid input is reported before the escalation check: an unknown permission is a 400, not a 403.
        var created = Role.Create(input.Name, input.Description, input.Scope, input.Permissions, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var allowed = AccessRules.CanDefineRole(await ActorPermissionsAsync(cancellationToken), input.Permissions);
        if (!allowed.IsSuccess)
        {
            return allowed.Error!;
        }

        var role = created.Value;
        if (await NameTakenAsync(role.NormalizedName, except: null, cancellationToken))
        {
            return AccessErrors.RoleNameTaken;
        }

        db.Roles.Add(role);
        audit.Record(AuditActions.Role.Created, metadata: new { name = role.Name, scope = role.Scope, permissions = Sorted(role.Permissions) })
            .WithTarget("role", role.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return Summarize(role, 0);
    }

    public async Task<Result<RoleSummary>> UpdateAsync(RoleId id, string name, string? description, IReadOnlyList<string> permissions, CancellationToken cancellationToken)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role is null)
        {
            return AccessErrors.RoleNotFound;
        }

        if (role.IsSystem)
        {
            return AccessErrors.SystemRoleImmutable;
        }

        var valid = Role.ValidatePermissions(role.Scope, permissions);
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        // Both what is removed and what is added: a role manager may not strip permissions they do not hold either.
        var before = role.Permissions;
        var allowed = AccessRules.CanDefineRole(await ActorPermissionsAsync(cancellationToken), before.Union(permissions, StringComparer.Ordinal));
        if (!allowed.IsSuccess)
        {
            return allowed.Error!;
        }

        if (await NameTakenAsync(Role.Normalize(name ?? string.Empty), except: id, cancellationToken))
        {
            return AccessErrors.RoleNameTaken;
        }

        var updated = role.Update(name ?? string.Empty, description, permissions, clock.GetUtcNow());
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        var after = role.Permissions;
        audit.Record(AuditActions.Role.Updated, metadata: new
        {
            name = role.Name,
            added = Sorted(after.Except(before, StringComparer.Ordinal)),
            removed = Sorted(before.Except(after, StringComparer.Ordinal)),
        }).WithTarget("role", role.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        evaluator.InvalidateAll();

        var count = await db.UserRoles.CountAsync(a => a.RoleId == id, cancellationToken);
        return Summarize(role, count);
    }

    /// <summary>
    /// Requires two-factor authentication from the role's users, or stops requiring it. Only someone who may define
    /// the role (holds all its permissions) may change it; the system role included.
    /// </summary>
    public async Task<Result<RoleSummary>> RequireTwoFactorAsync(RoleId id, bool required, CancellationToken cancellationToken)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role is null)
        {
            return AccessErrors.RoleNotFound;
        }

        var allowed = AccessRules.CanDefineRole(await ActorPermissionsAsync(cancellationToken), role.Permissions);
        if (!allowed.IsSuccess)
        {
            return allowed.Error!;
        }

        if (role.RequireTwoFactor(required, clock.GetUtcNow()))
        {
            audit.Record(AuditActions.Role.Updated, metadata: new { name = role.Name, requiresTwoFactor = required })
                .WithTarget("role", role.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            evaluator.InvalidateAll();
        }

        var count = await db.UserRoles.CountAsync(a => a.RoleId == id, cancellationToken);
        return Summarize(role, count);
    }

    public async Task<Result> DeleteAsync(RoleId id, CancellationToken cancellationToken)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role is null)
        {
            return AccessErrors.RoleNotFound;
        }

        var deletable = role.EnsureDeletable();
        if (!deletable.IsSuccess)
        {
            return deletable;
        }

        var allowed = AccessRules.CanDefineRole(await ActorPermissionsAsync(cancellationToken), role.Permissions);
        if (!allowed.IsSuccess)
        {
            return allowed;
        }

        if (await db.UserRoles.AnyAsync(a => a.RoleId == id, cancellationToken))
        {
            return AccessErrors.RoleInUse;
        }

        db.Roles.Remove(role);
        audit.Record(AuditActions.Role.Deleted, metadata: new { name = role.Name, permissions = Sorted(role.Permissions) })
            .WithTarget("role", role.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        evaluator.InvalidateAll();
        return Result.Success();
    }

    private async Task<EffectivePermissions> ActorPermissionsAsync(CancellationToken cancellationToken) =>
        await evaluator.GetAsync(request.UserId ?? throw new InvalidOperationException("Role management requires a signed-in user."), cancellationToken);

    private Task<bool> NameTakenAsync(string normalizedName, RoleId? except, CancellationToken cancellationToken) =>
        db.Roles.AnyAsync(r => r.NormalizedName == normalizedName && (except == null || r.Id != except), cancellationToken);

    private async Task<Dictionary<RoleId, int>> CountAssignmentsAsync(CancellationToken cancellationToken) =>
        await db.UserRoles.GroupBy(a => a.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);

    private static RoleSummary Summarize(Role role, int assignedUsers) => new(
        role.Id.Value, role.Name, role.Description, role.Scope, role.IsSystem, role.RequiresTwoFactor, Sorted(role.Permissions), assignedUsers,
        role.CreatedAt, role.UpdatedAt);

    private static string[] Sorted(IEnumerable<string> permissions) => [.. permissions.Order(StringComparer.Ordinal)];
}
