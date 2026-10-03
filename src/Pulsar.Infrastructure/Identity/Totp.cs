using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace Pulsar.Infrastructure.Identity;

/// <summary>
/// Time-based one-time passwords (RFC 6238) as authenticator apps compute them: HMAC-SHA1 over 30-second steps, six
/// digits, with the key shared as Base32 (RFC 4648, no padding).
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for <paramref name="time"/> (tests and the browser's end-to-end suite use it too).</summary>
    public static string Compute(string base32Key, DateTimeOffset time) => Compute(Base32.Decode(base32Key), StepAt(time));

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 authenticator apps use HMAC-SHA1; HMAC does not rely on SHA-1's collision resistance.")]
    public static string Compute(ReadOnlySpan<byte> key, long step)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[^1] & 0x0F;
        var value = (BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7FFF_FFFF) % 1_000_000;
        return value.ToString("D6", CultureInfo.InvariantCulture);
    }
}

/// <summary>Base32 (RFC 4648 alphabet) as authenticator keys are written: spaces and padding ignored, any case.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '=' or '-')
            {
                continue;
            }

            var value = Alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("The key is not Base32.");
            }

            buffer = ((buffer << 5) | value) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
