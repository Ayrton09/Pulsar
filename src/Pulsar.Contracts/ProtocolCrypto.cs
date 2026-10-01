using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pulsar.Contracts;

/// <summary>
/// Shared cryptographic conventions of the protocol, so the panel and the agent sign and verify exactly the same bytes:
/// ECDSA P-256 with SHA-256, signatures in IEEE P1363 format (r‖s, 64 bytes), public keys as SubjectPublicKeyInfo DER.
/// </summary>
public static class ProtocolCrypto
{
    public const string Algorithm = "ecdsa-p256-sha256";
    public const DSASignatureFormat SignatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;
    public const int SignatureLength = 64;

    /// <summary>
    /// Unambiguous input for a signature: the purpose and every field, each prefixed with its 4-byte big-endian
    /// length. ("ab","c") and ("a","bc") produce different bytes, and a signature for one purpose never validates
    /// for another.
    /// </summary>
    public static byte[] SignedData(string purpose, params ReadOnlySpan<byte[]> fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        var length = 4 + purposeBytes.Length;
        foreach (var field in fields)
        {
            length += 4 + field.Length;
        }

        var buffer = new byte[length];
        var offset = Write(buffer, 0, purposeBytes);
        foreach (var field in fields)
        {
            offset = Write(buffer, offset, field);
        }

        return buffer;
    }

    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    public static byte[] GuidBytes(Guid value) => value.ToByteArray(bigEndian: true);

    /// <summary>"sha256:" + base64url of the SHA-256 of the SubjectPublicKeyInfo: what humans compare.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        "sha256:" + Convert.ToBase64String(SHA256.HashData(subjectPublicKeyInfo)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Imports a P-256 public key, rejecting any other curve or algorithm.</summary>
    public static ECDsa ImportPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            var curve = key.ExportParameters(includePrivateParameters: false).Curve;
            if (!curve.IsNamed || curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new CryptographicException("Only ECDSA P-256 keys are accepted.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static bool Verify(ECDsa publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        signature.Length == SignatureLength && publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, SignatureFormat);

    /// <summary>Verifies with a key given as SubjectPublicKeyInfo; malformed keys simply do not verify.</summary>
    public static bool Verify(ReadOnlySpan<byte> subjectPublicKeyInfo, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var key = ImportPublicKey(subjectPublicKeyInfo);
            return Verify(key, data, signature);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static byte[] Sign(ECDsa privateKey, ReadOnlySpan<byte> data) =>
        privateKey.SignData(data, HashAlgorithmName.SHA256, SignatureFormat);

    /// <summary>Signs <paramref name="payload"/> for one purpose with a panel key.</summary>
    public static SignedMessage SignMessage(ECDsa privateKey, string keyId, string purpose, byte[] payload) =>
        new(keyId, payload, Sign(privateKey, SignedData(purpose, payload)));

    /// <summary>True when <paramref name="message"/> was signed for <paramref name="purpose"/> by the given public key.</summary>
    public static bool VerifyMessage(SignedMessage message, string purpose, ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Payload is not null && message.Signature is not null
            && Verify(subjectPublicKeyInfo, SignedData(purpose, message.Payload), message.Signature);
    }

    private static int Write(byte[] buffer, int offset, ReadOnlySpan<byte> field)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), field.Length);
        field.CopyTo(buffer.AsSpan(offset + 4));
        return offset + 4 + field.Length;
    }
}
