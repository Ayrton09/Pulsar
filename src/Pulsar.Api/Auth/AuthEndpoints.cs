using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Http;
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

    // Verified when the user name does not exist, so both paths cost one password hash (no user enumeration by timing).
    private static readonly string s_dummyPasswordHash =
        new PasswordHasher<PulsarUser>().HashPassword(new PulsarUser(), "pulsar-timing-equalizer");

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/session", GetSessionAsync).AllowAnonymous();
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
        IAuditLog audit, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var changed = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!changed.Succeeded)
        {
            if (changed.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
            {
                audit.Record(AuditActions.Auth.PasswordChanged, AuditOutcome.Failure, new { reason = "wrong_current_password" })
                    .WithTarget("user", user.Id.ToString());
                await db.SaveChangesAsync(ct);
                return ApiProblems.Create(StatusCodes.Status400BadRequest, "auth.wrong_current_password", "The current password is not correct.");
            }

            return IdentityProblems.Validation(changed);
        }

        audit.Record(AuditActions.Auth.PasswordChanged).WithTarget("user", user.Id.ToString());
        await db.SaveChangesAsync(ct);
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
        TimeProvider clock,
        CancellationToken ct)
    {
        var user = await users.FindByNameAsync(request.UserName);
        if (user is null)
        {
            users.PasswordHasher.VerifyHashedPassword(new PulsarUser(), s_dummyPasswordHash, request.Password);
            return await FailAsync(null, "invalid_credentials");
        }

        var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
        {
            // The password was right; the sign-in (and its audit entry) waits for the second factor.
            return TypedResults.Ok(new LoginResponse(TwoFactorRequired: true, User: null));
        }

        if (!result.Succeeded)
        {
            // Locked-out, disabled and wrong-password attempts get the same answer, so the response says nothing about the account.
            var reason = result.IsLockedOut ? "locked_out" : result.IsNotAllowed ? "not_allowed" : "invalid_credentials";
            return await FailAsync(user, reason);
        }

        user.LastLoginAt = clock.GetUtcNow();
        audit.Record(AuditActions.Auth.Login, AuditOutcome.Success, AuditActor.ForUser(user.Id, user.UserName));
        await users.UpdateAsync(user); // also saves the audit entry (same unit of work)

        http.User = await signIn.CreateUserPrincipalAsync(user);
        AntiforgeryProtection.IssueToken(http);
        return TypedResults.Ok(new LoginResponse(TwoFactorRequired: false, await DescribeAsync(user, evaluator, ct)));

        async Task<ProblemHttpResult> FailAsync(PulsarUser? known, string reason)
        {
            var entry = audit.Record(AuditActions.Auth.LoginFailed, AuditOutcome.Failure,
                new { userName = Truncate(request.UserName, 64), reason });
            if (known is not null)
            {
                entry.WithTarget("user", known.Id.ToString());
            }

            await db.SaveChangesAsync(ct);
            return ApiProblems.Create(StatusCodes.Status401Unauthorized, "auth.invalid_credentials", "Invalid user name or password.");
        }
    }

    private static async Task<NoContent> LogoutAsync(
        HttpContext http, PulsarDbContext db, SignInManager<PulsarUser> signIn, IAuditLog audit, CancellationToken ct)
    {
        audit.Record(AuditActions.Auth.Logout);
        await db.SaveChangesAsync(ct);
        await signIn.SignOutAsync();

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

    internal static async Task<CurrentUserResponse> DescribeAsync(PulsarUser user, IPermissionEvaluator evaluator, CancellationToken ct)
    {
        var permissions = await evaluator.GetAsync(user.Id, ct);
        return new CurrentUserResponse(user.Id.Value, user.UserName!, user.DisplayName, user.Email,
            [.. permissions.Global.Order(StringComparer.Ordinal)], user.TwoFactorEnabled, permissions.RequiresTwoFactor && !user.TwoFactorEnabled);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
