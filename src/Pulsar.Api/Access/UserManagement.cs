using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Auth;
using Pulsar.Api.Http;
using Pulsar.Api.Realtime;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Access;

/// <summary>
/// User administration (ASP.NET Core Identity accounts, and role assignments: global, on a node or on a server) with the
/// anti-escalation rules of docs/architecture/02. Lives in the API because it works directly with Identity's UserManager.
/// </summary>
public sealed class UserManagement(
    PulsarDbContext db,
    UserManager<PulsarUser> users,
    IPermissionEvaluator evaluator,
    IRequestContext request,
    IAuditLog audit,
    BrowserConnections connections,
    ApiKeys apiKeys,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<UserResponse>> ListAsync(CancellationToken ct)
    {
        var all = await db.Users.AsNoTracking().OrderBy(u => u.NormalizedUserName).ToListAsync(ct);
        var roles = await RoleReferencesAsync(null, ct);
        var access = await AccessReferencesAsync(null, ct);
        var keys = await apiKeys.CountAsync([.. all.Select(u => u.Id)], ct);
        return [.. all.Select(u => Describe(u, roles, access, keys.GetValueOrDefault(u.Id)))];
    }

    public async Task<Results<Ok<UserResponse>, ProblemHttpResult>> GetAsync(UserId id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return ApiProblems.FromDomainError(AccessErrors.UserNotFound);
        }

        return TypedResults.Ok(await DescribeAsync(user, ct));
    }

    public async Task<Results<Created<UserResponse>, ProblemHttpResult, ValidationProblem>> CreateAsync(CreateUserRequest input, CancellationToken ct)
    {
        var actor = Actor;
        var actorPermissions = await evaluator.GetAsync(actor, ct);
        var newId = UserId.New();

        var roleIds = (input.RoleIds ?? []).Distinct().Select(g => new RoleId(g)).ToList();
        var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        if (roles.Count != roleIds.Count)
        {
            return ApiProblems.FromDomainError(AccessErrors.RoleNotFound);
        }

        foreach (var role in roles)
        {
            var allowed = AccessRules.CanChangeAssignment(actor, actorPermissions, newId, role, AccessTarget.Global);
            if (!allowed.IsSuccess)
            {
                return ApiProblems.FromDomainError(allowed.Error!);
            }
        }

        var now = clock.GetUtcNow();
        var user = new PulsarUser
        {
            Id = newId,
            UserName = input.UserName.Trim(),
            DisplayName = Clean(input.DisplayName),
            Email = Clean(input.Email),
            CreatedAt = now,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var created = await users.CreateAsync(user, input.Password);
        if (!created.Succeeded)
        {
            return IdentityProblems.Validation(created);
        }

        audit.Record(AuditActions.User.Created, metadata: new { userName = user.UserName }).WithTarget("user", user.Id.ToString());
        foreach (var role in roles)
        {
            db.UserRoles.Add(new UserRoleAssignment(user.Id, role.Id, actor, now));
            RecordAssignment(AuditActions.Role.Assigned, user, role);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var response = Describe(user, roles.Select(r => (user.Id, new RoleReference(r.Id.Value, r.Name))).ToList(), [], apiKeys: 0);
        return TypedResults.Created($"/api/v1/users/{user.Id}", response);
    }

    public async Task<Results<Ok<UserResponse>, ProblemHttpResult, ValidationProblem>> UpdateAsync(UserId id, UpdateUserRequest input, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        var changed = new List<string>();
        if (user!.DisplayName != Clean(input.DisplayName))
        {
            user.DisplayName = Clean(input.DisplayName);
            changed.Add("displayName");
        }

        if (user.Email != Clean(input.Email))
        {
            user.Email = Clean(input.Email);
            changed.Add("email");
        }

        var disabledChanged = user.IsDisabled != input.IsDisabled;
        if (disabledChanged)
        {
            user.IsDisabled = input.IsDisabled;
            changed.Add("isDisabled");
        }

        if (changed.Count > 0)
        {
            audit.Record(AuditActions.User.Updated, metadata: new { userName = user.UserName, changed, isDisabled = user.IsDisabled })
                .WithTarget("user", user.Id.ToString());
            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded)
            {
                return IdentityProblems.Validation(updated);
            }

            if (disabledChanged)
            {
                // A new security stamp ends the user's existing sessions at the next validation; live channels now.
                await users.UpdateSecurityStampAsync(user);
                connections.EndUser(user.Id);
            }
        }

        return TypedResults.Ok(await DescribeAsync(user, ct));
    }

    public async Task<Results<NoContent, ProblemHttpResult, ValidationProblem>> ResetPasswordAsync(UserId id, ResetPasswordRequest input, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        audit.Record(AuditActions.User.PasswordReset, metadata: new { userName = user!.UserName }).WithTarget("user", user.Id.ToString());
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, input.NewPassword); // also renews the security stamp
        if (!reset.Succeeded)
        {
            return IdentityProblems.Validation(reset);
        }

        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        connections.EndUser(user.Id);
        return TypedResults.NoContent();
    }

    /// <summary>Revokes every API key of the user (after a leak, say); the user can create new ones.</summary>
    public async Task<Results<Ok<UserResponse>, ProblemHttpResult>> RevokeApiKeysAsync(UserId id, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        await apiKeys.RevokeAllAsync(user!.Id, ct);
        return TypedResults.Ok(await DescribeAsync(user, ct));
    }

    /// <summary>
    /// Turns off a user's two-factor authentication (a lost phone and no recovery codes left): the key and the codes
    /// are forgotten and the user's sessions end. A role that requires it makes them set it up again at sign-in.
    /// </summary>
    public async Task<Results<NoContent, ProblemHttpResult>> ResetTwoFactorAsync(UserId id, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        audit.Record(AuditActions.User.TwoFactorReset, metadata: new { userName = user!.UserName }).WithTarget("user", user.Id.ToString());
        await TwoFactorEndpoints.TurnOffAsync(users, user); // also renews the security stamp and saves the audit entry
        connections.EndUser(user.Id);
        return TypedResults.NoContent();
    }

    public async Task<Results<NoContent, ProblemHttpResult, ValidationProblem>> DeleteAsync(UserId id, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await HoldsLastAdministratorAssignmentAsync(id, ct))
        {
            return ApiProblems.FromDomainError(AccessErrors.LastAdministrator);
        }

        audit.Record(AuditActions.User.Deleted, metadata: new { userName = user!.UserName }).WithTarget("user", user.Id.ToString());
        var deleted = await users.DeleteAsync(user);
        if (!deleted.Succeeded)
        {
            return IdentityProblems.Validation(deleted);
        }

        await transaction.CommitAsync(ct);
        evaluator.Invalidate(id);
        connections.EndUser(id);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Replaces the user's global roles: each one added or removed under the assignment rules, and only for a user the
    /// actor may manage at all (nobody strips a more powerful user of the roles they share).
    /// </summary>
    public async Task<Results<Ok<UserResponse>, ProblemHttpResult>> SetRolesAsync(UserId id, SetUserRolesRequest input, CancellationToken ct)
    {
        var (found, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        var user = found!;
        var actor = Actor;
        var actorPermissions = await evaluator.GetAsync(actor, ct);
        var desired = input.RoleIds.Distinct().Select(g => new RoleId(g)).ToHashSet();
        var current = await db.UserRoles.Where(a => a.UserId == id).ToListAsync(ct);
        var currentIds = current.Select(a => a.RoleId).ToHashSet();

        var involvedIds = desired.Union(currentIds).ToList();
        var roles = await db.Roles.Where(r => involvedIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        if (desired.Any(r => !roles.ContainsKey(r)))
        {
            return ApiProblems.FromDomainError(AccessErrors.RoleNotFound);
        }

        var added = desired.Except(currentIds).Select(r => roles[r]).ToList();
        var removed = current.Where(a => !desired.Contains(a.RoleId)).ToList();
        foreach (var role in added.Concat(removed.Select(a => roles[a.RoleId])))
        {
            var allowed = AccessRules.CanChangeAssignment(actor, actorPermissions, id, role, AccessTarget.Global);
            if (!allowed.IsSuccess)
            {
                return ApiProblems.FromDomainError(allowed.Error!);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var removesAdministrator = removed.Any(a => roles[a.RoleId].IsSystem);
        if (removesAdministrator && await HoldsLastAdministratorAssignmentAsync(id, ct))
        {
            return ApiProblems.FromDomainError(AccessErrors.LastAdministrator);
        }

        var now = clock.GetUtcNow();
        foreach (var role in added)
        {
            db.UserRoles.Add(new UserRoleAssignment(id, role.Id, actor, now));
            RecordAssignment(AuditActions.Role.Assigned, user, role);
        }

        foreach (var assignment in removed)
        {
            db.UserRoles.Remove(assignment);
            RecordAssignment(AuditActions.Role.Revoked, user, roles[assignment.RoleId]);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        evaluator.Invalidate(id);
        connections.EndUser(id);
        return TypedResults.Ok(await DescribeAsync(user, ct));
    }

    /// <summary>
    /// Grants (<paramref name="granted"/>) or revokes a node or server role of a user on <paramref name="targetId"/>,
    /// under the same rules as global roles: nobody changes their own, the user must be one the actor may manage, the
    /// role must be of that scope, and the actor must hold every permission of the role there. Repeating a grant or a
    /// revoke changes nothing. The user's live connections are closed, so what they watch follows at once.
    /// </summary>
    public async Task<Results<Ok<UserResponse>, ProblemHttpResult>> SetAccessAsync(UserId id, PermissionScope scope, Guid targetId, RoleId roleId,
        bool granted, CancellationToken ct)
    {
        var (found, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        var user = found!;

        if (await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, ct) is not { } role)
        {
            return ApiProblems.FromDomainError(AccessErrors.RoleNotFound);
        }

        AccessTarget target;
        string targetName;
        if (scope == PermissionScope.Node)
        {
            var node = await db.Nodes.Where(n => n.Id == new NodeId(targetId)).Select(n => new { n.Id, n.Name }).SingleOrDefaultAsync(ct);
            if (node is null)
            {
                return ApiProblems.FromDomainError(NodeErrors.NotFound);
            }

            (target, targetName) = (AccessTarget.ForNode(node.Id), node.Name);
        }
        else
        {
            var server = await db.Servers.Where(s => s.Id == new ServerId(targetId)).Select(s => new { s.Id, s.NodeId, s.Name }).SingleOrDefaultAsync(ct);
            if (server is null)
            {
                return ApiProblems.FromDomainError(ServerErrors.NotFound);
            }

            (target, targetName) = (AccessTarget.ForServer(server.Id, server.NodeId), server.Name);
        }

        var actor = Actor;
        var allowed = AccessRules.CanChangeAssignment(actor, await evaluator.GetAsync(actor, ct), id, role, target);
        if (!allowed.IsSuccess)
        {
            return ApiProblems.FromDomainError(allowed.Error!);
        }

        var changed = scope == PermissionScope.Node
            ? await SetNodeAccessAsync(id, new NodeId(targetId), roleId, granted, actor, ct)
            : await SetServerAccessAsync(id, new ServerId(targetId), roleId, granted, actor, ct);
        if (changed)
        {
            audit.Record(granted ? AuditActions.Role.Assigned : AuditActions.Role.Revoked, metadata: new
            {
                userName = user.UserName,
                role = role.Name,
                permissions = role.Permissions.Order(StringComparer.Ordinal),
                scope = scope == PermissionScope.Node ? "node" : "server",
                target = targetName,
                targetId,
            }).WithTarget("user", user.Id.ToString());
            await db.SaveChangesAsync(ct);
            evaluator.Invalidate(id);
            connections.EndUser(id);
        }

        return TypedResults.Ok(await DescribeAsync(user, ct));
    }

    private async Task<bool> SetNodeAccessAsync(UserId id, NodeId node, RoleId role, bool granted, UserId actor, CancellationToken ct)
    {
        var existing = await db.NodeAccess.SingleOrDefaultAsync(a => a.UserId == id && a.NodeId == node && a.RoleId == role, ct);
        if (granted == (existing is not null))
        {
            return false;
        }

        if (existing is null)
        {
            db.NodeAccess.Add(new NodeAccess(id, node, role, actor, clock.GetUtcNow()));
        }
        else
        {
            db.NodeAccess.Remove(existing);
        }

        return true;
    }

    private async Task<bool> SetServerAccessAsync(UserId id, ServerId server, RoleId role, bool granted, UserId actor, CancellationToken ct)
    {
        var existing = await db.ServerAccess.SingleOrDefaultAsync(a => a.UserId == id && a.ServerId == server && a.RoleId == role, ct);
        if (granted == (existing is not null))
        {
            return false;
        }

        if (existing is null)
        {
            db.ServerAccess.Add(new ServerAccess(id, server, role, actor, clock.GetUtcNow()));
        }
        else
        {
            db.ServerAccess.Remove(existing);
        }

        return true;
    }

    private UserId Actor => request.UserId ?? throw new InvalidOperationException("User management requires a signed-in user.");

    /// <summary>Loads a user the actor may change (rule 6 of docs/architecture/02), or the problem to return.</summary>
    private async Task<(PulsarUser? User, ProblemHttpResult? Problem)> FindManageableAsync(UserId id, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return (null, ApiProblems.FromDomainError(AccessErrors.UserNotFound));
        }

        var target = await evaluator.GetAsync(id, ct);
        var servers = target.Servers.Keys.ToList();
        var serverNodes = servers.Count == 0
            ? []
            : await db.Servers.Where(s => servers.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.NodeId, ct);
        var allowed = AccessRules.CanManageUser(Actor, await evaluator.GetAsync(Actor, ct), id, target, serverNodes);
        return allowed.IsSuccess ? (user, null) : (null, ApiProblems.FromDomainError(allowed.Error!));
    }

    /// <summary>
    /// True when <paramref name="user"/> holds the only global Administrator assignment. Locks the Administrator role
    /// row first, so two concurrent removals cannot both see "another administrator remains". Call inside a transaction.
    /// </summary>
    private async Task<bool> HoldsLastAdministratorAssignmentAsync(UserId user, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM roles WHERE is_system FOR UPDATE", ct);
        var administrators = await db.UserRoles
            .Where(a => db.Roles.Any(r => r.Id == a.RoleId && r.IsSystem))
            .Select(a => a.UserId)
            .ToListAsync(ct);
        return administrators.Contains(user) && administrators.Count == 1;
    }

    private void RecordAssignment(string action, PulsarUser user, Role role) =>
        audit.Record(action, metadata: new { userName = user.UserName, role = role.Name, permissions = role.Permissions.Order(StringComparer.Ordinal) })
            .WithTarget("user", user.Id.ToString());

    private async Task<List<(UserId UserId, RoleReference Role)>> RoleReferencesAsync(UserId? user, CancellationToken ct)
    {
        var rows = await (
                from a in db.UserRoles
                join r in db.Roles on a.RoleId equals r.Id
                where user == null || a.UserId == user
                orderby r.NormalizedName
                select new { a.UserId, RoleId = r.Id, r.Name })
            .ToListAsync(ct);
        return [.. rows.Select(x => (x.UserId, new RoleReference(x.RoleId.Value, x.Name)))];
    }

    private async Task<List<(UserId UserId, AccessReference Access)>> AccessReferencesAsync(UserId? user, CancellationToken ct)
    {
        var nodes = await (
                from a in db.NodeAccess
                join r in db.Roles on a.RoleId equals r.Id
                join n in db.Nodes on a.NodeId equals n.Id
                where user == null || a.UserId == user
                select new { a.UserId, TargetId = n.Id.Value, TargetName = n.Name, RoleId = r.Id, RoleName = r.Name })
            .ToListAsync(ct);
        var servers = await (
                from a in db.ServerAccess
                join r in db.Roles on a.RoleId equals r.Id
                join s in db.Servers on a.ServerId equals s.Id
                where user == null || a.UserId == user
                select new { a.UserId, TargetId = s.Id.Value, TargetName = s.Name, RoleId = r.Id, RoleName = r.Name })
            .ToListAsync(ct);
        return
        [
            .. nodes.Select(x => (x.UserId, new AccessReference(PermissionScope.Node, x.TargetId, x.TargetName, new RoleReference(x.RoleId.Value, x.RoleName))))
                .Concat(servers.Select(x => (x.UserId,
                    new AccessReference(PermissionScope.Server, x.TargetId, x.TargetName, new RoleReference(x.RoleId.Value, x.RoleName)))))
                .OrderBy(x => x.Item2.Scope).ThenBy(x => x.Item2.TargetName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Item2.Role.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private async Task<UserResponse> DescribeAsync(PulsarUser user, CancellationToken ct) =>
        Describe(user, await RoleReferencesAsync(user.Id, ct), await AccessReferencesAsync(user.Id, ct),
            (await apiKeys.CountAsync([user.Id], ct)).GetValueOrDefault(user.Id));

    private static UserResponse Describe(PulsarUser user, List<(UserId UserId, RoleReference Role)> roles,
        List<(UserId UserId, AccessReference Access)> access, int apiKeys) => new(
        user.Id.Value,
        user.UserName!,
        user.DisplayName,
        user.Email,
        user.IsDisabled,
        user.TwoFactorEnabled,
        user.CreatedAt,
        user.LastLoginAt,
        [.. roles.Where(r => r.UserId == user.Id).Select(r => r.Role)],
        [.. access.Where(a => a.UserId == user.Id).Select(a => a.Access)],
        apiKeys);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
