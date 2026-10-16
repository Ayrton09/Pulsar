using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Auth;

/// <summary>
/// One credential check at a time per account (docs/architecture/08, "Users and web"). The browser's sign-in steps and
/// SFTP's run the password or code check, the failed-count update and the audit entry under the account's lock: the
/// user row has a concurrency stamp, so of two parallel updates only one is saved, and parallel guesses would otherwise
/// count once and lose their audit entries. The lock is in the process (the panel is one process). It is keyed by the
/// normalized user name, names of no user included, so parallel attempts wait the same whether the user exists or not.
/// </summary>
public sealed class SignInLocks
{
    private const int UpdateAttempts = 3;

    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Waits for the account's turn; disposing the result lets the next check in.</summary>
    public async Task<IDisposable> AcquireAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedUserName);
        Gate gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(normalizedUserName, out gate!))
            {
                gate = new Gate();
                _gates.Add(normalizedUserName, gate);
            }

            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(normalizedUserName, gate);
            throw;
        }

        return new Turn(this, normalizedUserName, gate);
    }

    /// <summary>
    /// Counts a failed check towards the lockout. When something other than a sign-in saved the user meanwhile, the update
    /// is refused (concurrency stamp): it is counted again on the row as it is now.
    /// </summary>
    public static async Task CountFailureAsync(UserManager<PulsarUser> users, PulsarDbContext db, PulsarUser user)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(db);
        for (var attempt = 1; attempt <= UpdateAttempts; attempt++)
        {
            if ((await users.AccessFailedAsync(user)).Succeeded)
            {
                return;
            }

            await db.Entry(user).ReloadAsync();
        }
    }

    /// <summary>Saves <paramref name="change"/> to the user, applied again on the row as it is now when the update is refused.</summary>
    public static async Task UpdateAsync(UserManager<PulsarUser> users, PulsarDbContext db, PulsarUser user, Action<PulsarUser> change)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(change);
        for (var attempt = 1; attempt <= UpdateAttempts; attempt++)
        {
            change(user);
            if ((await users.UpdateAsync(user)).Succeeded)
            {
                return;
            }

            await db.Entry(user).ReloadAsync();
        }
    }

    /// <summary>
    /// Drops what a refused update left unsaved in <paramref name="db"/> (Identity's own updates within a sign-in), so that
    /// the audit entry saved next is not refused with it.
    /// </summary>
    public static async Task DiscardUnsavedAsync(PulsarDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
            else
            {
                await entry.ReloadAsync();
            }
        }
    }

    private void Leave(string normalizedUserName, Gate gate)
    {
        lock (_lock)
        {
            if (--gate.Users == 0)
            {
                // Nobody holds or waits for it: whoever comes next makes a new one.
                _gates.Remove(normalizedUserName);
                gate.Dispose();
            }
        }
    }

    private sealed class Gate : IDisposable
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }

        public void Dispose() => Semaphore.Dispose();
    }

    private sealed class Turn(SignInLocks locks, string normalizedUserName, Gate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Semaphore.Release();
                locks.Leave(normalizedUserName, gate);
            }
        }
    }
}
