using System.Security.Cryptography;
using Pulsar.Contracts;
using Pulsar.UpdateSigner;

namespace Pulsar.Contracts.Tests;

/// <summary>The maintainer's signing tool: an encrypted key, and signatures installations verify.</summary>
public sealed class UpdateSigningTests : IDisposable
{
    private const string Passphrase = "a passphrase long enough";
    private readonly string _folder = Directory.CreateTempSubdirectory("pulsar-signer-tests-").FullName;

    [Fact]
    public void AKeyIsWrittenEncryptedAndOpensOnlyWithItsPassphrase()
    {
        var path = Path.Combine(_folder, "update.key");
        var publicKey = Signing.NewKey(path, Passphrase);

        Assert.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", File.ReadAllText(path), StringComparison.Ordinal);
        using (var opened = Signing.Open(path, Passphrase))
        {
            Assert.Equal(publicKey, opened.ExportSubjectPublicKeyInfo());
        }

        Assert.ThrowsAny<CryptographicException>(() => Signing.Open(path, "not the passphrase at all"));
        Assert.Throws<IOException>(() => Signing.NewKey(path, Passphrase));
        Assert.Throws<ArgumentException>(() => Signing.NewKey(Path.Combine(_folder, "short.key"), "too short"));

        // A key that is not encrypted is refused, so that it never sits on a disk in the clear.
        using var plain = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plainPath = Path.Combine(_folder, "plain.key");
        File.WriteAllText(plainPath, plain.ExportPkcs8PrivateKeyPem());
        Assert.Throws<CryptographicException>(() => Signing.Open(plainPath, Passphrase));
    }

    [Fact]
    public void ASignatureVerifiesAsInstallationsCheckIt()
    {
        var path = Path.Combine(_folder, "update.key");
        var publicKey = Convert.ToBase64String(Signing.NewKey(path, Passphrase));
        using var key = Signing.Open(path, Passphrase);
        var manifest = ProtocolJson.Serialize(new UpdateManifest("pulsar", "1.0.1", "1.0.0", DateTimeOffset.UtcNow, null,
            [new UpdateFile("pulsar-1.0.1-windows-x64.zip", 10, new string('a', 64))]));

        var signature = Signing.Sign(key, manifest, allowUntrusted: true);

        Assert.Equal("1.0.1", new UpdateVerifier([publicKey]).Verify(manifest, signature).Manifest?.Version);
        // A key this build does not trust signs nothing unless told to (tests), and a malformed manifest never.
        if (!UpdateKeys.Trusted.Contains(publicKey))
        {
            Assert.Throws<InvalidOperationException>(() => Signing.Sign(key, manifest, allowUntrusted: false));
        }

        Assert.Throws<InvalidOperationException>(() => Signing.Sign(key, "{}"u8.ToArray(), allowUntrusted: true));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
