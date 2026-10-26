using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>Encrypts the CurseForge API key for the database with the panel's Data Protection key ring.</summary>
public interface ICatalogKeyProtector
{
    byte[] Protect(string value);

    /// <exception cref="CryptographicException">The key ring that encrypted it is gone.</exception>
    string Unprotect(byte[] protectedValue);
}

/// <summary>Asks CurseForge whether a key works, before the panel keeps it.</summary>
public interface ICurseForgeKeyCheck
{
    /// <returns>False when CurseForge refuses it.</returns>
    /// <exception cref="ModCatalogUnavailableException">CurseForge could not be asked.</exception>
    Task<bool> AcceptsAsync(string key, CancellationToken cancellationToken);
}

/// <param name="CurseForgeConfigured">The panel has a CurseForge key (its value is never shown again).</param>
public sealed record CatalogSettingsView(bool CurseForgeConfigured, DateTimeOffset? UpdatedAt);

/// <summary>
/// Mod catalog settings for holders of <c>settings.manage</c> (Pulsar 1.0.9): the administrator's CurseForge API key,
/// checked with CurseForge before it is kept, encrypted, write-only; changes are audited without the key.
/// </summary>
public sealed class CatalogSettings(IPulsarDbContext db, ICatalogKeyProtector protector, ICurseForgeKeyCheck curseForge, IAuditLog audit, TimeProvider clock)
{
    /// <summary>CurseForge's keys are a few dozen characters: anything far longer is not one.</summary>
    public const int MaxKeyLength = 256;

    public async Task<CatalogSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ModCatalogSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return new CatalogSettingsView(settings?.CurseForgeKey is not null, settings?.UpdatedAt);
    }

    public async Task<Result<CatalogSettingsView>> SetCurseForgeKeyAsync(string? key, CancellationToken cancellationToken)
    {
        var value = key?.Trim();
        if (value is not { Length: > 0 and <= MaxKeyLength } || !value.All(c => c is > ' ' and <= '~'))
        {
            return ModErrors.CurseForgeKeyInvalid;
        }

        try
        {
            if (!await curseForge.AcceptsAsync(value, cancellationToken))
            {
                return ModErrors.CurseForgeKeyRejected;
            }
        }
        catch (ModCatalogUnavailableException ex)
        {
            return ex.Busy ? ModErrors.CatalogBusy : ModErrors.CatalogUnavailable(ex.Message);
        }

        await SaveAsync(protector.Protect(value), AuditActions.ModCatalogs.CurseForgeKeySet, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task<CatalogSettingsView> RemoveCurseForgeKeyAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(null, AuditActions.ModCatalogs.CurseForgeKeyRemoved, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    private async Task SaveAsync(byte[]? protectedKey, string action, CancellationToken cancellationToken)
    {
        // The row is made on the first save: two first saves at once, and the second goes again on the row the first made.
        for (var attempt = 0; ; attempt++)
        {
            var now = clock.GetUtcNow();
            var settings = await db.ModCatalogSettings.SingleOrDefaultAsync(cancellationToken);
            if (settings is null)
            {
                settings = ModCatalogSettings.Default(now);
                db.ModCatalogSettings.Add(settings);
            }

            settings.SetCurseForgeKey(protectedKey, now);
            audit.Record(action);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
