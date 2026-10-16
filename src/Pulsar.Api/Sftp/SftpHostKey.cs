using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Persistence;
using SshAlgorithms = Microsoft.DevTunnels.Ssh.Algorithms;

namespace Pulsar.Api.Sftp;

/// <summary>
/// The SFTP server's host key: ECDSA P-256, made once and kept in <c>sftp_settings</c> with its private key encrypted
/// by Data Protection (ADR-0011), as the panel signing key is. Losing the key ring loses it, and SFTP does not start
/// until the operator restores the key ring or clears the key; only then is a new one made, and every client warns that
/// the host key changed (docs/guides/sftp.md).
/// </summary>
public sealed partial class SftpHostKey(
    IServiceScopeFactory scopes, IDataProtectionProvider dataProtection, TimeProvider clock, ILogger<SftpHostKey> logger) : IDisposable
{
    private const string ProtectionPurpose = "Pulsar.SftpHostKey.v1";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Loaded? _loaded;

    /// <summary>The fingerprint as OpenSSH prints it (<c>SHA256:</c> and unpadded base64), once the key was loaded.</summary>
    public string? Fingerprint => Volatile.Read(ref _loaded)?.Fingerprint;

    /// <summary>The key, read from the database or made there on first use.</summary>
    internal async Task<Loaded> GetAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _loaded) is { } loaded)
        {
            return loaded;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded is null)
            {
                var read = await ReadOrCreateAsync(cancellationToken);
                Volatile.Write(ref _loaded, read);
                LogLoaded(read.Fingerprint);
            }

            return _loaded;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>OpenSSH's fingerprint of a public key blob (RFC 4253's encoding).</summary>
    public static string FingerprintOf(ReadOnlySpan<byte> publicKeyBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKeyBlob)).TrimEnd('=');

    public void Dispose()
    {
        _loaded?.Key.Dispose();
        _lock.Dispose();
    }

    private async Task<Loaded> ReadOrCreateAsync(CancellationToken cancellationToken)
    {
        var protector = dataProtection.CreateProtector(ProtectionPurpose);
        for (var attempt = 0; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            var settings = await db.SftpSettings.SingleOrDefaultAsync(cancellationToken);
            if (settings?.ProtectedHostKey is { } stored)
            {
                byte[] unprotected;
                try
                {
                    unprotected = protector.Unprotect(stored);
                }
                catch (CryptographicException ex)
                {
                    // A new key would make every client warn of a changed host key: the operator decides.
                    throw new InvalidOperationException(
                        "The SFTP host key cannot be decrypted: the Data Protection key ring is missing or different from the one that " +
                        "protected it. Restore the key ring from the panel backup, or clear the key (docs/guides/sftp.md) to make a new one.", ex);
                }

                return Import(unprotected);
            }

            using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var privateKey = created.ExportPkcs8PrivateKey();
            try
            {
                if (settings is null)
                {
                    settings = SftpSettings.Default(clock.GetUtcNow());
                    db.SftpSettings.Add(settings);
                }

                settings.SetHostKey(created.ExportSubjectPublicKeyInfo(), protector.Protect(privateKey));
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException) when (attempt < 2)
                {
                    // Another save (the settings, or a first start racing this one) went first: read what it left.
                    continue;
                }

                var loaded = Import(privateKey);
                LogCreated(loaded.Fingerprint);
                return loaded;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
    }

    private static Loaded Import(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            var pair = new SshAlgorithms.ECDsa.KeyPair(key);
            var blob = pair.GetPublicKeyBytes(pair.KeyAlgorithmName).ToArray();
            return new Loaded(key, pair, FingerprintOf(blob));
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    internal sealed record Loaded(ECDsa Key, SshAlgorithms.IKeyPair Pair, string Fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the SFTP host key {Fingerprint}")]
    private partial void LogCreated(string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "SFTP host key {Fingerprint}")]
    private partial void LogLoaded(string fingerprint);
}
