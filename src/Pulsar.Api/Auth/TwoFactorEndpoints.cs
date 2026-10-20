using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Pulsar.Api.Http;
using Pulsar.Api.Realtime;
using Pulsar.Api.Security;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Auth;

/// <summary>
/// Two-factor authentication with an authenticator app (TOTP) and recovery codes (docs/architecture/08): the second
/// sign-in step and the user's own setup.
/// </summary>
public static class TwoFactorEndpoints
{
    public const int RecoveryCodeCount = 10;
    private const string Issuer = "Pulsar";

    public static RouteGroupBuilder MapTwoFactorEndpoints(this RouteGroupBuilder auth)
    {
        auth.MapPost("/login/two-factor", LoginAsync).AllowAnonymous().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        var twoFactor = auth.MapGroup("/two-factor");
        twoFactor.MapGet("/", GetStatusAsync).RequireSignedInUser().AllowWithoutTwoFactor();
        twoFactor.MapPost("/setup", SetupAsync).RequireSignedInUser().AllowWithoutTwoFactor().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        twoFactor.MapPost("/enable", EnableAsync).RequireSignedInUser().AllowWithoutTwoFactor()
            .RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        twoFactor.MapPost("/disable", DisableAsync).RequireSignedInUser().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        twoFactor.MapPost("/recovery-codes", RenewRecoveryCodesAsync).RequireSignedInUser().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        return auth;
    }

    /// <summary>Turns two-factor authentication off: forgets the key, the codes and the replay guard; sessions end.</summary>
    public static async Task TurnOffAsync(UserManager<PulsarUser> users, PulsarUser user)
    {
        ArgumentNullException.ThrowIfNull(users);
        foreach (var name in (string[])[PulsarUserStore.AuthenticatorKeyName, PulsarUserStore.RecoveryCodesName, PulsarUserStore.LastStepName])
        {
            await users.RemoveAuthenticationTokenAsync(user, PulsarUserStore.LoginProvider, name);
        }

        await users.SetTwoFactorEnabledAsync(user, false); // renews the security stamp
    }

