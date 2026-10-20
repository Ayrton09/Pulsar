using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.Nist;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>
/// An SSH public key (docs/guides/sftp.md, "SSH keys"), read from the one line of OpenSSH's <c>.pub</c> files and
/// <c>authorized_keys</c> (<c>type base64 [comment]</c>, as ssh-keygen writes it and PuTTYgen shows it under "Public key
/// for pasting into OpenSSH authorized_keys file"), or from the key blob a client presents at sign-in (RFC 4253, 6.6).
/// Read strictly and with bounds: the text's length is capped before anything is decoded, the type before the key must
/// be the one inside it, every field has its exact length and nothing follows the last one, numbers are in their
/// shortest form, curve points lie on their curve (Ed25519 ones in its prime-order subgroup), and an RSA key has 2048
/// bits at least, an odd modulus and a small odd exponent. So a key has a single encoding, and its blob is compared and
/// fingerprinted as it is. Accepted: Ed25519, ECDSA P-256, P-384 and P-521, and RSA (signed with SHA-2 at sign-in, never
/// SHA-1); refused: DSA, security keys (<c>sk-…</c>), certificates and anything else. The comment is kept only as a
/// default name.
/// </summary>
public sealed class SshPublicKey
{
    public const string Ed25519 = "ssh-ed25519";
    public const string EcdsaP256 = "ecdsa-sha2-nistp256";
    public const string EcdsaP384 = "ecdsa-sha2-nistp384";
    public const string EcdsaP521 = "ecdsa-sha2-nistp521";
    public const string Rsa = "ssh-rsa";

    public const int MinRsaBits = 2048;

    /// <summary>OpenSSH's own limit.</summary>
    public const int MaxRsaBits = 16384;

    /// <summary>Text read at all: a 16384-bit RSA key and a long comment fit in it.</summary>
    public const int MaxTextLength = 8192;

    /// <summary>A key's blob: a 16384-bit RSA key takes 2,071 bytes.</summary>
    public const int MaxBlobLength = 4096;

    /// <summary>The longest accepted type: <c>ecdsa-sha2-nistp256</c>.</summary>
    public const int MaxTypeLength = 19;

    private const int MaxBase64Length = (MaxBlobLength + 2) / 3 * 4;
    private const int MaxTypeNameLength = 64;
    private const int Ed25519KeyLength = 32;
    private const int MaxRsaExponentBytes = 4;

    private static readonly string[] s_types = [Ed25519, EcdsaP256, EcdsaP384, EcdsaP521, Rsa];

    private SshPublicKey(string type, byte[] blob, int bits, string? comment)
    {
        Type = type;
        Blob = blob;
        Bits = bits;
        Comment = comment;
        Fingerprint = FingerprintOf(blob);
    }

    /// <summary>The key's type, as written in its blob: <c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256</c>…, <c>ssh-rsa</c>.</summary>
    public string Type { get; }

    /// <summary>The key in SSH's wire format, as clients present it.</summary>
    public byte[] Blob { get; }

    /// <summary>The key's size: 256 for Ed25519, the curve's for ECDSA, the modulus's for RSA.</summary>
    public int Bits { get; }

    /// <summary>What followed the key on its line, trimmed; null when nothing did (or for a blob).</summary>
    public string? Comment { get; }

    /// <summary>The SHA-256 fingerprint as OpenSSH prints it: <c>SHA256:</c> and the unpadded base64 of the blob's hash.</summary>
    public string Fingerprint { get; }

    /// <summary>Whether <paramref name="type"/> is one of the accepted key types.</summary>
    public static bool IsAccepted(string? type) => type is not null && s_types.Contains(type, StringComparer.Ordinal);

    /// <summary>The fingerprint of a key blob, as OpenSSH prints it.</summary>
    public static string FingerprintOf(ReadOnlySpan<byte> blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    /// <summary>A key as users paste it: one line of OpenSSH's format.</summary>
    public static Result<SshPublicKey> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            return AccessErrors.SshKeyInvalid;
        }

