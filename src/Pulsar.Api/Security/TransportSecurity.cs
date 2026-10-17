using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Pulsar.Api.Http;

namespace Pulsar.Api.Security;

/// <summary>Configuration section <c>Security</c>: how the panel is reached by browsers.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Serve the UI and API over plain HTTP. Off by default: passwords and session cookies would travel unencrypted.
    /// Only for development or a trusted network; the panel logs a warning and the UI shows a banner.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Reverse proxies (IP addresses) whose X-Forwarded-For/Proto headers are trusted, so a TLS-terminating proxy is
    /// recognized as HTTPS. Loopback is always trusted.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; set; } = [];
}

/// <summary>Cookie names: the <c>__Host-</c> prefix (bound to this host, Secure, path /) is only possible over HTTPS.</summary>
public sealed class PanelCookies(bool secure)
{
    public string Session { get; } = secure ? "__Host-pulsar-session" : "pulsar-session";

    /// <summary>Who gave the right password and still has to give the second factor.</summary>
    public string TwoFactor { get; } = secure ? "__Host-pulsar-2fa" : "pulsar-2fa";

    public string AntiforgeryCookieToken { get; } = secure ? "__Host-pulsar-af" : "pulsar-af";

    /// <summary>Readable by JavaScript on purpose (it only holds the antiforgery request token).</summary>
    public string AntiforgeryRequestToken { get; } = secure ? "__Host-pulsar-xsrf" : "pulsar-xsrf";

    public CookieSecurePolicy SecurePolicy { get; } = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
}

internal static partial class TransportSecurity
{
    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, SecurityOptions security)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        foreach (var proxy in security.TrustedProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }
    }

    /// <summary>
    /// Adds HTTPS enforcement unless plain HTTP was explicitly allowed. API calls that still arrive over HTTP get a
    /// clear problem instead of failing deeper (antiforgery refuses Secure cookies on HTTP); so do downloads, since a
    /// program fetched over plain HTTP could be swapped on the way.
    /// </summary>
    public static void UseTransportSecurity(this WebApplication app, SecurityOptions security)
    {
        app.UseForwardedHeaders();
        if (security.AllowInsecureHttp)
        {
            LogInsecureHttp(app.Logger);
            return;
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps && (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.StartsWithSegments("/agent", StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.StartsWithSegments("/downloads", StringComparison.OrdinalIgnoreCase)))
            {
                await ApiProblems.Create(StatusCodes.Status400BadRequest, "transport.https_required",
                    "This panel only accepts HTTPS.",
                    "Open the panel with https://, or set Security:AllowInsecureHttp to true on a trusted network.").ExecuteAsync(context);
                return;
            }

            await next(context);
        });
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Plain HTTP is allowed (Security:AllowInsecureHttp): passwords and session cookies travel unencrypted. " +
            "Use it only for development or on a trusted network, or put the panel behind a TLS proxy.")]
    private static partial void LogInsecureHttp(ILogger logger);
}
