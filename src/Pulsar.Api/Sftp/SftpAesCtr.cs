using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Microsoft.DevTunnels.Ssh.Algorithms;
using SshBuffer = Microsoft.DevTunnels.Ssh.Buffer;

namespace Pulsar.Api.Sftp;

/// <summary>
/// <c>aes256-ctr</c> (RFC 4344) in place of the SSH library's own, which asks the platform's AES for one 16-byte block
/// at a time and held SFTP near 100 MB/s with the clients that choose CTR (Windows' <c>sftp</c> 9.5 among them). The
/// algorithm, the key, the counter and the packets are the same: the counter blocks of up to <see cref="ChunkBytes"/> are
/// encrypted in one call and XORed with the data. Its MAC stays the library's (encrypt-then-MAC HMAC-SHA2). The keystream
/// is cleared after each use and a cipher's key and buffers when the library disposes it, which it does only for the last
/// key epoch: the library swaps algorithms at NEWKEYS without disposing the old ones, so the ciphers of earlier key
/// exchanges are released to the garbage collector, not zeroed (as with the library's own cipher).
/// </summary>
internal sealed class SftpAesCtr() : EncryptionAlgorithm(AlgorithmName, "AES", CipherModeEx.CTR, KeyBits)
{
    public const string AlgorithmName = "aes256-ctr";

    /// <summary>Keystream made per call to the platform's AES.</summary>
    internal const int ChunkBytes = 16 * 1024;

    private const int KeyBits = 256;
    private const int BlockBytes = 16;

    public override ICipher CreateCipher(bool isEncryption, SshBuffer key, SshBuffer iv)
    {
        // Encryption and decryption are the same in CTR mode.
        if (key.Count < KeyBits / 8 || iv.Count < BlockBytes)
        {
            throw new ArgumentException("The key or the IV is too short for aes256-ctr.");
        }

        return new Cipher(key.Span[..(KeyBits / 8)], iv.Span[..BlockBytes]);
    }

    internal sealed class Cipher : ICipher
    {
        private readonly Aes _aes;
        private readonly byte[] _counters = new byte[ChunkBytes];
        private readonly byte[] _keystream = new byte[ChunkBytes];
        private ulong _high;
        private ulong _low;
        private bool _disposed;

        public Cipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
        {
            _aes = Aes.Create();
            var keyBytes = key.ToArray();
            try
            {
                _aes.Key = keyBytes;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }

            // The counter is the IV as one 128-bit big-endian number, carried across packets.
            _high = BinaryPrimitives.ReadUInt64BigEndian(iv);
            _low = BinaryPrimitives.ReadUInt64BigEndian(iv[8..]);
        }

        public int BlockLength => BlockBytes;

        public void Transform(SshBuffer input, SshBuffer output) => Transform(input.Span, output.Span);

        /// <summary>
        /// XORs <paramref name="input"/> (whole blocks, as SSH packets always are) with the next keystream into
        /// <paramref name="output"/>, which may be the same memory.
        /// </summary>
        public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (input.Length % BlockBytes != 0 || output.Length < input.Length)
            {
                throw new ArgumentException("CTR transforms whole blocks into a buffer at least as large.");
            }

            while (input.Length > 0)
            {
                var length = Math.Min(input.Length, ChunkBytes);
                var counters = _counters.AsSpan(0, length);
                for (var at = 0; at < length; at += BlockBytes)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(counters[at..], _high);
                    BinaryPrimitives.WriteUInt64BigEndian(counters[(at + 8)..], _low);
                    if (++_low == 0)
                    {
                        _high++;
                    }
                }

                var keystream = _keystream.AsSpan(0, length);
                _aes.EncryptEcb(counters, keystream, PaddingMode.None);
                Xor(input[..length], keystream, output[..length]);
                CryptographicOperations.ZeroMemory(keystream);
                input = input[length..];
                output = output[length..];
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_counters);
            CryptographicOperations.ZeroMemory(_keystream);
            _high = _low = 0;
        }

        private static void Xor(ReadOnlySpan<byte> data, ReadOnlySpan<byte> keystream, Span<byte> output)
        {
            var at = 0;
            if (Vector.IsHardwareAccelerated)
            {
                for (; at <= data.Length - Vector<byte>.Count; at += Vector<byte>.Count)
                {
                    (new Vector<byte>(data[at..]) ^ new Vector<byte>(keystream[at..])).CopyTo(output[at..]);
                }
            }

            for (; at < data.Length; at++)
            {
                output[at] = (byte)(data[at] ^ keystream[at]);
            }
        }
    }
}
