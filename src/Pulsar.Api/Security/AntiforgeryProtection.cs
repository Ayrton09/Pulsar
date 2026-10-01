using Microsoft.AspNetCore.Antiforgery;
using Pulsar.Api.Http;

namespace Pulsar.Api.Security;

/// <summary>
/// CSRF protection for the JSON API: the SPA reads the request token from a readable cookie and sends it back in the
/// <see cref="HeaderName"/> header on every state-changing request. Session cookies are also SameSite=Strict.
/// </summary>
public static class AntiforgeryProtection
{
    public const string HeaderName = "X-XSRF-TOKEN";

    /// <summary>Readable by JavaScript on purpose (it only holds the request token, never the session).</summary>
    public const string RequestTokenCookie = "__Host-pulsar-xsrf";

    public const string CookieTokenCookie = "__Host-pulsar-af";

    /// <summary>
    /// Issues a fresh request token. Tokens are bound to the current user, so call this again after sign-in and
    /// sign-out with <see cref="HttpContext.User"/> already updated.
    /// </summary>
    public static void IssueToken(HttpContext context)
    {
        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(RequestTokenCookie, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
    }
}

/// <summary>Validates the antiforgery token on every unsafe method (POST, PUT, PATCH, DELETE).</summary>
internal sealed class AntiforgeryValidationFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return ApiProblems.Create(StatusCodes.Status400BadRequest, "antiforgery.invalid",
                    "Missing or invalid antiforgery token.", $"Send the token from the {AntiforgeryProtection.RequestTokenCookie} cookie in the {AntiforgeryProtection.HeaderName} header.");
            }
        }

        return await next(context);
    }
}
