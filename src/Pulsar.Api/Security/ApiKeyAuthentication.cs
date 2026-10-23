using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
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
/// What requests with an API key count against (docs/architecture/02, "API keys"), decided before the key is looked up:
/// a key known to work (it worked within the last hour) counts against its own budget; any other value (a guess, a
/// revoked or expired key, the first request of a key) against its client address's budget of lookups. A request over
/// either is refused without reading the database. A key's first request counts against both: the lookup it costs, then
/// its own budget. Clients sharing an address (a household, an office) share only the lookups of keys unused for an
/// hour: keys in use keep their own budgets whatever is guessed from their address.
/// </summary>
internal sealed class ApiKeyRequestBudget : IDisposable
{
    /// <summary>Only keys that worked are remembered (25 a user at most): the bound is never near for a real panel.</summary>
    public const int MaxKnownKeys = 100_000;

    private const string KeyPartition = "key:";
    private static readonly TimeSpan s_window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_remembered = TimeSpan.FromHours(1);

    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly MemoryCache _known = new(new MemoryCacheOptions { SizeLimit = MaxKnownKeys });

    public ApiKeyRequestBudget(IOptions<AuthRateLimitOptions> options)
    {
        var limits = options.Value;
        _limiter = PartitionedRateLimiter.Create<string, string>(partition => RateLimitPartition.GetFixedWindowLimiter(partition,
            name => new FixedWindowRateLimiterOptions
            {
                PermitLimit = name.StartsWith(KeyPartition, StringComparison.Ordinal) ? limits.ApiKeyPermitLimit : limits.ApiKeyLookupPermitLimit,
                Window = s_window,
                QueueLimit = 0,
            }));
    }

    /// <summary>The key a bearer value was last found to be, while remembered; null for any other value.</summary>
    public string? KnownKey(string value) => _known.TryGetValue(CacheKey(value), out string? key) ? key : null;

    public void Remember(string value, string key) =>
        _known.Set(CacheKey(value), key, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = s_remembered });

    public void Forget(string value) => _known.Remove(CacheKey(value));

    public RateLimitLease ForKey(string key) => _limiter.AttemptAcquire(KeyPartition + key);

    public RateLimitLease ForLookup(IPAddress? client) => _limiter.AttemptAcquire("address:" + ClientAddress.PartitionOf(client));

    public void Dispose()
    {
        _limiter.Dispose();
        _known.Dispose();
    }

    // The value's hash, as stored: the plain value is never kept.
    private static string CacheKey(string value) => Convert.ToBase64String(ApiKey.Hash(value));
}

internal static class ApiKeyRateLimit
{
    /// <summary>
    /// Before authorization, so refused requests count as well; before the key is looked up, so a request over its
    /// budget costs the database nothing (<see cref="ApiKeyRequestBudget"/>). Browsers (no key) have no budget here.
    /// </summary>
    public static IApplicationBuilder UseApiKeyRequestBudget(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (UserSchemes.BearerValue(context.Request) is not { } value)
        {
            await next(context);
            return;
        }

        var budget = context.RequestServices.GetRequiredService<ApiKeyRequestBudget>();
        var known = budget.KnownKey(value);
        using (var lease = known is null ? budget.ForLookup(context.Connection.RemoteIpAddress) : budget.ForKey(known))
        {
            if (!lease.IsAcquired)
            {
                await ApiServiceCollectionExtensions.WriteRateLimitedAsync(context, lease);
                return;
            }
        }

        // The handler keeps its result for the request: authorization uses it without matching the key again.
        var result = await context.AuthenticateAsync(ApiKeyAuthenticationHandler.Scheme);
        if (result.Succeeded && result.Principal.FindFirstValue(ApiKeyClaims.KeyId) is { } key)
        {
            budget.Remember(value, key);
            if (!string.Equals(key, known, StringComparison.Ordinal))
            {
                // Its first request: counted against its own budget too.
                using var lease = budget.ForKey(key);
                if (!lease.IsAcquired)
                {
                    await ApiServiceCollectionExtensions.WriteRateLimitedAsync(context, lease);
                    return;
                }
            }
        }
        else
        {
            // Revoked, expired or its user disabled: from now on its requests are lookups again.
            budget.Forget(value);
        }

        await next(context);
    });
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