    /// <summary>
    /// The second step after a correct password: an authenticator code or a recovery code. Wrong authenticator codes
    /// count towards the lockout, like wrong passwords; every answer that is not a sign-in is the same.
    /// </summary>
    private static async Task<Results<Ok<CurrentUserResponse>, ProblemHttpResult>> LoginAsync(
        TwoFactorLoginRequest request,
        HttpContext http,
        PulsarDbContext db,
        UserManager<PulsarUser> users,
        SignInManager<PulsarUser> signIn,
        IPermissionEvaluator evaluator,
        IAuditLog audit,
        SignInLocks locks,
        TimeProvider clock,
        CancellationToken ct)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return InvalidCode();
        }

        // Under the account's lock, as the password step: parallel guesses each count and keep their audit entries.
        using var turn = await locks.AcquireAsync(user.NormalizedUserName!, ct);
        await db.Entry(user).ReloadAsync(ct);
        var recovery = !string.IsNullOrWhiteSpace(request.RecoveryCode);
        SignInResult result;
        if (await users.IsLockedOutAsync(user))
        {
            result = SignInResult.LockedOut;
        }
        else if (recovery)
        {
            result = await signIn.TwoFactorRecoveryCodeSignInAsync(request.RecoveryCode!);
        }
        else
        {
            var code = PulsarAuthenticatorTokenProvider.Normalize(request.Code);
            result = code is null ? SignInResult.Failed : await signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: false);
        }

        var actor = AuditActor.ForUser(user.Id, user.UserName);
        if (!result.Succeeded)
        {
            // Identity's update of the user may have been refused (another request saved it meanwhile): not the entry.
            await SignInLocks.DiscardUnsavedAsync(db);
            audit.Record(AuditActions.Auth.TwoFactorFailed, AuditOutcome.Failure, actor,
                    new { method = recovery ? "recovery_code" : "authenticator", reason = result.IsLockedOut ? "locked_out" : "invalid_code" })
                .WithTarget("user", user.Id.ToString());
            await db.SaveChangesAsync(CancellationToken.None);
            return InvalidCode();
        }

        // The user row first: the audit entries are saved on their own, whatever happened to the row.
        await SignInLocks.UpdateAsync(users, db, user, u => u.LastLoginAt = clock.GetUtcNow());
        await SignInLocks.DiscardUnsavedAsync(db);
        audit.Record(AuditActions.Auth.Login, AuditOutcome.Success, actor, new { secondFactor = recovery ? "recovery_code" : "authenticator" });
        if (recovery)
        {
            audit.Record(AuditActions.Auth.RecoveryCodeUsed, AuditOutcome.Success, actor, new { left = await users.CountRecoveryCodesAsync(user) })
                .WithTarget("user", user.Id.ToString());
        }

        await db.SaveChangesAsync(CancellationToken.None);

        http.User = await signIn.CreateUserPrincipalAsync(user);
        AntiforgeryProtection.IssueToken(http);
        return TypedResults.Ok(await AuthEndpoints.DescribeAsync(user, evaluator, ct));

        static ProblemHttpResult InvalidCode() =>
            ApiProblems.Create(StatusCodes.Status401Unauthorized, "auth.invalid_code", "The code is not valid. Sign in again if it keeps failing.");
    }

    private static async Task<Results<Ok<TwoFactorStatusResponse>, UnauthorizedHttpResult>> GetStatusAsync(
        HttpContext http, UserManager<PulsarUser> users, IPermissionEvaluator evaluator, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var required = (await evaluator.GetAsync(user.Id, ct)).RequiresTwoFactor;
        var left = user.TwoFactorEnabled ? await users.CountRecoveryCodesAsync(user) : 0;
        return TypedResults.Ok(new TwoFactorStatusResponse(user.TwoFactorEnabled, required, left));
    }

    /// <summary>
    /// A new authenticator key for the user's app. Nothing changes at sign-in until a code from the app turns two-factor
    /// authentication on (<c>/enable</c>). Refused while it is on: turning it off comes first. Takes the current password:
    /// a new key ends the user's other sessions, which a stolen session alone must not do.
    /// </summary>
    private static async Task<Results<Ok<TwoFactorSetupResponse>, ProblemHttpResult, UnauthorizedHttpResult>> SetupAsync(
        PasswordConfirmationRequest request, HttpContext http, PulsarDbContext db, UserManager<PulsarUser> users, SignInManager<PulsarUser> signIn,
        SignInLocks locks, IAuditLog audit, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (await AuthEndpoints.WrongPasswordAsync(user, request.Password, users, db, locks, audit, AuditActions.Auth.TwoFactorEnabled, ct) is { } wrong)
        {
            return wrong;
        }

        if (user.TwoFactorEnabled)
        {
            return ApiProblems.Create(StatusCodes.Status409Conflict, "auth.two_factor_enabled", "Two-factor authentication is already on.");
        }

        await users.ResetAuthenticatorKeyAsync(user); // renews the security stamp; this session is renewed below
        await RenewSessionAsync(http, signIn, user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        var uri = $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(user.UserName!)}" +
            $"?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";
        return TypedResults.Ok(new TwoFactorSetupResponse(FormatKey(key), uri));
    }

    /// <summary>
    /// Turns two-factor authentication on with a code from the app and the current password (a stolen session alone
    /// must not lock the owner out and get the recovery codes), and returns the recovery codes (shown once). The user's
    /// other sessions end.
    /// </summary>
    private static async Task<Results<Ok<RecoveryCodesResponse>, ProblemHttpResult, UnauthorizedHttpResult>> EnableAsync(
        EnableTwoFactorRequest request, HttpContext http, PulsarDbContext db, UserManager<PulsarUser> users, SignInManager<PulsarUser> signIn,
        SignInLocks locks, IAuditLog audit, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (await AuthEndpoints.WrongPasswordAsync(user, request.Password, users, db, locks, audit, AuditActions.Auth.TwoFactorEnabled, ct) is { } wrong)
        {
            return wrong;
        }

        if (user.TwoFactorEnabled)
        {
            return ApiProblems.Create(StatusCodes.Status409Conflict, "auth.two_factor_enabled", "Two-factor authentication is already on.");
        }

        var code = PulsarAuthenticatorTokenProvider.Normalize(request.Code);
        if (code is null || !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            audit.Record(AuditActions.Auth.TwoFactorFailed, AuditOutcome.Failure, new { method = "authenticator", reason = "setup" })
                .WithTarget("user", user.Id.ToString());
            await db.SaveChangesAsync(ct);
            return ApiProblems.Create(StatusCodes.Status400BadRequest, "auth.invalid_code",
                "The code is not valid.", "Check that the device's clock is right and type the code the app shows now.");
        }

        audit.Record(AuditActions.Auth.TwoFactorEnabled).WithTarget("user", user.Id.ToString());
        await users.SetTwoFactorEnabledAsync(user, true); // renews the security stamp: other sessions end
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        await RenewSessionAsync(http, signIn, user);
        return TypedResults.Ok(new RecoveryCodesResponse([.. codes ?? []]));
    }

    private static async Task<Results<NoContent, ProblemHttpResult, UnauthorizedHttpResult>> DisableAsync(
        PasswordConfirmationRequest request, HttpContext http, PulsarDbContext db, UserManager<PulsarUser> users, SignInManager<PulsarUser> signIn,
        SignInLocks locks, IPermissionEvaluator evaluator, IAuditLog audit, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (await AuthEndpoints.WrongPasswordAsync(user, request.Password, users, db, locks, audit, AuditActions.Auth.TwoFactorDisabled, ct) is { } wrong)
        {
            return wrong;
        }

        if ((await evaluator.GetAsync(user.Id, ct)).RequiresTwoFactor)
        {
            return ApiProblems.Create(StatusCodes.Status409Conflict, "auth.two_factor_required_by_role",
                "One of your roles requires two-factor authentication.");
        }

        if (user.TwoFactorEnabled)
        {
            audit.Record(AuditActions.Auth.TwoFactorDisabled).WithTarget("user", user.Id.ToString());
            await TurnOffAsync(users, user);
            await RenewSessionAsync(http, signIn, user);
        }

        return TypedResults.NoContent();
    }

    /// <summary>New recovery codes; the old ones stop working.</summary>
    private static async Task<Results<Ok<RecoveryCodesResponse>, ProblemHttpResult, UnauthorizedHttpResult>> RenewRecoveryCodesAsync(
        PasswordConfirmationRequest request, HttpContext http, PulsarDbContext db, UserManager<PulsarUser> users, SignInLocks locks, IAuditLog audit,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (await AuthEndpoints.WrongPasswordAsync(user, request.Password, users, db, locks, audit, AuditActions.Auth.RecoveryCodesRenewed, ct) is { } wrong)
        {
            return wrong;
        }

        if (!user.TwoFactorEnabled)
        {
            return ApiProblems.Create(StatusCodes.Status409Conflict, "auth.two_factor_not_enabled", "Two-factor authentication is off.");
        }

        audit.Record(AuditActions.Auth.RecoveryCodesRenewed).WithTarget("user", user.Id.ToString());
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount); // also saves the audit entry
        return TypedResults.Ok(new RecoveryCodesResponse([.. codes ?? []]));
    }

    /// <summary>
    /// Confirms a code from the authenticator app of a signed-in user who has two-factor authentication on (adding an SSH
    /// key): recovery codes are not accepted. Under the account's lock, as the sign-ins: a wrong code counts towards the
    /// lockout, as at sign-in, so a stolen session and a known password cannot be used to guess it; while locked out,
    /// every answer is "wrong". A code works once (the replay guard), here too.
    /// </summary>
    internal static async Task<ProblemHttpResult?> WrongCodeAsync(PulsarUser user, string? code, UserManager<PulsarUser> users, PulsarDbContext db,
        SignInLocks locks, IAuditLog audit, string action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(audit);
        if (string.IsNullOrWhiteSpace(code))
        {
            // No answer, no guess: nothing counts.
            return InvalidCode();
        }

        using var turn = await locks.AcquireAsync(user.NormalizedUserName!, ct);
        await db.Entry(user).ReloadAsync(ct);
        var lockedOut = await users.IsLockedOutAsync(user);
        var normalized = PulsarAuthenticatorTokenProvider.Normalize(code);
        if (!lockedOut && normalized is not null && await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, normalized))
        {
            return null;
        }

        if (!lockedOut)
        {
            await SignInLocks.CountFailureAsync(users, db, user);
        }

        // A refused save of the code's replay guard (another request saved the user meanwhile) is not the entry's.
        await SignInLocks.DiscardUnsavedAsync(db);
        audit.Record(action, AuditOutcome.Failure, new { reason = lockedOut ? "locked_out" : "invalid_code" }).WithTarget("user", user.Id.ToString());
        await db.SaveChangesAsync(CancellationToken.None);
        return InvalidCode();

        static ProblemHttpResult InvalidCode() => ApiProblems.Create(StatusCodes.Status400BadRequest, "auth.invalid_code",
            "The code is not valid.", "Type the code your authenticator app shows now (recovery codes do not work here).");
    }

    // The security stamp changed: other sessions end, live channels too (this browser's reconnects with the new cookie).
    private static async Task RenewSessionAsync(HttpContext http, SignInManager<PulsarUser> signIn, PulsarUser user)
    {
        http.RequestServices.GetRequiredService<BrowserConnections>().EndUser(user.Id);
        await signIn.RefreshSignInAsync(user);
        http.User = await signIn.CreateUserPrincipalAsync(user);
        AntiforgeryProtection.IssueToken(http);
    }

    // "JBSW Y3DP EHPK 3PXP …": easier to type into an app than one long string.
    private static string FormatKey(string key)
    {
        var formatted = new StringBuilder(key.Length + key.Length / 4);
        for (var i = 0; i < key.Length; i += 4)
        {
            formatted.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }

        return formatted.ToString().TrimEnd();
    }
}
