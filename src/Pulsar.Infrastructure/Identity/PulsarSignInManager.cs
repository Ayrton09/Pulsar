using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
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
