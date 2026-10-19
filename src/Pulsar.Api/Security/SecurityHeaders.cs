namespace Pulsar.Api.Security;

/// <summary>
/// Browser hardening headers on every response (docs/architecture/07 and 08). The SPA is built without inline scripts,
/// CDNs or external fonts, so a strict same-origin policy applies. One narrow exception: inline &lt;style&gt; elements
/// (style-src-elem), which the dialog's scroll lock injects at runtime with content that varies (scrollbar width), so
/// neither a hash nor a nonce on the static index.html can cover it. Inline style attributes stay blocked and scripts
/// stay strict; exploiting it would first require HTML injection, which React prevents. Images may also come from
/// Steam's store CDN while an administrator leaves the game artwork on (<see cref="SteamArtworkPolicy"/>): the artwork on
/// server cards and pages. The browser asks Steam for them with no referrer, and the SPA only builds those addresses from
/// a fixed table of app numbers, never from data a user typed. With the artwork off, or the setting not read yet, the
/// policy names no Steam origin, so no browser contacts Steam.
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>Steam's store CDN, where the game artwork comes from.</summary>
    public const string SteamArtworkOrigin = "https://shared.akamai.steamstatic.com";

    /// <summary>The policy while the game artwork is on: images from the panel and Steam's store CDN.</summary>
    public const string ContentSecurityPolicyWithSteamArtwork = PolicyStart + "img-src 'self' data: " + SteamArtworkOrigin + "; " + PolicyEnd;

    /// <summary>The policy while it is off or not read yet: images from the panel only.</summary>
    public const string ContentSecurityPolicyWithoutSteamArtwork = PolicyStart + "img-src 'self' data:; " + PolicyEnd;

    private const string PolicyStart = "default-src 'self'; script-src 'self'; style-src 'self'; style-src-elem 'self' 'unsafe-inline'; ";

    private const string PolicyEnd =
        "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    // Set when the response starts, not before the rest of the pipeline runs: the exception handler clears the headers
    // of a response it replaces with an error, and errors need them too.
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        var artwork = app.ApplicationServices.GetRequiredService<SteamArtworkPolicy>();
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = artwork.SteamArtwork ? ContentSecurityPolicyWithSteamArtwork : ContentSecurityPolicyWithoutSteamArtwork;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                return Task.CompletedTask;
            });
            await next(context);
        });
    }
}
