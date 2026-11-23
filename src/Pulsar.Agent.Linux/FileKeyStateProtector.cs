using System.Runtime.Versioning;
using System.Security.Cryptography;
using Pulsar.Agent.Core.Security;

namespace Pulsar.Agent.Linux;

/// <summary>
/// The agent's state on Linux, encrypted with AES-256-GCM under a key of its own in a file only its owner reads (root,
/// for the service): Windows' machine DPAPI plays this part there. As with DPAPI, root could always decrypt it, which is
/// why the state stays root's alone too; the encryption protects copies of the files without that key (a backup of the
/// state folder, a disk image of it).
/// </summary>
/// <param name="keyFile">Created with a random key the first time one is needed.</param>
[SupportedOSPlatform("linux")]
public sealed class FileKeyStateProtector(string keyFile) : IStateProtector
{
    private const byte Version = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] s_associated = "Pulsar.Agent.State.v1"u8.ToArray();

    private readonly Lock _lock = new();
    private byte[]? _key;

    public string Name => "aes-gcm-file";

    public string KeyFile { get; } = Path.GetFullPath(keyFile);

    public byte[] Protect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var output = new byte[1 + NonceSize + TagSize + data.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Key(create: true), TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(1 + NonceSize + TagSize), output.AsSpan(1 + NonceSize, TagSize), s_associated);
        return output;
    }

    public byte[] Unprotect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 1 + NonceSize + TagSize || data[0] != Version)
        {
            throw new CryptographicException("The data was not protected by this agent's state key.");
        }

        var plain = new byte[data.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(Key(create: false), TagSize);
        aes.Decrypt(data.AsSpan(1, NonceSize), data.AsSpan(1 + NonceSize + TagSize), data.AsSpan(1 + NonceSize, TagSize), plain, s_associated);
        return plain;
    }

    private byte[] Key(bool create)
    {
        lock (_lock)
        {
            return _key ??= Load(create);
        }
    }

    private byte[] Load(bool create)
    {
        try
        {
            if (Libc.Lstat(KeyFile) is null)
            {
                if (!create)
                {
                    throw new CryptographicException($"The agent's state key ({KeyFile}) is missing: what it protected cannot be read.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var key = RandomNumberGenerator.GetBytes(KeySize);
                try
                {
                    using var stream = new FileStream(KeyFile, new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                        Options = FileOptions.WriteThrough,
                    });
                    stream.Write(key);
                    return key;
                }
                catch (IOException) when (Libc.Lstat(KeyFile) is not null)
                {
                    // Another process of the agent made it first: its key is the one.
                }
            }

            if (Libc.Lstat(KeyFile) is not { IsRegularFile: true } stat || (stat.Permissions & 0x3F) != 0 || stat.Uid != Libc.geteuid())
            {
                throw new CryptographicException($"The agent's state key ({KeyFile}) is not a file only its owner can read and change.");
            }

            var bytes = File.ReadAllBytes(KeyFile);
            return bytes.Length == KeySize ? bytes : throw new CryptographicException($"The agent's state key ({KeyFile}) is damaged.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException($"The agent's state key ({KeyFile}) cannot be read: {ex.Message}", ex);
        }
    }
}
