using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Node keys on Linux (docs/architecture/03): ECDSA P-256 in a PKCS#8 file of <paramref name="directory"/> that only its
/// owner can read (mode 0600 in a 0700 folder: root, for the service). Linux has no machine key store like Windows' CNG,
/// and most servers have no TPM the agent could rely on, so the key is as safe as root's files: the panel's revocation is
/// what stops a stolen one.
/// </summary>
/// <param name="directory">The agent's own folder for keys (in its configuration folder).</param>
[SupportedOSPlatform("linux")]
public sealed partial class FileNodeKeyStore(string directory) : INodeKeyStore
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public string Directory { get; } = Path.GetFullPath(directory);

    public INodeKey Create(string name, NodeKeyScope scope)
    {
        var path = PathOf(name);
        System.IO.Directory.CreateDirectory(Directory, OwnerOnlyFolder);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        try
        {
            // Written whole under another name, then renamed: a key file is never seen half written.
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = OwnerOnly,
                    Options = FileOptions.WriteThrough,
                }))
                {
                    stream.Write(pkcs8);
                }

                File.Move(temporary, path, overwrite: false);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        return Open(new NodeKeyReference(name, scope, NodeKeyStorage.File));
    }

    public INodeKey Open(NodeKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Storage != NodeKeyStorage.File)
        {
            throw new CryptographicException($"Unknown key storage '{reference.Storage}'.");
        }

        var path = PathOf(reference.Name);
        byte[] pkcs8;
        try
        {
            if (Libc.Lstat(path) is not { IsRegularFile: true } stat)
            {
                throw new CryptographicException($"The node key {reference.Name} does not exist (or is not a file).");
            }

            // A key anyone else could read or replace is not this node's alone any more.
            if ((stat.Permissions & 0x3F) != 0 || stat.Uid != Libc.geteuid())
            {
                throw new CryptographicException($"The node key file {path} can be read or changed by other users than its owner.");
            }

            pkcs8 = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException($"The node key {reference.Name} cannot be read: {ex.Message}", ex);
        }

        try
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return new FileNodeKey(reference, ecdsa);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    public void Delete(NodeKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        try
        {
            File.Delete(PathOf(reference.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException($"The node key {reference.Name} cannot be deleted: {ex.Message}", ex);
        }
    }

    private string PathOf(string name) => KeyName().IsMatch(name)
        ? Path.Combine(Directory, name + ".p8")
        : throw new CryptographicException($"'{name}' is not a node key name.");

    // As the agent names its keys: PulsarAgent-<32 hex digits>.
    [GeneratedRegex(@"^PulsarAgent-[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyName();

    private sealed class FileNodeKey(NodeKeyReference reference, ECDsa ecdsa) : INodeKey
    {
        public NodeKeyReference Reference { get; } = reference;

        public byte[] PublicKey => ecdsa.ExportSubjectPublicKeyInfo();

        public byte[] Sign(ReadOnlySpan<byte> data) => ecdsa.SignData(data, HashAlgorithmName.SHA256, ProtocolCrypto.SignatureFormat);

        public X509Certificate2 CreateCertificate() => NodeCertificate.Create(ecdsa);

        public void Dispose() => ecdsa.Dispose();
    }
}
