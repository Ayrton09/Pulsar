using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pulsar.Agent.Core.Security;

/// <summary>Where a node key lives. The service runs as SYSTEM, so production keys are machine keys.</summary>
public enum NodeKeyScope
{
    Machine,
    /// <summary>The current user's key store: development only (no administrator rights, no service).</summary>
    User,
}

/// <summary>Names the panel understands for <see cref="Pulsar.Contracts.EnrollRequest.KeyStorage"/>.</summary>
public static class NodeKeyStorage
{
    /// <summary>Non-exportable key in the TPM (Microsoft Platform Crypto Provider).</summary>
    public const string Tpm = "tpm";

    /// <summary>Non-exportable key in the software key storage provider.</summary>
    public const string Software = "software";
}

/// <summary>How to find a node key again: it is identified by name, scope and the provider that holds it.</summary>
public sealed record NodeKeyReference(string Name, NodeKeyScope Scope, string Storage);

/// <summary>
/// The node's private key (ECDSA P-256). Implementations keep it non-exportable; the agent only ever signs with it
/// and reads its public half.
/// </summary>
public interface INodeKey : IDisposable
{
    NodeKeyReference Reference { get; }

    /// <summary>SubjectPublicKeyInfo (DER).</summary>
    byte[] PublicKey { get; }

    /// <summary>ECDSA P-256 / SHA-256 signature in IEEE P1363 format, as <see cref="Pulsar.Contracts.ProtocolCrypto"/> expects.</summary>
    byte[] Sign(ReadOnlySpan<byte> data);

    /// <summary>The key as a TLS client certificate (<see cref="NodeCertificate"/>), its private key still in its store.</summary>
    X509Certificate2 CreateCertificate();
}

/// <summary>
/// The node key as a TLS client certificate (Phase 16, docs/architecture/03 "Client certificates"): self-signed, for
/// client authentication. The panel matches its key with the node's credential and looks at nothing else, so no CA
/// issues it and its dates only keep TLS stacks content (from a day back, for clocks behind, to ten years ahead).
/// </summary>
public static class NodeCertificate
{
    public static X509Certificate2 Create(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var request = new CertificateRequest("CN=Pulsar node", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2", "Client Authentication")], critical: false));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }

    /// <summary>
    /// <paramref name="key"/>'s certificate, or null when its store cannot make one (<paramref name="failure"/> says why):
    /// the agent then works where no certificate is asked for, and says it cannot move to the agents' address.
    /// </summary>
    public static X509Certificate2? TryCreate(INodeKey key, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            failure = null;
            return key.CreateCertificate();
        }
        catch (CryptographicException ex)
        {
            failure = ex.Message;
            return null;
        }
    }
}

public interface INodeKeyStore
{
    /// <summary>Creates a new key, in the TPM when the machine has one.</summary>
    INodeKey Create(string name, NodeKeyScope scope);

    /// <summary>Opens an existing key; throws when it no longer exists.</summary>
    INodeKey Open(NodeKeyReference reference);

    /// <summary>Deletes the key if it exists.</summary>
    void Delete(NodeKeyReference reference);
}
