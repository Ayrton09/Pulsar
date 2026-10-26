using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Pulsar.Api.Auth;

/// <summary>
/// How long a browser session lasts (docs/architecture/08, "Users and web"). By default the cookie lives as long as the
/// browser, and slides while it is used but never past <see cref="MaxAge"/> from its sign-in, so a copied one (a shared
/// PC, malware) does not last by being used. A session the user asked to be remembered on the device (1.0.9) is a
/// persistent cookie that slides for <see cref="RememberFor"/> and has no maximum age: it lasts until the user signs out,
/// as long as it is used. Both end with the user's security stamp (signing out, a password changed, disabled). The time
/// of the sign-in and the choice are kept with the session, and survive the cookie's renewals.
/// </summary>
public static class SessionLifetime
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>A remembered session ends after this long without being used (it renews once half of it has passed).</summary>
    public static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);

    private const string SignedInAtKey = "pulsar.signed-in-at";
    private const string RememberedKey = "pulsar.remembered";

    private static readonly object s_rememberThisSignIn = new();

    /// <summary>The sign-in this request makes (a password, or the second factor after it) is remembered on the device.</summary>
    public static void Remember(HttpContext http, bool remember)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (remember)
        {
            http.Items[s_rememberThisSignIn] = true;
        }
    }

    /// <summary>
    /// A sign-in starts the clock; renewing a session (a password changed in it) keeps the first sign-in's time, and
    /// whether it is remembered.
    /// </summary>
    public static Task OnSigningIn(CookieSigningInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = Clock(context.HttpContext).GetUtcNow();
        if (!context.Properties.Items.ContainsKey(SignedInAtKey))
        {
            context.Properties.Items[SignedInAtKey] = now.ToString("O", CultureInfo.InvariantCulture);
        }

        if (context.HttpContext.Items.ContainsKey(s_rememberThisSignIn))
        {
            context.Properties.Items[RememberedKey] = "true";
            context.Properties.ExpiresUtc = (context.Properties.IssuedUtc ?? now) + RememberFor;
        }

        // The cookie outlives the browser only when remembered (the handler gives it the ticket's expiry then).
        context.Properties.IsPersistent = IsRemembered(context.Properties);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A session past its maximum age (a remembered one has none) or without its sign-in's time ends; the others go on
    /// to the security stamp's check.
    /// </summary>
    public static async Task OnValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Properties.Items.TryGetValue(SignedInAtKey, out var value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var signedInAt)
            || (!IsRemembered(context.Properties) && Clock(context.HttpContext).GetUtcNow() - signedInAt >= MaxAge))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }

        await SecurityStampValidator.ValidatePrincipalAsync(context);
    }

    private static bool IsRemembered(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(RememberedKey, out var remembered) && remembered == "true";

    private static TimeProvider Clock(HttpContext context) => context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
}
