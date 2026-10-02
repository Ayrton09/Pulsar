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
