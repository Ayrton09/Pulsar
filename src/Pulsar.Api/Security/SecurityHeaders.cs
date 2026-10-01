namespace Pulsar.Api.Security;

/// <summary>
/// Browser hardening headers on every response (docs/architecture/07 and 08). The SPA is built without inline scripts,
/// CDNs or external fonts, so a strict same-origin policy applies. One narrow exception: inline &lt;style&gt; elements
/// (style-src-elem), which the dialog's scroll lock injects at runtime with content that varies (scrollbar width), so
/// neither a hash nor a nonce on the static index.html can cover it. Inline style attributes stay blocked and scripts
/// stay strict; exploiting it would first require HTML injection, which React prevents.
/// </summary>
internal static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; style-src-elem 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        await next(context);
    });
}
