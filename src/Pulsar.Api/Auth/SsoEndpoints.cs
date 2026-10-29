using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Http;
using Pulsar.Api.Realtime;
using Pulsar.Api.Security;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Auth;

/// <summary>Where the browser goes next: the provider's sign-in page.</summary>
public sealed record SsoStartResponse(string Url);

/// <param name="Password">The current password: a linked account signs in alone, so a session alone must not link one.</param>
/// <param name="Code">With two-factor authentication on: the code the authenticator app shows now.</param>
public sealed record SsoLinkRequest([property: Required, StringLength(256)] string Password, [property: StringLength(16)] string? Code);

/// <summary>An account of a provider linked to the signed-in user's.</summary>
public sealed record SsoLinkView(SsoKind Kind, string? Name);

/// <param name="ClientSecret">Leave it out to keep the stored one.</param>
/// <param name="Authority">OpenID Connect only: the issuer's https address.</param>
/// <param name="DisplayName">OpenID Connect only: what its sign-in button says.</param>
public sealed record SsoProviderRequest(
    bool Enabled,
    [property: StringLength(SsoProvider.MaxClientIdLength)] string? ClientId,
    [property: StringLength(512)] string? ClientSecret,
    [property: StringLength(SsoProvider.MaxAuthorityLength)] string? Authority,
    [property: StringLength(SsoProvider.MaxNameLength)] string? DisplayName);

/// <summary>
/// Signing in with Discord, Google or an OpenID Connect provider (Pulsar 1.0.18; docs/architecture/08, T59): only to an
/// account that exists and whose owner linked the provider's account to it, with the password and, if on, the second
/// factor. The panel's own two-factor authentication is still asked: a provider stands in for the password only.
/// The browser goes to the provider with a state, a PKCE verifier and a nonce kept in an encrypted cookie for 10
/// minutes (SameSite=Lax: it comes back from another site, which the session cookie, Strict, does not), and comes back
/// to the callback, which signs in, or links, and sends the browser on to a page of the panel.
/// </summary>
public static partial class SsoEndpoints
{
    private const string Purpose = "Pulsar.Sso.Flow.v1";
    private static readonly TimeSpan s_flowLifetime = TimeSpan.FromMinutes(10);

    public static RouteGroupBuilder MapSsoEndpoints(this RouteGroupBuilder api)
    {
        var sso = api.MapGroup("/auth/sso").WithTags("Auth");
        sso.MapGet("/", async (SsoProviders providers, CancellationToken ct) => TypedResults.Ok(await providers.EnabledAsync(ct)))
            .AllowAnonymous().RequireRateLimiting(AuthEndpoints.AnonymousRateLimitPolicy);
        sso.MapPost("/{provider}/login", LoginAsync).AllowAnonymous().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        sso.MapGet("/{provider}/callback", CallbackAsync).AllowAnonymous().RequireRateLimiting(AuthEndpoints.RateLimitPolicy)
            .ExcludeFromDescription();

        var account = api.MapGroup("/account/sso").WithTags("Account");
        account.MapGet("/", ListLinksAsync).RequireSignedInUser();
        account.MapPost("/{provider}/link", LinkAsync).RequireSignedInUser().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        account.MapDelete("/{provider}", UnlinkAsync).RequireSignedInUser();

        // The providers themselves: panel settings (their secrets are the panel's).
        var providers = api.MapGroup("/sso-providers").WithTags("Auth").RequirePermission(Permissions.Settings.Manage);
        providers.MapGet("/", async (SsoProviders p, CancellationToken ct) => TypedResults.Ok(await p.ListAsync(ct)));
        providers.MapPut("/{provider}", UpdateProviderAsync);
        return api;
    }

