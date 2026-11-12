using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Access;

/// <summary>A key as its owner sees it: never its value, which is shown once at creation.</summary>
/// <param name="Permissions">The only permissions it may use; null: all of its user's.</param>
public sealed record ApiKeyView(Guid Id, string Name, string Prefix, IReadOnlyList<string>? Permissions, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt);

/// <summary>A new key and its value, shown this once.</summary>
public sealed record CreatedApiKey(ApiKeyView Key, string Value);

/// <summary>A key that authenticated a request: whose it is and what it is limited to.</summary>
public sealed record ApiKeyMatch(ApiKeyId Id, UserId UserId, string Name, IReadOnlyList<string>? Permissions);

/// <summary>
/// API keys (docs/architecture/02, "API keys"): each user manages their own from a signed-in session (never with a
/// key); administrators can revoke all of a user's keys. Requests with a key are matched here.
/// </summary>
public sealed class ApiKeys(IPulsarDbContext db, IRowLocks locks, IAuditLog audit, TimeProvider clock, FileTransfers transfers)
{
    private static readonly TimeSpan s_lastUsedPrecision = TimeSpan.FromMinutes(1);

    /// <summary>The user's keys that still work, newest first (revoked ones are gone; expired ones stay until revoked).</summary>
    public async Task<IReadOnlyList<ApiKeyView>> ListAsync(UserId user, CancellationToken cancellationToken)
    {
        var keys = await db.ApiKeys.AsNoTracking()
            .Where(k => k.UserId == user && k.RevokedAt == null)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. keys.Select(View)];
    }

    /// <summary>
    /// Whether a key like this could be made now (its name, its permissions, the user's number of keys), before the
    /// password is checked: a mistake in the request costs no password attempt. <see cref="CreateAsync"/> checks again.
    /// </summary>
    public async Task<Result> ValidateAsync(UserId user, string? name, IReadOnlyCollection<string>? permissions, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        if (await db.ApiKeys.CountAsync(k => k.UserId == user && k.RevokedAt == null, cancellationToken) >= ApiKey.MaxPerUser)
        {
            return AccessErrors.ApiKeyTooMany;
        }

        var made = ApiKey.Create(user, name, permissions, expiresAt, clock.GetUtcNow());
        return made.IsSuccess ? Result.Success() : made.Error!;
    }

    /// <summary>Checks the credential proof and count with the user's row locked, before inserting the key.</summary>
    /// <param name="credentialsStillValid">Reads current persisted credential state without taking a sign-in lock.</param>
    public async Task<Result<CreatedApiKey>> CreateAsync(UserId user, string? name, IReadOnlyCollection<string>? permissions, DateTimeOffset? expiresAt,
        Func<CancellationToken, Task<bool>> credentialsStillValid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialsStillValid);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockUserAsync(user, cancellationToken);
        if (!await credentialsStillValid(cancellationToken))
        {
            return AccessErrors.CredentialsChanged;
        }

        if (await db.ApiKeys.CountAsync(k => k.UserId == user && k.RevokedAt == null, cancellationToken) >= ApiKey.MaxPerUser)
        {
            return AccessErrors.ApiKeyTooMany;
        }

        var created = ApiKey.Create(user, name, permissions, expiresAt, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var (key, value) = created.Value;
        db.ApiKeys.Add(key);
        audit.Record(AuditActions.Auth.ApiKeyCreated, metadata: new { key = key.Id.Value, key.Name, key.Prefix, key.Permissions, key.ExpiresAt });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedApiKey(View(key), value);
    }

    public async Task<Result> RevokeAsync(UserId user, ApiKeyId id, CancellationToken cancellationToken)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id && k.UserId == user && k.RevokedAt == null, cancellationToken);
        if (key is null)
        {
            return AccessErrors.ApiKeyNotFound;
        }

        key.Revoke(clock.GetUtcNow());
        audit.Record(AuditActions.Auth.ApiKeyRevoked, metadata: new { key = key.Id.Value, key.Name, key.Prefix });
        await db.SaveChangesAsync(cancellationToken);
        // A transfer outlives the requests that check the key: it ends with it.
        transfers.EndApiKey(key.Id.Value);
        return Result.Success();
    }

    /// <summary>
    /// Revokes every key of <paramref name="user"/> (an administrator's response to a leak); returns how many.
    /// </summary>
    /// <param name="because">
    /// Revoked with a change of the user's credentials (1.0.36: a password changed or reset, two-factor reset), recorded
    /// with the entry; a user without keys then leaves none.
    /// </param>
    public async Task<int> RevokeAllAsync(UserId user, CancellationToken cancellationToken, string? because = null)
    {
        var keys = await db.ApiKeys.Where(k => k.UserId == user && k.RevokedAt == null).ToListAsync(cancellationToken);
        if (keys.Count == 0 && because is not null)
        {
            return 0;
        }

        var now = clock.GetUtcNow();
        foreach (var key in keys)
        {
            key.Revoke(now);
        }

        object metadata = because is null ? new { user = user.Value, count = keys.Count } : new { user = user.Value, count = keys.Count, because };
        audit.Record(AuditActions.User.ApiKeysRevoked, metadata: metadata).WithTarget("user", user.Value.ToString());
        await db.SaveChangesAsync(cancellationToken);
        transfers.EndApiKeys(user);
        return keys.Count;
    }

    /// <summary>How many working keys each of <paramref name="users"/> has (for the administration pages).</summary>
    public async Task<IReadOnlyDictionary<UserId, int>> CountAsync(IReadOnlyCollection<UserId> users, CancellationToken cancellationToken) =>
        await db.ApiKeys.AsNoTracking()
            .Where(k => users.Contains(k.UserId) && k.RevokedAt == null)
            .GroupBy(k => k.UserId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

    /// <summary>
    /// The key a bearer value belongs to, if it works now (not revoked, not expired); its last use is recorded at most
    /// once a minute. Whether its user may still sign in is the caller's to check.
    /// </summary>
    public async Task<ApiKeyMatch?> MatchAsync(string value, CancellationToken cancellationToken)
    {
        if (!ApiKey.LooksLikeKey(value))
        {
            return null;
        }

        var hash = ApiKey.Hash(value);
        var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.TokenHash == hash, cancellationToken);
        var now = clock.GetUtcNow();
        if (key is null || !key.IsUsable(now))
        {
            return null;
        }

        if (key.LastUsedAt is not { } last || now - last >= s_lastUsedPrecision)
        {
            var id = key.Id;
            await db.ApiKeys.Where(k => k.Id == id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }

        return new ApiKeyMatch(key.Id, key.UserId, key.Name, key.Permissions);
    }

    private static ApiKeyView View(ApiKey key) =>
        new(key.Id.Value, key.Name, key.Prefix, key.Permissions, key.CreatedAt, key.ExpiresAt, key.LastUsedAt);
}
