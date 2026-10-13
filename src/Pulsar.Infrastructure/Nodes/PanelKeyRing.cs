using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Nodes;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Infrastructure.Nodes;

/// <summary>
/// The panel signing keys (ADR-0003): the active key, created on first use, plus any key being retired after a
/// rotation. Stored in <c>panel_signing_keys</c> with the private key encrypted by Data Protection (ADR-0011) and
/// kept in memory afterwards. Losing the Data Protection key ring makes them unreadable, which means re-enrolling every
/// node: both belong in the panel backups.
/// </summary>
internal sealed partial class PanelKeyRing(
    IServiceScopeFactory scopes, IDataProtectionProvider dataProtection, TimeProvider clock, ILogger<PanelKeyRing> logger)
    : IPanelKeyRing, IDisposable
{
    private const string ProtectionPurpose = "Pulsar.PanelSigningKey.v1";

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Loaded? _loaded;
    private long _generation;

    public async Task<IReadOnlyList<PanelKey>> GetPublicKeysAsync(CancellationToken cancellationToken) =>
        (await LoadAsync(cancellationToken)).PublicKeys;

    public async Task<SignedMessage> SignAsync(string purpose, byte[] payload, CancellationToken cancellationToken)
    {
        var active = (await LoadAsync(cancellationToken)).Keys[0];
        return ProtocolCrypto.SignMessage(active.Key, active.KeyId, purpose, payload);
    }

    public async Task<SignedMessage?> SignForAsync(
        string purpose, byte[] payload, IReadOnlyCollection<string>? trustedKeyIds, CancellationToken cancellationToken)
    {
        var keys = (await LoadAsync(cancellationToken)).Keys;
        // Active first, then retiring keys newest first: the first one the agent trusts.
        var signer = trustedKeyIds is null ? keys[0] : keys.FirstOrDefault(k => trustedKeyIds.Contains(k.KeyId));
        return signer is null ? null : ProtocolCrypto.SignMessage(signer.Key, signer.KeyId, purpose, payload);
    }

    public PanelSigningKey CreateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var privateKey = key.ExportPkcs8PrivateKey();
        try
        {
            var protector = dataProtection.CreateProtector(ProtectionPurpose);
            return new PanelSigningKey(ProtocolCrypto.Fingerprint(publicKey), publicKey, protector.Protect(privateKey), clock.GetUtcNow());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    // The previous keys are not disposed here: a signature in progress may still be using them. The generation goes up
    // first (a full fence): a load that read the keys before the change sees it and reads them again, or publishes
    // before this clears what it published.
    public void Reload()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _loaded, null);
    }

    public void Dispose()
    {
        foreach (var key in _loaded?.Keys ?? [])
        {
            key.Key.Dispose();
        }

        _loadLock.Dispose();
    }

    private async Task<Loaded> LoadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _loaded) is { } loaded)
        {
            return loaded;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _loaded) is { } current)
                {
                    return current;
                }

                var generation = Volatile.Read(ref _generation);
                var fresh = await ReadAsync(cancellationToken);
                Interlocked.Exchange(ref _loaded, fresh);
                if (Volatile.Read(ref _generation) == generation)
                {
                    return fresh;
                }

                // Reloaded while reading (a key retired or added meanwhile): what was read may be from before.
                Interlocked.CompareExchange(ref _loaded, null, fresh);
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<Loaded> ReadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var stored = await db.PanelSigningKeys.AsNoTracking()
            .Where(k => k.Status != PanelSigningKeyStatus.Retired)
            .ToListAsync(cancellationToken);
        if (!stored.Any(k => k.Status == PanelSigningKeyStatus.Active))
        {
            var created = CreateKey();
            db.PanelSigningKeys.Add(created);
            await db.SaveChangesAsync(cancellationToken);
            stored.Add(created);
            LogCreated(created.KeyId);
        }

        var protector = dataProtection.CreateProtector(ProtectionPurpose);
        var keys = stored
            .OrderBy(k => k.Status == PanelSigningKeyStatus.Active ? 0 : 1)
            .ThenByDescending(k => k.CreatedAt)
            .Select(k => new SigningKey(k.KeyId, k.PublicKey, Import(protector, k)))
            .ToList();
        return new Loaded(keys, [.. keys.Select(k => new PanelKey(k.KeyId, k.PublicKey))]);
    }

    private static ECDsa Import(IDataProtector protector, PanelSigningKey stored)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(protector.Unprotect(stored.ProtectedPrivateKey), out _);
            return key;
        }
        catch (CryptographicException ex)
        {
            key.Dispose();
            throw new InvalidOperationException(
                "The panel signing key cannot be decrypted: the Data Protection key ring is missing or different from the one " +
                "that protected it. Restore the key ring from the panel backup (re-enrolling every node is the alternative).", ex);
        }
    }

    private sealed record SigningKey(string KeyId, byte[] PublicKey, ECDsa Key);

    /// <summary><see cref="Keys"/>[0] is the active key.</summary>
    private sealed record Loaded(IReadOnlyList<SigningKey> Keys, IReadOnlyList<PanelKey> PublicKeys);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the panel signing key {KeyId}")]
    private partial void LogCreated(string keyId);
}
