using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pulsar.Infrastructure.Identity;

/// <summary>
/// Adds the <see cref="PulsarUser.IsDisabled"/> rule: a disabled user cannot sign in, and an existing session is
/// rejected at the next security stamp validation (the cookie is revalidated every minute).
/// </summary>
public sealed class PulsarSignInManager(
    UserManager<PulsarUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<PulsarUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<PulsarUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<PulsarUser> confirmation)
    : SignInManager<PulsarUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public const string PendingSecurityStampClaim = "pulsar:pending-security-stamp";
    private static readonly object s_pendingProof = new();

    protected override async Task<SignInResult> SignInOrTwoFactorAsync(PulsarUser user, bool isPersistent,
        string? loginProvider = null, bool bypassTwoFactor = false)
    {
        ArgumentNullException.ThrowIfNull(user);
        // Bind the pending cookie to the exact user generation whose first factor was checked.
        // Re-querying here could bind an old password proof to a newly reset credential.
        Context.Items[s_pendingProof] = (user.Id.ToString(), user.SecurityStamp);
        try
        {
            return await base.SignInOrTwoFactorAsync(user, isPersistent, loginProvider, bypassTwoFactor);
        }
        finally
        {
            Context.Items.Remove(s_pendingProof);
        }
    }

    public static Task BindPendingCookieAsync(CookieSigningInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HttpContext.Items[s_pendingProof] is not ValueTuple<string, string?> proof
            || string.IsNullOrEmpty(proof.Item2)
            || context.Principal?.Identity is not ClaimsIdentity identity
            || !string.Equals(identity.FindFirst(ClaimTypes.Name)?.Value, proof.Item1, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The pending sign-in has no matching credential proof.");
        }

        identity.AddClaim(new Claim(PendingSecurityStampClaim, proof.Item2));
        return Task.CompletedTask;
    }

    public override async Task<bool> CanSignInAsync(PulsarUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return !user.IsDisabled && await base.CanSignInAsync(user);
    }

    public override async Task<PulsarUser?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
    {
        var user = await base.ValidateSecurityStampAsync(principal);
        return user is { IsDisabled: false } ? user : null;
    }
}
