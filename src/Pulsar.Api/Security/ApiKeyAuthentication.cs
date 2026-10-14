using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Pulsar.Api.Http;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Api.Security;

/// <summary>
/// API keys on <c>/api/v1</c> (docs/architecture/02, "API keys"): <c>Authorization: Bearer pulsar_…</c>. The key acts as
/// its user, checked at every request: a revoked or expired key, or a disabled or deleted user, stops working at once.
/// Only endpoints guarded by a permission accept keys (<see cref="UserSchemes.CookieOrApiKey"/>); signing in, the
/// account, two-factor, the keys themselves and the live hub take the session cookie only.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeys keys, UserManager<PulsarUser> users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "PulsarApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (UserSchemes.BearerValue(Request) is not { } value)
        {
            return AuthenticateResult.NoResult();
        }

        var key = await keys.MatchAsync(value, Context.RequestAborted);
        if (key is null)
        {
            return AuthenticateResult.Fail("Unknown, expired or revoked API key.");
        }

        var user = await users.FindByIdAsync(key.UserId.ToString());
        if (user is null || user.IsDisabled)
        {
            return AuthenticateResult.Fail("The API key's user cannot sign in.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? ""),
            new(ApiKeyClaims.KeyId, key.Id.ToString()),
            new(ApiKeyClaims.KeyName, key.Name),
        };
        if (user.TwoFactorEnabled)
        {
            // A role that requires two-factor authentication keeps requiring it of its users' keys.
            claims.Add(new Claim(PulsarClaimsPrincipalFactory.TwoFactorClaim, "true"));
        }

        claims.AddRange((key.Permissions ?? []).Select(p => new Claim(ApiKeyClaims.Permission, p)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)), Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return Problem(StatusCodes.Status401Unauthorized, "auth.invalid_api_key", "The API key is unknown, expired or revoked.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Problem(StatusCodes.Status403Forbidden, "auth.forbidden", "You do not have permission to do this.");

    private Task Problem(int status, string code, string title)
    {
        Response.StatusCode = status;
        return ApiProblems.Create(status, code, title).ExecuteAsync(Context);
    }
}

/// <summary>
/// What a request with an API key counts against (docs/architecture/02, "API keys"): its key, or the client's address
/// when the key does not work. Found before authorization, so refused requests count as well: each costs a key lookup.
/// </summary>
internal static class ApiKeyRateLimit
{
    private static readonly object s_partition = new();

    public static IApplicationBuilder UseApiKeyRateLimitPartition(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (UserSchemes.BearerValue(context.Request) is not null)
        {
            // The handler keeps its result for the request: authorization uses it without matching the key again.
            var result = await context.AuthenticateAsync(ApiKeyAuthenticationHandler.Scheme);
            context.Items[s_partition] = result.Succeeded && result.Principal.FindFirstValue(ApiKeyClaims.KeyId) is { } key
                ? $"key:{key}"
                : $"address:{ClientAddress.PartitionOf(context.Connection.RemoteIpAddress)}";
        }

        await next(context);
    });

    /// <summary>The request's partition; null for requests without an API key (browsers have no budget).</summary>
    public static string? Partition(HttpContext context) => context.Items.TryGetValue(s_partition, out var partition) ? partition as string : null;
}

/// <summary>Claims of a request made with an API key.</summary>
public static class ApiKeyClaims
{
    public const string KeyId = "pulsar:api_key";
    public const string KeyName = "pulsar:api_key_name";

    /// <summary>One per permission the key is limited to (none: all of its user's).</summary>
    public const string Permission = "pulsar:api_key_permission";

    /// <summary>Whether the request was authenticated with an API key (not a session cookie).</summary>
    public static bool IsApiKey(this ClaimsPrincipal principal) =>
        principal.Identity is { IsAuthenticated: true, AuthenticationType: ApiKeyAuthenticationHandler.Scheme };
}

/// <summary>How user endpoints authenticate.</summary>
public static class UserSchemes
{
    /// <summary>
    /// A request with <c>Authorization: Bearer pulsar_…</c> is authenticated by its key alone (a cookie sent along is
    /// ignored: the two are never merged); any other by the session cookie.
    /// </summary>
    public const string CookieOrApiKey = "PulsarUser";

    internal static string Select(HttpContext context) =>
        BearerValue(context.Request) is not null ? ApiKeyAuthenticationHandler.Scheme : IdentityConstants.ApplicationScheme;

    internal static string? BearerValue(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return null;
        }

        var value = header["Bearer ".Length..].Trim();
        return value.StartsWith(ApiKey.TokenPrefix, StringComparison.Ordinal) ? value : null;
    }
}
