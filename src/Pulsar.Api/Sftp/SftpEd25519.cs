using Microsoft.DevTunnels.Ssh.Algorithms;
using Pulsar.Domain.Access;
using Buffer = Microsoft.DevTunnels.Ssh.Buffer;
using Ed25519 = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace Pulsar.Api.Sftp;

/// <summary>
/// Clients' <c>ssh-ed25519</c> keys (RFC 8709), which the SSH library does not know: a public key algorithm it takes in
/// its list like its own ones (as it takes <see cref="SftpAesCtr"/>), whose signatures are verified with BouncyCastle's
/// Ed25519 (RFC 8032: a canonical S, R and the key decoded, the key checked by <see cref="SshPublicKey"/>). The library
/// builds what was signed (the session id and the request) and reads the signature's name and bytes itself; this only
/// checks the 64 bytes against the 32 of the key. For clients' keys only: it never signs, and the server offers as host
/// key algorithms only those of the keys it holds (its ECDSA P-256 key), so it is never one.
/// </summary>
internal sealed class SftpEd25519() : PublicKeyAlgorithm(AlgorithmName, AlgorithmName, "SHA512")
{
    public const string AlgorithmName = SshPublicKey.Ed25519;

    private const int KeyLength = 32;
    private const int SignatureLength = 64;

    public override IKeyPair CreateKeyPair() => new KeyPair();

    public override IKeyPair GenerateKeyPair(int? keySizeInBits = null) => throw new NotSupportedException("Ed25519 is for clients' keys only.");

    public override ISigner CreateSigner(IKeyPair keyPair) => throw new NotSupportedException("Ed25519 is for clients' keys only.");

    public override IVerifier CreateVerifier(IKeyPair keyPair) =>
        new Verifier(keyPair as KeyPair ?? throw new ArgumentException("Not an Ed25519 key.", nameof(keyPair)));

    /// <summary>A client's public key, as its blob said.</summary>
    internal sealed class KeyPair : IKeyPair
    {
        private byte[]? _blob;

        public string KeyAlgorithmName => AlgorithmName;

        public bool HasPrivateKey => false;

        public string? Comment { get; set; }

        /// <summary>The key's 32 bytes; null until <see cref="SetPublicKeyBytes"/>.</summary>
        public byte[]? PublicKey { get; private set; }

        /// <summary>Only a blob <see cref="SshPublicKey"/> accepts: the type, 32 bytes of a valid point, nothing else.</summary>
        public void SetPublicKeyBytes(Buffer keyBytes)
        {
            var parsed = SshPublicKey.FromBlob(keyBytes.Span);
            if (!parsed.IsSuccess || parsed.Value.Type != AlgorithmName)
            {
                throw new ArgumentException("Not a valid Ed25519 public key.", nameof(keyBytes));
            }

            _blob = parsed.Value.Blob;
            PublicKey = _blob[^KeyLength..];
        }

        public Buffer GetPublicKeyBytes(string? algorithmName = null) =>
            Buffer.From(_blob ?? throw new InvalidOperationException("Key is not present."));

        public void Dispose()
        {
            // Nothing secret is held.
        }
    }

    private sealed class Verifier(KeyPair key) : IVerifier
    {
        public int DigestLength => SignatureLength;

        public bool Verify(Buffer data, Buffer signature) =>
            key.PublicKey is { } publicKey && signature.Count == SignatureLength && Ed25519.Verify(signature.Span, publicKey, data.Span);

        public void Dispose()
        {
            // Nothing is held.
        }
    }
}