    /// <summary>
    /// A provider's settings; when the OpenID Connect issuer or client changes, the accounts linked to it go in the same
    /// transaction (their subjects were the old issuer's: kept, the new issuer could sign in as their owners), and those
    /// users' sessions end.
    /// </summary>
    private static async Task<Results<Ok<SsoProviderView>, ProblemHttpResult>> UpdateProviderAsync(string provider, SsoProviderRequest request,
        PulsarDbContext db, UserManager<PulsarUser> users, SsoProviders providers, BrowserConnections connections, CancellationToken ct)
    {
        if (SsoProviders.Parse(provider) is not { } kind)
        {
            return ApiProblems.FromDomainError(SsoErrors.NotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var updated = await providers.UpdateAsync(kind, new SsoProviderDefinition(request.Enabled, request.ClientId, request.ClientSecret, request.Authority,
            request.DisplayName), ct);
        if (!updated.IsSuccess)
        {
            return ApiProblems.FromDomainError(updated.Error!);
        }

        var ended = new List<PulsarUser>();
        if (updated.Value.LinksStale)
        {
            var slug = SsoProviders.Slug(kind);
            var owners = await db.UserLogins.Where(l => l.LoginProvider == slug).Select(l => new { l.UserId, l.ProviderKey }).ToListAsync(ct);
            foreach (var owner in owners)
            {
                if (await users.FindByIdAsync(owner.UserId.ToString()) is { } user)
                {
                    await users.RemoveLoginAsync(user, slug, owner.ProviderKey); // renews the security stamp
                    ended.Add(user);
                }
            }
        }

        await transaction.CommitAsync(ct);
        foreach (var user in ended)
        {
            connections.EndUser(user.Id);
        }

        return TypedResults.Ok(updated.Value.View);
    }

    private static Task<Results<Ok<SsoStartResponse>, ProblemHttpResult>> LoginAsync(string provider, HttpContext http, SsoProviders providers,
        ISsoClient client, IDataProtectionProvider protection, PanelCookies cookies, CancellationToken ct) =>
        StartAsync(provider, Flow.Login, null, null, http, providers, client, protection, cookies, ct);

    private static async Task<Results<Ok<SsoStartResponse>, ProblemHttpResult, UnauthorizedHttpResult>> LinkAsync(string provider, SsoLinkRequest request,
        HttpContext http, PulsarDbContext db, UserManager<PulsarUser> users, SignInLocks locks, IAuditLog audit, SsoProviders providers, ISsoClient client,
        IDataProtectionProvider protection, PanelCookies cookies, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (SsoProviders.Parse(provider) is not { } kind || await providers.SettingsAsync(kind, ct) is null)
        {
            return ApiProblems.FromDomainError(SsoErrors.NotFound);
        }

        if (await AuthEndpoints.WrongPasswordAsync(user, request.Password, users, db, locks, audit, AuditActions.Auth.SsoLinked, ct) is { } wrong)
        {
            return wrong;
        }

        if (user.TwoFactorEnabled && await TwoFactorEndpoints.WrongCodeAsync(user, request.Code, users, db, locks, audit, AuditActions.Auth.SsoLinked, ct)
            is { } wrongCode)
        {
            return wrongCode;
        }

        var started = await StartAsync(provider, Flow.Link, user.Id.Value, user.SecurityStamp, http, providers, client, protection, cookies, ct);
        return started.Result switch
        {
            Ok<SsoStartResponse> ok => ok,
            ProblemHttpResult problem => problem,
            _ => throw new InvalidOperationException("Unexpected result."),
        };
    }

    /// <summary>The provider's address, with the flow's secrets kept in the browser's encrypted cookie.</summary>
    private static async Task<Results<Ok<SsoStartResponse>, ProblemHttpResult>> StartAsync(string provider, Flow flow, Guid? userId, string? stamp,
        HttpContext http, SsoProviders providers, ISsoClient client, IDataProtectionProvider protection, PanelCookies cookies, CancellationToken ct)
    {
        if (SsoProviders.Parse(provider) is not { } kind || await providers.SettingsAsync(kind, ct) is not { } settings)
        {
            return ApiProblems.FromDomainError(SsoErrors.NotFound);
        }

        var state = Random();
        var verifier = Random();
        var nonce = Random();
        var redirect = RedirectUri(http, kind);
        Uri url;
        try
        {
            url = await client.AuthorizeAsync(settings, redirect, state, Challenge(verifier), nonce, ct);
        }
        catch (SsoException ex)
        {
            return ApiProblems.Create(StatusCodes.Status502BadGateway, ex.Code, ex.Message);
        }

        var pending = new PendingSignIn(kind, flow, state, verifier, nonce, redirect, userId, stamp);
        var protector = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        http.Response.Cookies.Append(cookies.Sso, protector.Protect(JsonSerializer.Serialize(pending), s_flowLifetime),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = cookies.SecurePolicy == CookieSecurePolicy.Always || http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = s_flowLifetime,
                IsEssential = true,
            });
        return TypedResults.Ok(new SsoStartResponse(url.AbsoluteUri));
    }

    /// <summary>
    /// The provider sends the browser back here: the flow's cookie must match (its state, one use), the code is redeemed,
    /// and the account signed in to, or linked; the browser goes on to the sign-in page (or the account page) of the panel.
    /// </summary>
    private static async Task<RedirectHttpResult> CallbackAsync(string provider, string? code, string? state, string? error, HttpContext http,
        PulsarDbContext db, UserManager<PulsarUser> users, SignInManager<PulsarUser> signIn, SsoProviders providers, ISsoClient client,
        IDataProtectionProvider protection, PanelCookies cookies, IAuditLog audit, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var pending = Read(http, protection, cookies);
        // One use: whatever happens next, the flow is over.
        http.Response.Cookies.Delete(cookies.Sso, new CookieOptions { Path = "/", Secure = cookies.SecurePolicy == CookieSecurePolicy.Always || http.Request.IsHttps });
        var kind = SsoProviders.Parse(provider);
        var failurePage = pending?.Flow == Flow.Link ? "/account" : "/login";
        if (pending is null || kind is null || pending.Kind != kind || state is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(pending.State)))
        {
            return Go(failurePage, "expired");
        }

        if (error is not null || string.IsNullOrEmpty(code) || code.Length > 2048)
        {
            return Go(failurePage, "denied");
        }

        if (await providers.SettingsAsync(kind.Value, ct) is not { } settings)
        {
            return Go(failurePage, "not_found");
        }

        SsoIdentity identity;
        try
        {
            identity = await client.RedeemAsync(settings, code, pending.Verifier, pending.RedirectUri, pending.Nonce, ct);
        }
        catch (SsoException ex)
        {
            LogRefused(loggers.CreateLogger(typeof(SsoEndpoints)), SsoProviders.Slug(kind.Value), ex.Code, ex.Message);
            return Go(failurePage, ex.Code["sso.".Length..]);
        }

        var slug = SsoProviders.Slug(kind.Value);
        return pending.Flow == Flow.Link
            ? await CompleteLinkAsync(pending, slug, identity, db, users, audit)
            : await CompleteLoginAsync(slug, identity, http, db, users, signIn, audit, clock);
    }

