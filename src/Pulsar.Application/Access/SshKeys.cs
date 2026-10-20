using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Access;

/// <summary>An SSH key as its owner sees it.</summary>
/// <param name="Type">The key's type: <c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256</c> (and 384, 521) or <c>ssh-rsa</c>.</param>
/// <param name="Bits">Its size: 256 for Ed25519, the curve's for ECDSA, the modulus's for RSA.</param>
/// <param name="Fingerprint">Its SHA-256 fingerprint as OpenSSH prints it (<c>SHA256:…</c>).</param>
public sealed record SshKeyView(Guid Id, string Name, string Type, int Bits, string Fingerprint, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// SSH keys for SFTP (docs/guides/sftp.md, "SSH keys"): each user adds and deletes their own from a signed-in session,
/// once the API has confirmed their password (and two-factor code); administrators can delete all of a user's. Ending
/// the SFTP sessions a deleted key opened is the API's part.
/// </summary>
public sealed class SshKeys(IPulsarDbContext db, IRowLocks locks, IAuditLog audit, TimeProvider clock)
{
    /// <summary>The SQLSTATE of a unique index violation (PostgreSQL's, the standard one).</summary>
    private const string UniqueViolation = "23505";

    /// <summary>The user's keys, newest first.</summary>
    public async Task<IReadOnlyList<SshKeyView>> ListAsync(UserId user, CancellationToken cancellationToken)
    {
        var keys = await db.SshKeys.AsNoTracking()
            .Where(k => k.UserId == user)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. keys.Select(View)];
    }

    /// <summary>
    /// Whether <paramref name="key"/> could be added now (its name, not one of the user's yet, room for one more):
    /// answered before the password and code are checked, so a mistake costs neither a password attempt nor the code.
    /// Whether another account has it is said only after them (<see cref="AddAsync"/>).
    /// </summary>
    public async Task<Result> ValidateAsync(UserId user, string? name, SshPublicKey key, CancellationToken cancellationToken)
    {
        var created = SshKey.Create(user, name, key, clock.GetUtcNow());
        return created.IsSuccess ? await RoomForAsync(user, key, anyAccount: false, cancellationToken) : created.Error!;
    }

    /// <summary>
    /// Counted with the user's row locked: requests made at the same moment cannot all take the last place. A key on
    /// another account is refused (the unique index too, for one added at the same moment).
    /// </summary>
    public async Task<Result<SshKeyView>> AddAsync(UserId user, string? name, SshPublicKey key, CancellationToken cancellationToken)
    {
        var created = SshKey.Create(user, name, key, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockUserAsync(user, cancellationToken);
        if (await RoomForAsync(user, key, anyAccount: true, cancellationToken) is { IsSuccess: false } refused)
        {
            return refused.Error!;
        }

        var added = created.Value;
        db.SshKeys.Add(added);
        audit.Record(AuditActions.Auth.SshKeyAdded, metadata: new { key = added.Id.Value, added.Name, added.Type, key.Bits, added.Fingerprint })
            .WithTarget("user", user.Value.ToString());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is DbException { SqlState: UniqueViolation })
        {
            // Another user added the same key at the same moment (the users' locks are not the same lock).
            return AccessErrors.SshKeyDuplicate;
        }

        await transaction.CommitAsync(cancellationToken);
        return View(added);
    }

    public async Task<Result> DeleteAsync(UserId user, SshKeyId id, CancellationToken cancellationToken)
    {
        // Deleted by a statement, so two deletions at the same moment find it once (the other one: not found).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var key = await db.SshKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id && k.UserId == user, cancellationToken);
        if (key is null || await db.SshKeys.Where(k => k.Id == id && k.UserId == user).ExecuteDeleteAsync(cancellationToken) == 0)
        {
            return AccessErrors.SshKeyNotFound;
        }

        audit.Record(AuditActions.Auth.SshKeyDeleted, metadata: new { key = key.Id.Value, key.Name, key.Type, key.Fingerprint })
            .WithTarget("user", user.Value.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Deletes every key of <paramref name="user"/> (an administrator's response to a leak); returns how many.</summary>
    public async Task<int> DeleteAllAsync(UserId user, CancellationToken cancellationToken)
    {
        // With the user's row locked, as adding takes it: a key added meanwhile is in the entry or stays.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockUserAsync(user, cancellationToken);
        var fingerprints = await db.SshKeys.Where(k => k.UserId == user).Select(k => k.Fingerprint).ToListAsync(cancellationToken);
        var count = await db.SshKeys.Where(k => k.UserId == user).ExecuteDeleteAsync(cancellationToken);
        audit.Record(AuditActions.User.SshKeysDeleted, metadata: new { user = user.Value, count, fingerprints })
            .WithTarget("user", user.Value.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    /// <summary>How many keys each of <paramref name="users"/> has (for the administration pages).</summary>
    public async Task<IReadOnlyDictionary<UserId, int>> CountAsync(IReadOnlyCollection<UserId> users, CancellationToken cancellationToken) =>
        await db.SshKeys.AsNoTracking()
            .Where(k => users.Contains(k.UserId))
            .GroupBy(k => k.UserId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

    /// <summary>Whether the key is still the user's (an SFTP session it opened ends when it is not).</summary>
    public Task<bool> ExistsAsync(UserId user, SshKeyId id, CancellationToken cancellationToken) =>
        db.SshKeys.AsNoTracking().AnyAsync(k => k.Id == id && k.UserId == user, cancellationToken);

    /// <summary>The key signed its user in now.</summary>
    public async Task RecordUseAsync(SshKeyId id, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await db.SshKeys.Where(k => k.Id == id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
    }

    public static SshKeyView View(SshKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // The size is read again from the blob, which was checked when the key was added.
        var bits = SshPublicKey.FromBlob(key.PublicKey) is { IsSuccess: true } parsed ? parsed.Value.Bits : 0;
        return new SshKeyView(key.Id.Value, key.Name, key.Type, bits, key.Fingerprint, key.CreatedAt, key.LastUsedAt);
    }

    /// <summary>
    /// Room for <paramref name="key"/>: not one of the user's already, fewer than <see cref="SshKey.MaxPerUser"/>, and,
    /// with <paramref name="anyAccount"/> (only once the password and code were given), on no other account either. A key
    /// belongs to one account: someone who added another person's public key (public by nature) to their own account
    /// would have that person's client sign in there, to their files, without a prompt. "Added already" then tells only
    /// that the key is on the panel, which a sign-in's question about the key tells too (docs/architecture/08, T42).
    /// </summary>
    private async Task<Result> RoomForAsync(UserId user, SshPublicKey key, bool anyAccount, CancellationToken cancellationToken)
    {
        var fingerprints = await db.SshKeys.Where(k => k.UserId == user).Select(k => k.Fingerprint).ToListAsync(cancellationToken);
        if (fingerprints.Contains(key.Fingerprint, StringComparer.Ordinal)
            || (anyAccount && await db.SshKeys.AnyAsync(k => k.Fingerprint == key.Fingerprint, cancellationToken)))
        {
            return AccessErrors.SshKeyDuplicate;
        }

        return fingerprints.Count >= SshKey.MaxPerUser ? AccessErrors.SshKeyTooMany : Result.Success();
    }
}
