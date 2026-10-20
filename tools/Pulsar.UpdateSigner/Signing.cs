using System.Security.Cryptography;
using Pulsar.Contracts;

namespace Pulsar.UpdateSigner;

/// <summary>
/// The maintainer's side of update signing (docs/guides/releasing.md): the key is an ECDSA P-256 key in a PKCS#8 file
/// encrypted with a passphrase (AES-256, PBKDF2-SHA256 with many iterations), which never leaves the maintainer's
/// computers; a release's manifest is signed for <see cref="UpdateRelease.SignaturePurpose"/>, as the panel and the
/// agents verify it.
/// </summary>
internal static class Signing
{
    public const int MinPassphraseLength = 12;
    private const int Iterations = 1_000_000;

    /// <summary>Makes a key and writes it, encrypted, to <paramref name="path"/> (never over an existing file); returns its public key.</summary>
    public static byte[] NewKey(string path, string passphrase)
    {
        CheckPassphrase(passphrase);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportEncryptedPkcs8PrivateKeyPem(passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Iterations));
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(pem);
        }

        return key.ExportSubjectPublicKeyInfo();
    }

    /// <summary>The key in <paramref name="path"/>; a wrong passphrase, or a file that is not an encrypted P-256 key, throws.</summary>
    public static ECDsa Open(string path, string passphrase)
    {
        var pem = File.ReadAllText(path);
        if (!pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new CryptographicException("The key file must hold an encrypted private key: an unencrypted key is refused.");
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(pem, passphrase);
            using var _ = ProtocolCrypto.ImportPublicKey(key.ExportSubjectPublicKeyInfo());
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The signature file's bytes for <paramref name="manifest"/>. A manifest that is not well formed is never signed, and,
    /// unless <paramref name="allowUntrusted"/>, neither is one by a key this build does not trust (no installation built
    /// from it would take the release).
    /// </summary>
    public static byte[] Sign(ECDsa key, byte[] manifest, bool allowUntrusted)
    {
        if (manifest.Length > UpdateRelease.MaxManifestBytes || UpdateVerifier.Read(manifest) is null)
        {
            throw new InvalidOperationException("The manifest is not a valid pulsar-update.json: nothing was signed.");
        }

        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        if (!allowUntrusted && !UpdateKeys.Trusted.Contains(publicKey, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"This key ({ProtocolCrypto.Fingerprint(key.ExportSubjectPublicKeyInfo())}) is not in UpdateKeys.Trusted: installations would refuse the release. Nothing was signed.");
        }

        var signature = ProtocolJson.Serialize(new UpdateSignature(ProtocolCrypto.Fingerprint(key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ProtocolCrypto.Sign(key, ProtocolCrypto.SignedData(UpdateRelease.SignaturePurpose, manifest)))));

        // What was written must verify as an installation verifies it.
        var check = new UpdateVerifier([publicKey]).Verify(manifest, signature);
        return check.Manifest is not null ? signature : throw new CryptographicException($"The signature does not verify: {check.Error}");
    }

    public static void CheckPassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinPassphraseLength)
        {
            throw new ArgumentException($"The passphrase needs at least {MinPassphraseLength} characters.");
        }
    }
}