        // Said plainly: a private key pasted here must never be taken for anything else (and is never kept or echoed).
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal) || text.Contains("PuTTY-User-Key-File", StringComparison.Ordinal))
        {
            return AccessErrors.SshKeyPrivate;
        }

        if (text.Contains("BEGIN SSH2 PUBLIC KEY", StringComparison.Ordinal))
        {
            return AccessErrors.SshKeyWrongFormat;
        }

        var line = text.AsSpan().Trim();
        if (line.ContainsAny('\r', '\n'))
        {
            return AccessErrors.SshKeyInvalid;
        }

        // The type, the key (base64) and what is left, the comment: separated by spaces or tabs.
        var typeEnd = line.IndexOfAny(' ', '\t');
        if (typeEnd <= 0)
        {
            return AccessErrors.SshKeyInvalid;
        }

        var type = line[..typeEnd].ToString();
        var rest = line[typeEnd..].TrimStart(" \t");
        var keyEnd = rest.IndexOfAny(' ', '\t');
        var key = keyEnd < 0 ? rest : rest[..keyEnd];
        var comment = keyEnd < 0 ? null : rest[keyEnd..].Trim(" \t").ToString();
        if (!IsAccepted(type))
        {
            // authorized_keys options before the type ("no-pty ssh-ed25519 …") are not a key's line either.
            var second = key.ToString();
            return IsAccepted(second) || !LooksLikeType(type) ? AccessErrors.SshKeyInvalid : AccessErrors.SshKeyUnsupported;
        }

        if (key.Length is 0 or > MaxBase64Length)
        {
            return AccessErrors.SshKeyInvalid;
        }

        var blob = new byte[key.Length / 4 * 3];
        if (!Convert.TryFromBase64Chars(key, blob, out var written))
        {
            return AccessErrors.SshKeyInvalid;
        }

        return Read(blob.AsSpan(0, written), type, string.IsNullOrEmpty(comment) ? null : comment);
    }

    /// <summary>A key blob in SSH's wire format, as a client presents it at sign-in.</summary>
    public static Result<SshPublicKey> FromBlob(ReadOnlySpan<byte> blob) => blob.Length > MaxBlobLength ? AccessErrors.SshKeyInvalid : Read(blob, null, null);

    /// <summary>The key's line in OpenSSH's format, without a comment.</summary>
    public string ToOpenSsh() => Type + " " + Convert.ToBase64String(Blob);

    private static Result<SshPublicKey> Read(ReadOnlySpan<byte> blob, string? expectedType, string? comment)
    {
        var reader = new WireReader(blob);
        if (!reader.TryString(out var typeBytes) || typeBytes.Length > MaxTypeNameLength)
        {
            return AccessErrors.SshKeyInvalid;
        }

        var type = Encoding.ASCII.GetString(typeBytes);
        if (expectedType is not null && !string.Equals(type, expectedType, StringComparison.Ordinal))
        {
            // The type named before the key must be the one written in it.
            return AccessErrors.SshKeyInvalid;
        }

        Result<int> bits = type switch
        {
            Ed25519 => ReadEd25519(ref reader),
            EcdsaP256 => ReadEcdsa(ref reader, "nistp256", "P-256", 32, 256),
            EcdsaP384 => ReadEcdsa(ref reader, "nistp384", "P-384", 48, 384),
            EcdsaP521 => ReadEcdsa(ref reader, "nistp521", "P-521", 66, 521),
            Rsa => ReadRsa(ref reader),
            _ => AccessErrors.SshKeyUnsupported,
        };

        if (!bits.IsSuccess)
        {
            return bits.Error!;
        }

        return reader.AtEnd ? new SshPublicKey(type, blob.ToArray(), bits.Value, comment) : AccessErrors.SshKeyInvalid;
    }

    private static Result<int> ReadEd25519(ref WireReader reader)
    {
        // A point that decodes, is not of small order and lies in the prime-order subgroup.
        return reader.TryString(out var key) && key.Length == Ed25519KeyLength && Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.ValidatePublicKeyFull(key)
            ? 256
            : AccessErrors.SshKeyInvalid;
    }

    private static Result<int> ReadEcdsa(ref WireReader reader, string curve, string nistName, int coordinateLength, int bits)
    {
        if (!reader.TryString(out var name) || !name.SequenceEqual(Encoding.ASCII.GetBytes(curve))
            || !reader.TryString(out var point) || point.Length != 1 + (2 * coordinateLength) || point[0] != 0x04)
        {
            // Uncompressed points only, as OpenSSH writes them (a client presents the same encoding at sign-in).
            return AccessErrors.SshKeyInvalid;
        }

        try
        {
            // Coordinates below the field's prime, on the curve, not the point at infinity (the cofactor is 1).
            var decoded = NistNamedCurves.GetByName(nistName).Curve.DecodePoint(point).Normalize();
            return !decoded.IsInfinity && decoded.IsValid() ? bits : AccessErrors.SshKeyInvalid;
        }
        catch (ArgumentException)
        {
            return AccessErrors.SshKeyInvalid;
        }
    }

    private static Result<int> ReadRsa(ref WireReader reader)
    {
        if (!reader.TryPositiveMpint(out var exponent) || !reader.TryPositiveMpint(out var modulus))
        {
            return AccessErrors.SshKeyInvalid;
        }

        // A small odd exponent (3, 37, 65537…) and an odd modulus of at most OpenSSH's size.
        var bits = ((modulus.Length - 1) * 8) + 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)modulus[0]);
        if (exponent.Length > MaxRsaExponentBytes || (exponent[^1] & 1) == 0 || (exponent.Length == 1 && exponent[0] < 3)
            || (modulus[^1] & 1) == 0 || bits > MaxRsaBits)
        {
            return AccessErrors.SshKeyInvalid;
        }

        return bits < MinRsaBits ? AccessErrors.SshKeyRsaTooShort : bits;
    }

    private static bool LooksLikeType(string type) =>
        type.Length <= MaxTypeNameLength && type.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '@' or '.' or '_');

    /// <summary>SSH's wire format (RFC 4251, 5): every length within what is left.</summary>
    private ref struct WireReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _rest = data;

        public readonly bool AtEnd => _rest.IsEmpty;

        public bool TryString(out ReadOnlySpan<byte> value)
        {
            value = default;
            if (_rest.Length < 4)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(_rest);
            if (length > (uint)(_rest.Length - 4))
            {
                return false;
            }

            value = _rest.Slice(4, (int)length);
            _rest = _rest[(4 + (int)length)..];
            return true;
        }

        /// <summary>A positive mpint in its shortest form; <paramref name="magnitude"/> without the sign byte.</summary>
        public bool TryPositiveMpint(out ReadOnlySpan<byte> magnitude)
        {
            magnitude = default;
            if (!TryString(out var value) || value.IsEmpty || (value[0] & 0x80) != 0)
            {
                // Zero or negative.
                return false;
            }

            if (value[0] == 0)
            {
                // A leading zero only where the next byte's top bit would make the number negative.
                if (value.Length == 1 || (value[1] & 0x80) == 0)
                {
                    return false;
                }

                value = value[1..];
            }

            magnitude = value;
            return true;
        }
    }
}
