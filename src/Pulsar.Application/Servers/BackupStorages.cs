using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>Encrypts storage secret keys for the database with the panel's Data Protection key ring (docs/architecture/08).</summary>
public interface IBackupStorageSecretProtector
{
    byte[] Protect(string value);

    /// <exception cref="CryptographicException">The key ring that encrypted it is gone.</exception>
    string Unprotect(byte[] protectedValue);
}

/// <param name="SecretAccessKey">Null on an update keeps the current secret.</param>
public sealed record BackupStorageDefinition(
    string Name,
    string? Endpoint,
    string Region,
    string Bucket,
    string? Prefix,
    string AccessKeyId,
    string? SecretAccessKey,
    bool PathStyle);

/// <param name="Servers">Servers that copy their new backups here.</param>
/// <param name="Copies">Backups with a copy here.</param>
public sealed record BackupStorageView(
    Guid Id,
    string Name,
    string? Endpoint,
    string Region,
    string Bucket,
    string Prefix,
    string AccessKeyId,
    bool PathStyle,
    int Servers,
    int Copies,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BackupStorageList(IReadOnlyList<BackupStorageView> Storages);

/// <summary>
/// S3-compatible storages for backups (Phase 10), for holders of <c>settings.manage</c>. The secret access key goes in
/// encrypted and never comes back out; nodes get it only inside the backup commands that need it.
/// </summary>
public sealed class BackupStorages(IPulsarDbContext db, IBackupStorageSecretProtector secrets, IRowLocks locks, IAuditLog audit, TimeProvider clock)
{
    public async Task<BackupStorageList> ListAsync(CancellationToken cancellationToken)
    {
        var storages = await db.BackupStorages.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken);
        var servers = await db.Servers.AsNoTracking().Where(s => s.BackupStorageId != null)
            .GroupBy(s => s.BackupStorageId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var copies = await db.Backups.AsNoTracking().Where(b => b.StorageId != null)
            .GroupBy(b => b.StorageId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        return new BackupStorageList([.. storages.Select(s => View(s,
            servers.FirstOrDefault(c => c.Id == s.Id)?.Count ?? 0, copies.FirstOrDefault(c => c.Id == s.Id)?.Count ?? 0))]);
    }

    public async Task<Result<BackupStorageView>> CreateAsync(BackupStorageDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // Counted with creations taking turns: requests made at the same moment cannot all take the last place.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockCreationsAsync<BackupStorage>(cancellationToken);
        if (await db.BackupStorages.CountAsync(cancellationToken) >= BackupStorage.MaxStorages)
        {
            return BackupStorageErrors.TooMany;
        }

        var created = BackupStorage.Create(definition.Name, definition.Endpoint, definition.Region, definition.Bucket, definition.Prefix,
            definition.AccessKeyId, Secret(definition), definition.PathStyle, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var storage = created.Value;
        db.BackupStorages.Add(storage);
        audit.Record(AuditActions.BackupStorage.Created, metadata: Metadata(storage, secretChanged: true)).WithTarget("backup_storage", storage.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return View(storage, 0, 0);
    }

    public async Task<Result<BackupStorageView>> UpdateAsync(Guid id, BackupStorageDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var storage = await db.BackupStorages.SingleOrDefaultAsync(s => s.Id == new BackupStorageId(id), cancellationToken);
        if (storage is null)
        {
            return BackupStorageErrors.NotFound;
        }

        // Backups keep only the key of their copy: the endpoint and bucket stay while there are any.
        var secret = Secret(definition);
        var holdsCopies = await db.Backups.AnyAsync(b => b.StorageId == storage.Id, cancellationToken);
        var changed = storage.Change(definition.Name, definition.Endpoint, definition.Region, definition.Bucket, definition.Prefix,
            definition.AccessKeyId, secret, definition.PathStyle, clock.GetUtcNow(), holdsCopies);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        audit.Record(AuditActions.BackupStorage.Updated, metadata: Metadata(storage, secret is not null)).WithTarget("backup_storage", storage.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return View(storage, await db.Servers.CountAsync(s => s.BackupStorageId == storage.Id, cancellationToken),
            await db.Backups.CountAsync(b => b.StorageId == storage.Id, cancellationToken));
    }

    /// <summary>Only a storage no server uses and no backup has a copy in (its copies would be lost to Pulsar).</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var storage = await db.BackupStorages.SingleOrDefaultAsync(s => s.Id == new BackupStorageId(id), cancellationToken);
        if (storage is null)
        {
            return BackupStorageErrors.NotFound;
        }

        if (await db.Servers.AnyAsync(s => s.BackupStorageId == storage.Id, cancellationToken)
            || await db.Backups.AnyAsync(b => b.StorageId == storage.Id, cancellationToken))
        {
            return BackupStorageErrors.InUse;
        }

        db.BackupStorages.Remove(storage);
        audit.Record(AuditActions.BackupStorage.Deleted, metadata: new { storage.Name, storage.Bucket }).WithTarget("backup_storage", storage.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>What a node needs to reach <paramref name="key"/> in the storage; null when the storage is gone or its secret unreadable.</summary>
    public async Task<RemoteBackupTarget?> TargetAsync(BackupStorageId? id, string? key, CancellationToken cancellationToken)
    {
        if (id is null || key is null)
        {
            return null;
        }

        var storage = await db.BackupStorages.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return storage is null ? null : Target(storage, key);
    }

    /// <summary>The target for a new backup's copy; null when the secret cannot be read with this panel's keys.</summary>
    public RemoteBackupTarget? Target(BackupStorage storage, string key)
    {
        ArgumentNullException.ThrowIfNull(storage);
        try
        {
            return new RemoteBackupTarget(storage.Endpoint, storage.Region, storage.Bucket, key, storage.AccessKeyId,
                secrets.Unprotect(storage.SecretProtected), storage.PathStyle);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private byte[]? Secret(BackupStorageDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.SecretAccessKey) ? null : secrets.Protect(definition.SecretAccessKey.Trim());

    private static object Metadata(BackupStorage storage, bool secretChanged) =>
        new { storage.Name, storage.Endpoint, storage.Region, storage.Bucket, storage.Prefix, storage.AccessKeyId, secretChanged };

    private static BackupStorageView View(BackupStorage storage, int servers, int copies) => new(storage.Id.Value, storage.Name, storage.Endpoint,
        storage.Region, storage.Bucket, storage.Prefix, storage.AccessKeyId, storage.PathStyle, servers, copies, storage.CreatedAt, storage.UpdatedAt);
}
