using Microsoft.AspNetCore.Identity;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Auth;

/// <summary>
/// A new security stamp is what ends a user's sessions (each one's stamp is checked against the stored one), so it counts
/// only once it is stored. A change of the user saved meanwhile (a failed sign-in counted, an administrator's edit) makes
/// the save fail on the user's concurrency stamp: the stored user is read again and the stamp renewed on it, a few times.
/// </summary>
internal static class SecurityStamps
{
    public const int MaxAttempts = 3;

    /// <summary>Whether the new stamp is stored; when not, every session of the user still works.</summary>
    public static async Task<bool> RenewAsync(PulsarUser user, UserManager<PulsarUser> users, PulsarDbContext db, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var renewed = await users.UpdateSecurityStampAsync(user);
            if (renewed.Succeeded)
            {
                return true;
            }

            if (attempt == MaxAttempts || !renewed.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
            {
                return false;
            }

            await db.Entry(user).ReloadAsync(ct);
        }
    }
}
