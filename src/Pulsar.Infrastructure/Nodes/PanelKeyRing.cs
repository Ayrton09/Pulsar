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
/// The panel signing key (ADR-0003): created on first use, stored in <c>panel_signing_keys</c> with the private key
/// encrypted by Data Protection (ADR-0011), and kept in memory afterwards. Losing the Data Protection key ring makes
/// it unreadable, which means re-enrolling every node: both belong in the panel backups.
/// </summary>
internal sealed partial class PanelKeyRing(
    IServiceScopeFactory scopes, IDataProtectionProvider dataProtection, TimeProvider clock, ILogger<PanelKeyRing> logger)
    : IPanelKeyRing, IDisposable
{
    private const string ProtectionPurpose = "Pulsar.PanelSigningKey.v1";

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Loaded? _loaded;

    public async Task<IReadOnlyList<PanelKey>> GetPublicKeysAsync(CancellationToken cancellationToken) =>
        (await LoadAsync(cancellationToken)).PublicKeys;

    public async Task<SignedMessage> SignAsync(string purpose, byte[] payload, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        return ProtocolCrypto.SignMessage(loaded.SigningKey, loaded.KeyId, purpose, payload);
    }

    public void Dispose()
    {
        _loaded?.SigningKey.Dispose();
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
            if (_loaded is not null)
            {
                return _loaded;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            var protector = dataProtection.CreateProtector(ProtectionPurpose);

            var keys = await db.PanelSigningKeys.AsNoTracking()
                .Where(k => k.Status != PanelSigningKeyStatus.Retired)
                .OrderBy(k => k.CreatedAt)
                .ToListAsync(cancellationToken);
            var active = keys.SingleOrDefault(k => k.Status == PanelSigningKeyStatus.Active);
            if (active is null)
            {
                active = Create(protector);
                db.PanelSigningKeys.Add(active);
                await db.SaveChangesAsync(cancellationToken);
                keys.Add(active);
                LogCreated(active.KeyId);
            }

            var signingKey = ECDsa.Create();
            try
            {
                signingKey.ImportPkcs8PrivateKey(protector.Unprotect(active.ProtectedPrivateKey), out _);
            }
            catch (CryptographicException ex)
            {
                signingKey.Dispose();
                throw new InvalidOperationException(
                    "The panel signing key cannot be decrypted: the Data Protection key ring is missing or different from the one " +
                    "that protected it. Restore the key ring from the panel backup (re-enrolling every node is the alternative).", ex);
            }

            _loaded = new Loaded(active.KeyId, signingKey, [.. keys.Select(k => new PanelKey(k.KeyId, k.PublicKey))]);
            return _loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private PanelSigningKey Create(IDataProtector protector)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var privateKey = key.ExportPkcs8PrivateKey();
        try
        {
            return new PanelSigningKey(ProtocolCrypto.Fingerprint(publicKey), publicKey, protector.Protect(privateKey), clock.GetUtcNow());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private sealed record Loaded(string KeyId, ECDsa SigningKey, IReadOnlyList<PanelKey> PublicKeys);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the panel signing key {KeyId}")]
    private partial void LogCreated(string keyId);
}
