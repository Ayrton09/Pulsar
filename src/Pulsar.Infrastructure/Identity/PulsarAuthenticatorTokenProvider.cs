using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace Pulsar.Infrastructure.Identity;

/// <summary>
/// Checks authenticator app codes. Unlike Identity's own provider, a code works once: the last accepted time step is
/// kept, and that step and earlier ones are refused afterwards (someone who sees a code cannot use it again within
/// its 90-second window). One step of clock drift either way is accepted.
/// </summary>
public sealed class PulsarAuthenticatorTokenProvider(TimeProvider clock) : IUserTwoFactorTokenProvider<PulsarUser>
{
    public const string ProviderName = "PulsarAuthenticator";

    public async Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<PulsarUser> manager, PulsarUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return !string.IsNullOrEmpty(await manager.GetAuthenticatorKeyAsync(user));
    }

    /// <summary>Codes come from the user's app; the panel never sends one.</summary>
    public Task<string> GenerateAsync(string purpose, UserManager<PulsarUser> manager, PulsarUser user) => Task.FromResult(string.Empty);

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<PulsarUser> manager, PulsarUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var code = Normalize(token);
        var key = await manager.GetAuthenticatorKeyAsync(user);
        if (code is null || string.IsNullOrEmpty(key))
        {
            return false;
        }

        var stored = await manager.GetAuthenticationTokenAsync(user, PulsarUserStore.LoginProvider, PulsarUserStore.LastStepName);
        var last = long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : long.MinValue;
        var keyBytes = Base32.Decode(key);
        var now = Totp.StepAt(clock.GetUtcNow());
        for (var step = now - 1; step <= now + 1; step++)
        {
            if (step > last && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Totp.Compute(keyBytes, step)), Encoding.ASCII.GetBytes(code)))
            {
                // Saving fails when another request saved the user meanwhile (concurrency stamp): only one of two
                // simultaneous uses of a code gets in.
                var saved = await manager.SetAuthenticationTokenAsync(user, PulsarUserStore.LoginProvider, PulsarUserStore.LastStepName,
                    step.ToString(CultureInfo.InvariantCulture));
                return saved.Succeeded;
            }
        }

        return false;
    }

    /// <summary>Six digits, ignoring the spaces and dashes apps put in the middle; null when it cannot be a code.</summary>
    public static string? Normalize(string? token)
    {
        var code = token?.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return code is { Length: 6 } && code.All(char.IsAsciiDigit) ? code : null;
    }
}
