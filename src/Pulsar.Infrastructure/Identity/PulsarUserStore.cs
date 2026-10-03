using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Infrastructure.Identity;

/// <summary>
/// Identity's store with the two-factor secrets protected (docs/architecture/08, "Secrets"): the authenticator key is
/// encrypted with Data Protection, and recovery codes are kept as SHA-256 hashes (Identity keeps both in clear).
/// </summary>
public sealed class PulsarUserStore(PulsarDbContext context, IDataProtectionProvider dataProtection, IdentityErrorDescriber? describer = null)
    : UserOnlyStore<PulsarUser, PulsarDbContext, UserId>(context, describer)
{
    public const string LoginProvider = "[Pulsar]";
    public const string AuthenticatorKeyName = "AuthenticatorKey";
    public const string RecoveryCodesName = "RecoveryCodes";
    public const string LastStepName = "AuthenticatorLastStep";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Pulsar.Identity.AuthenticatorKey.v1");

    public override Task SetAuthenticatorKeyAsync(PulsarUser user, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SetTokenAsync(user, LoginProvider, AuthenticatorKeyName, _protector.Protect(key), cancellationToken);
    }

    public override async Task<string?> GetAuthenticatorKeyAsync(PulsarUser user, CancellationToken cancellationToken)
    {
        var stored = await GetTokenAsync(user, LoginProvider, AuthenticatorKeyName, cancellationToken);
        return stored is null ? null : _protector.Unprotect(stored);
    }

    public override Task ReplaceCodesAsync(PulsarUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken) =>
        SetTokenAsync(user, LoginProvider, RecoveryCodesName, string.Join(';', recoveryCodes.Select(Hash)), cancellationToken);

    public override async Task<bool> RedeemCodeAsync(PulsarUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        var hashes = await StoredCodesAsync(user, cancellationToken);
        var hash = Hash(code);
        var match = hashes.FirstOrDefault(h => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(hash)));
        if (match is null)
        {
            return false;
        }

        hashes.Remove(match);
        await SetTokenAsync(user, LoginProvider, RecoveryCodesName, string.Join(';', hashes), cancellationToken);
        return true;
    }

    public override async Task<int> CountCodesAsync(PulsarUser user, CancellationToken cancellationToken) =>
        (await StoredCodesAsync(user, cancellationToken)).Count;

    private async Task<List<string>> StoredCodesAsync(PulsarUser user, CancellationToken cancellationToken)
    {
        var stored = await GetTokenAsync(user, LoginProvider, RecoveryCodesName, cancellationToken);
        return string.IsNullOrEmpty(stored) ? [] : [.. stored.Split(';', StringSplitOptions.RemoveEmptyEntries)];
    }

    // Codes are XXXXX-XXXXX from a 26-letter alphabet (about 47 random bits), typed in any case, with or without the dash.
    private static string Hash(string code)
    {
        var normalized = code.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length == 10 && !normalized.Contains('-', StringComparison.Ordinal))
        {
            normalized = $"{normalized[..5]}-{normalized[5..]}";
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}

/// <summary>Claims added to every session: whether the user signs in with two-factor authentication.</summary>
public sealed class PulsarClaimsPrincipalFactory(UserManager<PulsarUser> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<PulsarUser>(userManager, options)
{
    public const string TwoFactorClaim = "pulsar:2fa";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(PulsarUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = await base.GenerateClaimsAsync(user);
        if (user.TwoFactorEnabled)
        {
            identity.AddClaim(new Claim(TwoFactorClaim, "true"));
        }

        return identity;
    }
}
