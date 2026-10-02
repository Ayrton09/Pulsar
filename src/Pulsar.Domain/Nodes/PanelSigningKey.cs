namespace Pulsar.Domain.Nodes;

public enum PanelSigningKeyStatus
{
    /// <summary>Signs commands.</summary>
    Active,
    /// <summary>Still accepted by agents during a key rotation, no longer used to sign.</summary>
    Retiring,
    Retired,
}

/// <summary>
/// The panel's ECDSA P-256 key that signs every command (ADR-0003). Agents pin it at enrollment. The private key is
/// stored encrypted with ASP.NET Core Data Protection (ADR-0011): losing the key ring means re-enrolling every node.
/// </summary>
public sealed class PanelSigningKey
{
    private PanelSigningKey()
    {
        // For EF Core materialization.
        KeyId = null!;
        PublicKey = null!;
        ProtectedPrivateKey = null!;
    }

    public PanelSigningKey(string keyId, byte[] publicKey, byte[] protectedPrivateKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        Id = Guid.CreateVersion7(now);
        KeyId = keyId;
        PublicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        ProtectedPrivateKey = protectedPrivateKey ?? throw new ArgumentNullException(nameof(protectedPrivateKey));
        Status = PanelSigningKeyStatus.Active;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    /// <summary>Fingerprint of the public key; also the key id in signed messages.</summary>
    public string KeyId { get; private set; }

    /// <summary>SubjectPublicKeyInfo (DER).</summary>
    public byte[] PublicKey { get; private set; }

    /// <summary>PKCS#8 private key encrypted with Data Protection.</summary>
    public byte[] ProtectedPrivateKey { get; private set; }

    public PanelSigningKeyStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RetiredAt { get; private set; }

    /// <summary>A newer key signs from now on; agents keep accepting this one until it is retired.</summary>
    public void StartRetiring()
    {
        if (Status != PanelSigningKeyStatus.Active)
        {
            throw new InvalidOperationException($"Only the active key can start retiring (this one is {Status}).");
        }

        Status = PanelSigningKeyStatus.Retiring;
    }

    /// <summary>The end of a rotation: agents are told to drop the key, and the panel never signs with it again.</summary>
    public void Retire(DateTimeOffset now)
    {
        if (Status != PanelSigningKeyStatus.Retiring)
        {
            throw new InvalidOperationException($"Only a retiring key can be retired (this one is {Status}).");
        }

        Status = PanelSigningKeyStatus.Retired;
        RetiredAt = now;
    }
}
