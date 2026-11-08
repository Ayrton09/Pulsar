using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Auth;
using Pulsar.Api.Http;
using Pulsar.Api.Realtime;
using Pulsar.Api.Sftp;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Application.Servers;
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
    SshKeys sshKeys,
    SftpConnections sftp,
    ServerQuotas quotas,
    TimeProvider clock)
{
    /// <param name="externalId">Only the user another system calls so (1.0.24).</param>
    public async Task<IReadOnlyList<UserResponse>> ListAsync(CancellationToken ct, string? externalId = null)
    {
        var all = await db.Users.AsNoTracking().Where(u => externalId == null || u.ExternalId == externalId).OrderBy(u => u.NormalizedUserName)
            .ToListAsync(ct);
        var roles = await RoleReferencesAsync(null, ct);
        var access = await AccessReferencesAsync(null, ct);
        var keys = await apiKeys.CountAsync([.. all.Select(u => u.Id)], ct);
        var ssh = await sshKeys.CountAsync([.. all.Select(u => u.Id)], ct);
        var links = await SsoLinksAsync(null, ct);
        return [.. all.Select(u => Describe(u, roles, access, keys.GetValueOrDefault(u.Id), ssh.GetValueOrDefault(u.Id), links))];
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

        if (input.ExternalId is { } externalId && !ExternalIds.IsValid(externalId))
        {
            return ApiProblems.FromDomainError(ExternalIds.Invalid);
        }

        if (input.ExternalId is not null && await db.Users.AnyAsync(u => u.ExternalId == input.ExternalId, ct))
        {
            return ApiProblems.FromDomainError(AccessErrors.UserExternalIdTaken);
        }

        var now = clock.GetUtcNow();
        var user = new PulsarUser
        {
            Id = newId,
            UserName = input.UserName.Trim(),
            DisplayName = Clean(input.DisplayName),
            Email = Clean(input.Email),
            CreatedAt = now,
            ExternalId = input.ExternalId,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        IdentityResult created;
        try
        {
            created = await users.CreateAsync(user, input.Password);
        }
        catch (DbUpdateException)
        {
            // The name or the external id taken at the same moment by another request (the unique indexes): answered as
            // the checks before would have answered, from what is there now.
            await transaction.RollbackAsync(ct);
            db.Entry(user).State = EntityState.Detached;
            if (await users.FindByNameAsync(user.UserName) is not null)
            {
                return IdentityProblems.Validation(IdentityResult.Failed(users.ErrorDescriber.DuplicateUserName(user.UserName)));
            }

            if (input.ExternalId is not null && await db.Users.AnyAsync(u => u.ExternalId == input.ExternalId, ct))
            {
                return ApiProblems.FromDomainError(AccessErrors.UserExternalIdTaken);
            }

            throw;
        }

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

        var response = Describe(user, roles.Select(r => (user.Id, new RoleReference(r.Id.Value, r.Name))).ToList(), [], apiKeys: 0, sshKeys: 0,
            Array.Empty<(UserId, SsoKind)>().ToLookup(l => l.Item1, l => l.Item2));
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
        // Two administrators disabling each other at once (or one whose session was not checked again yet) would leave
        // none: under the Administrator role's lock, the last enabled one stays.
        await using var transaction = disabledChanged && input.IsDisabled ? await db.Database.BeginTransactionAsync(ct) : null;
        if (transaction is not null && await HoldsLastEnabledAdministratorAssignmentAsync(user.Id, ct))
        {
            return ApiProblems.FromDomainError(AccessErrors.LastAdministrator);
        }

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

            if (transaction is not null)
            {
                // Disabled: a new security stamp ends the user's existing sessions at the next validation; live channels
                // now. A user whose sessions would go on working is not disabled (the transaction is not committed).
                if (!await SecurityStamps.RenewAsync(user, users, db, ct))
                {
                    return ApiProblems.Create(StatusCodes.Status409Conflict, "user.disable_failed",
                        "The user's sessions could not be ended, so the user was not disabled. Try again.");
                }

                await transaction.CommitAsync(ct);
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
        // Whoever had the old password may have made keys with it: they go with it (1.0.36).
        await apiKeys.RevokeAllAsync(user.Id, ct, because: "password_reset");
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
    /// Removes every account of a sign-in provider linked to the user's (1.0.18; a provider account taken over, say): the
    /// user's sessions end with the security stamp. The user can link them again, with their password.
    /// </summary>
    public async Task<Results<Ok<UserResponse>, ProblemHttpResult>> RemoveSsoLinksAsync(UserId id, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        // All or none: a link Identity could not remove would still sign in.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var logins = await users.GetLoginsAsync(user!);
        audit.Record(AuditActions.User.SsoLinksRemoved, metadata: new { userName = user!.UserName, providers = logins.Select(l => l.LoginProvider).ToList() })
            .WithTarget("user", user.Id.ToString());
        foreach (var login in logins)
        {
            if (!(await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey)).Succeeded) // renews the security stamp; saves the entry
            {
                return SsoEndpoints.UnlinkFailed();
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        connections.EndUser(user.Id);
        return TypedResults.Ok(await DescribeAsync(user, ct));
    }

    /// <summary>
    /// Deletes every SSH key of the user (after a leak, say): the SFTP sessions they signed in end. The user can add new ones.
    /// </summary>
    public async Task<Results<Ok<UserResponse>, ProblemHttpResult>> DeleteSshKeysAsync(UserId id, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        await sshKeys.DeleteAllAsync(user!.Id, ct);
        sftp.EndKeys(user.Id);
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
        await apiKeys.RevokeAllAsync(user.Id, ct, because: "two_factor_reset");
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

    /// <summary>The user's quota (1.0.24) and what their servers use of it; 404 <c>quota.not_found</c> without one.</summary>
    public async Task<Results<Ok<UserQuotaResponse>, ProblemHttpResult>> GetQuotaAsync(UserId id, CancellationToken ct)
    {
        if (await users.FindByIdAsync(id.ToString()) is null)
        {
            return ApiProblems.FromDomainError(AccessErrors.UserNotFound);
        }

        var quota = await db.UserQuotas.AsNoTracking().SingleOrDefaultAsync(q => q.UserId == id, ct);
        return quota is null ? ApiProblems.FromDomainError(AccessErrors.QuotaNotFound) : TypedResults.Ok(await QuotaResponseAsync(quota, ct));
    }

    /// <summary>
    /// Gives the user a quota, or changes it (1.0.24). Letting them create servers on a node, and own them, is as much as
    /// giving them <c>server.create</c> and the owner permissions there: only on nodes where the actor holds them all
    /// (<see cref="AccessRules.QuotaGrants"/>), for a user the actor may manage. Lowering it
    /// below what their servers use takes nothing away: it only stops them creating more.
    /// </summary>
    public async Task<Results<Ok<UserQuotaResponse>, ProblemHttpResult>> SetQuotaAsync(UserId id, SetUserQuotaRequest input, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        var nodes = (input.NodeIds ?? []).Distinct().Select(n => new NodeId(n)).ToList();
        var actor = await evaluator.GetAsync(Actor, ct);
        if (nodes.Any(n => AccessRules.QuotaGrants.Any(p => !actor.Has(p, AccessTarget.ForNode(n)))))
        {
            return ApiProblems.FromDomainError(AccessErrors.QuotaEscalation);
        }

        if (await db.Nodes.CountAsync(n => nodes.Contains(n.Id), ct) != nodes.Count)
        {
            return ApiProblems.FromDomainError(NodeErrors.NotFound);
        }

        var now = clock.GetUtcNow();
        var quota = await db.UserQuotas.SingleOrDefaultAsync(q => q.UserId == id, ct);
        if (quota is null)
        {
            var created = UserQuota.Create(id, input.MaxServers, input.MaxMemoryMb, input.MaxCpuPercent, nodes, now);
            if (!created.IsSuccess)
            {
                return ApiProblems.FromDomainError(created.Error!);
            }

            quota = created.Value;
            db.UserQuotas.Add(quota);
        }
        else if (quota.Set(input.MaxServers, input.MaxMemoryMb, input.MaxCpuPercent, nodes, now) is { IsSuccess: false } invalid)
        {
            return ApiProblems.FromDomainError(invalid.Error!);
        }

        audit.Record(AuditActions.User.QuotaSet, metadata: new
        {
            userName = user!.UserName,
            input.MaxServers,
            input.MaxMemoryMb,
            input.MaxCpuPercent,
            nodes = nodes.Select(n => n.Value),
        }).WithTarget("user", user.Id.ToString());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Given or changed by someone else at the same moment: theirs stands, this one is asked again.
            return ApiProblems.FromDomainError(AccessErrors.QuotaConflict);
        }

        evaluator.Invalidate(id);
        connections.EndUser(id); // their live pages reconnect and read the session again (hasQuota): the Servers page is theirs
        return TypedResults.Ok(await QuotaResponseAsync(quota, ct));
    }

    /// <summary>Takes the user's quota away: their servers stay theirs, they create no more.</summary>
    public async Task<Results<NoContent, ProblemHttpResult>> RemoveQuotaAsync(UserId id, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        if (await db.UserQuotas.SingleOrDefaultAsync(q => q.UserId == id, ct) is not { } quota)
        {
            return ApiProblems.FromDomainError(AccessErrors.QuotaNotFound);
        }

        db.UserQuotas.Remove(quota);
        audit.Record(AuditActions.User.QuotaRemoved, metadata: new { userName = user!.UserName }).WithTarget("user", user.Id.ToString());
        await db.SaveChangesAsync(ct);
        evaluator.Invalidate(id);
        connections.EndUser(id); // as when it is given
        return TypedResults.NoContent();
    }

    /// <summary>What another system (a billing system) calls the user from now on (1.0.24); null: nothing.</summary>
    public async Task<Results<Ok<UserResponse>, ProblemHttpResult>> SetExternalIdAsync(UserId id, string? externalId, CancellationToken ct)
    {
        var (user, problem) = await FindManageableAsync(id, ct);
        if (problem is not null)
        {
            return problem;
        }

        if (externalId is not null && !ExternalIds.IsValid(externalId))
        {
            return ApiProblems.FromDomainError(ExternalIds.Invalid);
        }

        if (externalId is not null && await db.Users.AnyAsync(u => u.ExternalId == externalId && u.Id != id, ct))
        {
            return ApiProblems.FromDomainError(AccessErrors.UserExternalIdTaken);
        }

        user!.ExternalId = externalId;
        audit.Record(AuditActions.User.ExternalIdChanged, metadata: new { userName = user.UserName, externalId }).WithTarget("user", user.Id.ToString());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ApiProblems.FromDomainError(AccessErrors.UserExternalIdTaken);
        }

        return TypedResults.Ok(await DescribeAsync(user, ct));
    }

    /// <summary>
    /// Gives the server to <paramref name="owner"/>, or to nobody (1.0.24, for billing systems): its owner holds
    /// <see cref="PermissionCatalog.OwnerKeys"/> there and it counts against their quota, if they have one. Only for an
    /// actor who holds all of those on the server themselves, and may manage both the new owner and the one before.
    /// Returns the problem, or null when it was done.
    /// </summary>
    public async Task<ProblemHttpResult?> SetServerOwnerAsync(ServerId serverId, UserId? owner, CancellationToken ct)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == serverId, ct);
        if (server is null)
        {
            return ApiProblems.FromDomainError(ServerErrors.NotFound);
        }

        var actor = await evaluator.GetAsync(Actor, ct);
        var target = AccessTarget.ForServer(server.Id, server.NodeId);
        if (!PermissionCatalog.OwnerKeys.All(p => actor.Has(p, target)))
        {
            return ApiProblems.FromDomainError(ServerErrors.OwnerForbidden);
        }

        foreach (var user in new[] { owner, server.OwnerId }.OfType<UserId>().Distinct())
        {
            if ((await FindManageableAsync(user, ct)).Problem is { } problem)
            {
                return problem;
            }
        }

        var before = server.OwnerId;
        if (before == owner)
        {
            return null;
        }

        server.OwnedBy(owner, clock.GetUtcNow());
        audit.Record(AuditActions.Server.OwnerChanged, metadata: new { from = before?.Value, to = owner?.Value }).WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(ct);
        foreach (var user in new[] { owner, before }.OfType<UserId>())
        {
            evaluator.Invalidate(user);
            connections.EndUser(user); // their live pages follow what they may see now
        }

        return null;
    }

    private async Task<UserQuotaResponse> QuotaResponseAsync(UserQuota quota, CancellationToken ct) =>
        new(quota.MaxServers, quota.MaxMemoryMb, quota.MaxCpuPercent, [.. quota.NodeIds.Select(n => n.Value)],
            await quotas.UsageAsync(quota.UserId, ct));

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
        var quotaNodes = await db.UserQuotas.AsNoTracking().Where(q => q.UserId == id).Select(q => q.NodeIds).SingleOrDefaultAsync(ct) ?? [];
        var allowed = AccessRules.CanManageUser(Actor, await evaluator.GetAsync(Actor, ct), id, target, serverNodes, [.. quotaNodes]);
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

    /// <summary>
    /// True when <paramref name="user"/> is the only enabled user with a global Administrator assignment, under the same
    /// lock as <see cref="HoldsLastAdministratorAssignmentAsync"/>. Call inside a transaction.
    /// </summary>
    private async Task<bool> HoldsLastEnabledAdministratorAssignmentAsync(UserId user, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM roles WHERE is_system FOR UPDATE", ct);
        var administrators = await db.UserRoles
            .Where(a => db.Roles.Any(r => r.Id == a.RoleId && r.IsSystem) && db.Users.Any(u => u.Id == a.UserId && !u.IsDisabled))
            .Select(a => a.UserId)
            .Distinct()
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
            (await apiKeys.CountAsync([user.Id], ct)).GetValueOrDefault(user.Id), (await sshKeys.CountAsync([user.Id], ct)).GetValueOrDefault(user.Id),
            await SsoLinksAsync(user.Id, ct));

    /// <summary>The providers each user (or <paramref name="user"/>) has linked an account of.</summary>
    private async Task<ILookup<UserId, SsoKind>> SsoLinksAsync(UserId? user, CancellationToken ct) =>
        (await db.UserLogins.AsNoTracking().Where(l => user == null || l.UserId == user).Select(l => new { l.UserId, l.LoginProvider }).ToListAsync(ct))
        .Select(l => (l.UserId, Kind: SsoProviders.Parse(l.LoginProvider)))
        .Where(l => l.Kind is not null)
        .ToLookup(l => l.UserId, l => l.Kind!.Value);

    private static UserResponse Describe(PulsarUser user, List<(UserId UserId, RoleReference Role)> roles,
        List<(UserId UserId, AccessReference Access)> access, int apiKeys, int sshKeys, ILookup<UserId, SsoKind> links) => new(
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
        apiKeys,
        sshKeys,
        [.. links[user.Id].Order()],
        user.ExternalId);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