    private static async Task<RedirectHttpResult> CompleteLoginAsync(string slug, SsoIdentity identity, HttpContext http, PulsarDbContext db,
        UserManager<PulsarUser> users, SignInManager<PulsarUser> signIn, IAuditLog audit, TimeProvider clock)
    {
        var user = await users.FindByLoginAsync(slug, identity.Subject);
        if (user is null)
        {
            audit.Record(AuditActions.Auth.LoginFailed, AuditOutcome.Failure, new { reason = "sso_not_linked", provider = slug });
            await db.SaveChangesAsync(CancellationToken.None);
            return Go("/login", "not_linked");
        }

        SessionLifetime.Remember(http, false);
        // Identity's own checks (disabled, locked out) and its second factor: the panel's two-factor authentication still applies.
        var result = await signIn.ExternalLoginSignInAsync(slug, identity.Subject, isPersistent: false, bypassTwoFactor: false);
        if (result.RequiresTwoFactor)
        {
            // The sign-in is the second step's to record; this says which provider got it there.
            audit.Record(AuditActions.Auth.SsoSecondFactorAsked, AuditOutcome.Success, AuditActor.ForUser(user.Id, user.UserName), new { provider = slug })
                .WithTarget("user", user.Id.ToString());
            await db.SaveChangesAsync(CancellationToken.None);
            return TypedResults.Redirect("/login?sso=two_factor");
        }

        if (!result.Succeeded)
        {
            await SignInLocks.DiscardUnsavedAsync(db);
            audit.Record(AuditActions.Auth.LoginFailed, AuditOutcome.Failure,
                new { userName = user.UserName, reason = result.IsLockedOut ? "locked_out" : "not_allowed", provider = slug })
                .WithTarget("user", user.Id.ToString());
            await db.SaveChangesAsync(CancellationToken.None);
            return Go("/login", "not_allowed");
        }

        await SignInLocks.UpdateAsync(users, db, user, u => u.LastLoginAt = clock.GetUtcNow());
        await SignInLocks.DiscardUnsavedAsync(db);
        audit.Record(AuditActions.Auth.Login, AuditOutcome.Success, AuditActor.ForUser(user.Id, user.UserName), new { provider = slug });
        await db.SaveChangesAsync(CancellationToken.None);
        return TypedResults.Redirect("/");
    }

