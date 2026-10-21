using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Http;
using Pulsar.Api.Realtime;
using Pulsar.Api.Security;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Auth;

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    /// <summary>The anonymous routes that read the database (<see cref="AuthRateLimitOptions.AnonymousPermitLimit"/>).</summary>
    public const string AnonymousRateLimitPolicy = "anonymous";

    // Verified when the user name does not exist, so both paths cost one password hash (no user enumeration by timing).
    private static readonly string s_dummyPasswordHash =
        new PasswordHasher<PulsarUser>().HashPassword(new PulsarUser(), "pulsar-timing-equalizer");

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/session", GetSessionAsync).AllowAnonymous().RequireRateLimiting(AnonymousRateLimitPolicy);
        group.MapPost("/setup", SetupAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        group.MapPost("/logout", LogoutAsync).RequireSignedInUser().AllowWithoutTwoFactor();
        group.MapGet("/me", GetMeAsync).RequireSignedInUser().AllowWithoutTwoFactor();
        group.MapPut("/profile", UpdateProfileAsync).RequireSignedInUser();
        group.MapPost("/password", ChangePasswordAsync).RequireSignedInUser().RequireRateLimiting(RateLimitPolicy);
        group.MapTwoFactorEndpoints();
        return group;
    }

    /// <summary>The signed-in user's own display name and email (administrators change others through /users).</summary>
    private static async Task<Results<Ok<CurrentUserResponse>, ValidationProblem, UnauthorizedHttpResult>> UpdateProfileAsync(
        UpdateProfileRequest request, HttpContext http, UserManager<PulsarUser> users, IPermissionEvaluator evaluator, IAuditLog audit, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        user.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        audit.Record(AuditActions.Auth.ProfileUpdated).WithTarget("user", user.Id.ToString());
        var updated = await users.UpdateAsync(user);
        return updated.Succeeded ? TypedResults.Ok(await DescribeAsync(user, evaluator, ct)) : IdentityProblems.Validation(updated);
    }

    /// <summary>
    /// Changes the signed-in user's password. Other sessions of the user end (new security stamp); this one is
    /// renewed so the user stays signed in here.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult, ValidationProblem, UnauthorizedHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest request, HttpContext http, PulsarDbContext db, UserManager<PulsarUser> users, SignInManager<PulsarUser> signIn,
        SignInLocks locks, IAuditLog audit, BrowserConnections connections, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (await WrongPasswordAsync(user, request.CurrentPassword, users, db, locks, audit, AuditActions.Auth.PasswordChanged, ct) is { } wrong)
        {
            return wrong;
        }

        var changed = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!changed.Succeeded)
        {
            return IdentityProblems.Validation(changed);
        }

        audit.Record(AuditActions.Auth.PasswordChanged).WithTarget("user", user.Id.ToString());
        await db.SaveChangesAsync(ct);
        connections.EndUser(user.Id); // this browser's own channel reconnects with the renewed cookie
        await signIn.RefreshSignInAsync(user);
        http.User = await signIn.CreateUserPrincipalAsync(user);
        AntiforgeryProtection.IssueToken(http);
        return TypedResults.NoContent();
    }

    /// <summary>Called by the SPA on startup. Also issues the antiforgery token for the following requests.</summary>
    private static async Task<Ok<SessionResponse>> GetSessionAsync(
        HttpContext http, UserManager<PulsarUser> users, IPermissionEvaluator evaluator, CancellationToken ct)
    {
        var setupRequired = !await users.Users.AnyAsync(ct);
        CurrentUserResponse? current = null;
        if (http.User.GetUserId() is { } id && await users.FindByIdAsync(id.ToString()) is { } user)
        {
            current = await DescribeAsync(user, evaluator, ct);
        }

        AntiforgeryProtection.IssueToken(http);
        return TypedResults.Ok(new SessionResponse(setupRequired, current, InsecureConnection: !http.Request.IsHttps));
    }

    private static async Task<Results<Ok<CurrentUserResponse>, ProblemHttpResult, ValidationProblem>> SetupAsync(
        SetupRequest request,
        HttpContext http,
        InitialSetup setup,
        PulsarDbContext db,
        UserManager<PulsarUser> users,
        SignInManager<PulsarUser> signIn,
        IPermissionEvaluator evaluator,
        IAuditLog audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        await setup.Gate.WaitAsync(ct);
        try
        {
            if (await users.Users.AnyAsync(ct))
            {
                return ApiProblems.Create(StatusCodes.Status409Conflict, "setup.completed", "The initial setup has already been completed.");
            }

            if (!setup.IsValid(request.Token))
            {
                audit.Record(AuditActions.Auth.Setup, AuditOutcome.Failure, new { reason = "invalid_token" });
                await db.SaveChangesAsync(ct);
                return ApiProblems.Create(StatusCodes.Status400BadRequest, "setup.invalid_token",
                    "The setup token is not valid.", "Use the one-time token printed in the panel log at startup.");
            }

            var now = clock.GetUtcNow();
            var user = new PulsarUser
            {
                Id = UserId.New(),
                UserName = request.UserName.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
                CreatedAt = now,
                LastLoginAt = now,
            };

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var created = await users.CreateAsync(user, request.Password);
            if (!created.Succeeded)
            {
                return IdentityProblems.Validation(created);
            }

            var administrator = await db.Roles.SingleAsync(r => r.IsSystem && r.Name == Role.AdministratorName, ct);
            db.UserRoles.Add(new UserRoleAssignment(user.Id, administrator.Id, grantedBy: null, now));
            audit.Record(AuditActions.Auth.Setup, AuditOutcome.Success, AuditActor.ForUser(user.Id, user.UserName));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            setup.Complete();

            await signIn.SignInAsync(user, isPersistent: false);
            http.User = await signIn.CreateUserPrincipalAsync(user);
            AntiforgeryProtection.IssueToken(http);
            return TypedResults.Ok(await DescribeAsync(user, evaluator, ct));
        }
        finally
        {
            setup.Gate.Release();
        }
    }

    private static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        HttpContext http,
        PulsarDbContext db,
        UserManager<PulsarUser> users,
        SignInManager<PulsarUser> signIn,
        IPermissionEvaluator evaluator,
        IAuditLog audit,
        FailedLoginBudget failures,
        SignInLocks locks,
        TimeProvider clock,
        CancellationToken ct)
    {
        // One check at a time per name (SignInLocks): each wrong password counts, and each attempt keeps its audit entry.
        using var turn = await locks.AcquireAsync(users.NormalizeName(request.UserName), ct);
        var user = await users.FindByNameAsync(request.UserName);
        if (user is null)
        {
            users.PasswordHasher.VerifyHashedPassword(new PulsarUser(), s_dummyPasswordHash, request.Password);
            return await FailAsync(null, "invalid_credentials");
        }

        await db.Entry(user).ReloadAsync(ct); // as it is now, not as this request may have loaded it before its turn

        // Whether Identity answers before hashing the password, decided now: the attempt that locks the account hashes
        // it and only then answers "locked out", so the result alone would make that attempt hash twice.
        var blocked = await users.IsLockedOutAsync(user) || !await signIn.CanSignInAsync(user);
        var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
        {
            // The password was right; the sign-in (and its audit entry) waits for the second factor.
            return TypedResults.Ok(new LoginResponse(TwoFactorRequired: true, User: null));
        }

        if (!result.Succeeded)
        {
            // Locked-out, disabled and wrong-password attempts get the same answer, so the response says nothing about the
            // account. Identity answers the first two before hashing the password: hash anyway, so the time says nothing either.
            if (blocked)
            {
                users.PasswordHasher.VerifyHashedPassword(user, s_dummyPasswordHash, request.Password);
            }

            var reason = result.IsLockedOut ? "locked_out" : result.IsNotAllowed ? "not_allowed" : "invalid_credentials";
            return await FailAsync(user, reason);
        }

        // The user row first: the audit entry is saved on its own, whatever happened to the row.
        await SignInLocks.UpdateAsync(users, db, user, u => u.LastLoginAt = clock.GetUtcNow());
        await SignInLocks.DiscardUnsavedAsync(db);
        audit.Record(AuditActions.Auth.Login, AuditOutcome.Success, AuditActor.ForUser(user.Id, user.UserName));
        await db.SaveChangesAsync(CancellationToken.None);

        http.User = await signIn.CreateUserPrincipalAsync(user);
        AntiforgeryProtection.IssueToken(http);
        return TypedResults.Ok(new LoginResponse(TwoFactorRequired: false, await DescribeAsync(user, evaluator, ct)));

        async Task<ProblemHttpResult> FailAsync(PulsarUser? known, string reason)
        {
            if (failures.TryRecord(out var leftOut))
            {
                // Identity's update of the user may have been refused (another request saved it meanwhile): not the entry.
                await SignInLocks.DiscardUnsavedAsync(db);

                // The name of an existing user only: what was typed may be a password typed in the wrong field.
                var userName = known?.UserName;
                var entry = audit.Record(AuditActions.Auth.LoginFailed, AuditOutcome.Failure,
                    leftOut == 0 ? new { userName, reason } : new { userName, reason, notRecordedBefore = leftOut });
                if (known is not null)
                {
                    entry.WithTarget("user", known.Id.ToString());
                }

                await db.SaveChangesAsync(CancellationToken.None);
            }

            return ApiProblems.Create(StatusCodes.Status401Unauthorized, "auth.invalid_credentials", "Invalid user name or password.");
        }
    }

    /// <summary>
    /// Signs out everywhere: a new security stamp ends every session of the user (a cookie copied before included, which
    /// signing out of one browser alone would leave working), and the user's live connections close now (the hub checks
    /// the cookie only when a connection opens). The user's other browsers sign in again.
    /// </summary>
    private static async Task<NoContent> LogoutAsync(
        HttpContext http, PulsarDbContext db, UserManager<PulsarUser> users, SignInManager<PulsarUser> signIn, IAuditLog audit,
        BrowserConnections connections, CancellationToken ct)
    {
        audit.Record(AuditActions.Auth.Logout);
        await db.SaveChangesAsync(ct);
        if (await users.GetUserAsync(http.User) is { } user)
        {
            await users.UpdateSecurityStampAsync(user);
        }

        await signIn.SignOutAsync();
        connections.EndUser(http.User.GetRequiredUserId());

        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        AntiforgeryProtection.IssueToken(http);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CurrentUserResponse>, UnauthorizedHttpResult>> GetMeAsync(
        HttpContext http, UserManager<PulsarUser> users, IPermissionEvaluator evaluator, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(await DescribeAsync(user, evaluator, ct));
    }

    /// <summary>
    /// Confirms the current password of a signed-in user (changing it, turning two-factor authentication off…). Wrong
    /// guesses count towards the lockout like failed sign-ins, so a stolen session cannot be used to guess the password;
    /// while locked out, every answer is "wrong".
    /// </summary>
    /// <param name="fingerprint">The SSH key being added, recorded with a refusal so the audit log says which key it was.</param>
    internal static async Task<ProblemHttpResult?> WrongPasswordAsync(PulsarUser user, string password, UserManager<PulsarUser> users,
        PulsarDbContext db, SignInLocks locks, IAuditLog audit, string action, CancellationToken ct, string? fingerprint = null)
    {
        // Under the account's lock, as the sign-ins: parallel guesses each count.
        using var turn = await locks.AcquireAsync(user.NormalizedUserName!, ct);
        await db.Entry(user).ReloadAsync(ct);
        var lockedOut = await users.IsLockedOutAsync(user);
        if (!lockedOut && await users.CheckPasswordAsync(user, password))
        {
            return null;
        }

        if (!lockedOut)
        {
            await SignInLocks.CountFailureAsync(users, db, user);
        }

        await SignInLocks.DiscardUnsavedAsync(db);
        var reason = lockedOut ? "locked_out" : "wrong_current_password";
        audit.Record(action, AuditOutcome.Failure, fingerprint is null ? new { reason } : new { reason, fingerprint })
            .WithTarget("user", user.Id.ToString());
        await db.SaveChangesAsync(CancellationToken.None);
        return ApiProblems.Create(StatusCodes.Status400BadRequest, "auth.wrong_current_password", "The current password is not correct.");
    }

    internal static async Task<CurrentUserResponse> DescribeAsync(PulsarUser user, IPermissionEvaluator evaluator, CancellationToken ct)
    {
        var permissions = await evaluator.GetAsync(user.Id, ct);
        return new CurrentUserResponse(user.Id.Value, user.UserName!, user.DisplayName, user.Email,
            [.. permissions.Global.Order(StringComparer.Ordinal)], user.TwoFactorEnabled, permissions.RequiresTwoFactor && !user.TwoFactorEnabled,
            [.. permissions.Nodes.Select(n => new ScopedPermissions(n.Key.Value, [.. n.Value.Order(StringComparer.Ordinal)]))],
            [.. permissions.Servers.Select(s => new ScopedPermissions(s.Key.Value, [.. s.Value.Order(StringComparer.Ordinal)]))]);
    }
}
