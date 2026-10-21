using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Pulsar.Api.Auth;

/// <summary>
/// How long a browser session lasts at most, however much it is used (docs/architecture/08, "Users and web"): the
/// cookie slides while it is used, so a copied one (a shared PC, malware) would otherwise last as long as it keeps being
/// used. The time of the sign-in is kept with the session, and survives the cookie's renewals.
/// </summary>
public static class SessionLifetime
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private const string SignedInAtKey = "pulsar.signed-in-at";

    /// <summary>A sign-in starts the clock; renewing a session (a password changed in it) keeps the first sign-in's time.</summary>
    public static Task OnSigningIn(CookieSigningInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Properties.Items.ContainsKey(SignedInAtKey))
        {
            context.Properties.Items[SignedInAtKey] = Clock(context.HttpContext).GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        }

        return Task.CompletedTask;
    }

    /// <summary>A session past its maximum age (or without its sign-in's time) ends; the others go on to the security stamp's check.</summary>
    public static async Task OnValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Properties.Items.TryGetValue(SignedInAtKey, out var value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var signedInAt)
            || Clock(context.HttpContext).GetUtcNow() - signedInAt >= MaxAge)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }

        await SecurityStampValidator.ValidatePrincipalAsync(context);
    }

    private static TimeProvider Clock(HttpContext context) => context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
}