    /// <summary>
    /// The account the flow was started for, still as it was (the same security stamp: no password change, no sign-out
    /// everywhere since) gets the provider's account, unless another account has it or this one has another of that provider.
    /// </summary>
    private static async Task<RedirectHttpResult> CompleteLinkAsync(PendingSignIn pending, string slug, SsoIdentity identity, PulsarDbContext db,
        UserManager<PulsarUser> users, IAuditLog audit)
    {
        var user = pending.UserId is { } id ? await users.FindByIdAsync(id.ToString()) : null;
        if (user is null || user.IsDisabled || !string.Equals(user.SecurityStamp, pending.Stamp, StringComparison.Ordinal))
        {
            return Go("/account", "expired");
        }

        var owner = await users.FindByLoginAsync(slug, identity.Subject);
        var current = (await users.GetLoginsAsync(user)).FirstOrDefault(l => l.LoginProvider == slug);
        string? refusal = owner is not null && owner.Id != user.Id ? "linked_elsewhere"
            : current is not null && current.ProviderKey != identity.Subject ? "provider_linked"
            : null;
        if (refusal is null && current is null)
        {
            var added = await users.AddLoginAsync(user, new UserLoginInfo(slug, identity.Subject, identity.Name));
            refusal = added.Succeeded ? null : "linked_elsewhere";
        }

        audit.Record(AuditActions.Auth.SsoLinked, refusal is null ? AuditOutcome.Success : AuditOutcome.Failure,
                AuditActor.ForUser(user.Id, user.UserName), refusal is null ? new { provider = slug, name = identity.Name } : new { provider = slug, reason = refusal })
            .WithTarget("user", user.Id.ToString());
        await db.SaveChangesAsync(CancellationToken.None);
        return refusal is null ? TypedResults.Redirect($"/account?sso=linked") : Go("/account", refusal);
    }

    private static async Task<Ok<List<SsoLinkView>>> ListLinksAsync(HttpContext http, UserManager<PulsarUser> users)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        var logins = user is null ? [] : await users.GetLoginsAsync(user);
        return TypedResults.Ok(logins.Select(l => SsoProviders.Parse(l.LoginProvider) is { } kind ? new SsoLinkView(kind, l.ProviderDisplayName) : null)
            .OfType<SsoLinkView>().OrderBy(l => l.Kind).ToList());
    }

    /// <summary>The link goes, and with it every session (signed in with it or not): this one is signed in again.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult, UnauthorizedHttpResult>> UnlinkAsync(string provider, HttpContext http,
        PulsarDbContext db, UserManager<PulsarUser> users, SignInManager<PulsarUser> signIn, IAuditLog audit, BrowserConnections connections)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var slug = SsoProviders.Parse(provider) is { } kind ? SsoProviders.Slug(kind) : null;
        var login = slug is null ? null : (await users.GetLoginsAsync(user)).FirstOrDefault(l => l.LoginProvider == slug);
        if (login is null)
        {
            return ApiProblems.Create(StatusCodes.Status404NotFound, "sso.link_not_found", "This account is not linked to that provider.");
        }

        audit.Record(AuditActions.Auth.SsoUnlinked, AuditOutcome.Success, AuditActor.ForUser(user.Id, user.UserName), new { provider = slug, name = login.ProviderDisplayName })
            .WithTarget("user", user.Id.ToString());
        await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey); // renews the security stamp, saving the entry
        // The live channel checks the session only when it opens: whoever signed in with the link loses theirs now. This
        // browser reconnects with the session signed in again below.
        connections.EndUser(user.Id);
        await signIn.RefreshSignInAsync(user);
        return TypedResults.NoContent();
    }

    private static PendingSignIn? Read(HttpContext http, IDataProtectionProvider protection, PanelCookies cookies)
    {
        if (http.Request.Cookies[cookies.Sso] is not { Length: > 0 and < 4096 } value)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingSignIn>(protection.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(value));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null; // expired, from another key ring, or not ours
        }
    }

    /// <summary>Where the provider sends the browser back: this panel, as the browser reached it.</summary>
    private static string RedirectUri(HttpContext http, SsoKind kind) => $"{http.Request.Scheme}://{http.Request.Host}{SsoProviders.CallbackPath(kind)}";

    /// <summary>Always a page of the panel, with a code the page translates (never what the provider said).</summary>
    private static RedirectHttpResult Go(string page, string error) => TypedResults.Redirect($"{page}?sso_error={Uri.EscapeDataString(error)}");

    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Challenge(string verifier) => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sign-in with {Provider} was refused ({Code}): {Reason}")]
    private static partial void LogRefused(ILogger logger, string provider, string code, string reason);

    private enum Flow
    {
        Login,
        Link,
    }

    private sealed record PendingSignIn(SsoKind Kind, Flow Flow, string State, string Verifier, string Nonce, string RedirectUri, Guid? UserId, string? Stamp);
}
