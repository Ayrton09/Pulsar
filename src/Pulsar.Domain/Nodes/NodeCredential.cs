using System.Security.Cryptography;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Nodes;

public enum NodeCredentialStatus
{
    /// <summary>Created during a rotation; becomes active once the agent authenticates with it.</summary>
    Pending,
    Active,
    /// <summary>Replaced by a newer credential.</summary>
    Superseded,
    Revoked,
}

public enum NodeKeyStorage
{
    /// <summary>Non-exportable key in the TPM (Microsoft Platform Crypto Provider).</summary>
    Tpm,
    /// <summary>Non-exportable key in the software key storage provider.</summary>
    Software,
}

/// <summary>
/// The public half of a node's key pair (the private key never leaves the node). At most one credential per node is
/// active; authentication uses it, and revoking it cuts the node off immediately.
/// </summary>
public sealed class NodeCredential
{
    public const string Algorithm = "ecdsa-p256-sha256";
    public const int MaxRevokeReasonLength = 256;

    private NodeCredential()
    {
        // For EF Core materialization.
        PublicKey = null!;
        Fingerprint = null!;
    }

    private NodeCredential(Guid id, NodeId nodeId, byte[] publicKey, string fingerprint, NodeKeyStorage keyStorage, NodeCredentialStatus status, DateTimeOffset now)
    {
        Id = id;
        NodeId = nodeId;
        PublicKey = publicKey;
        Fingerprint = fingerprint;
        KeyStorage = keyStorage;
        Status = status;
        CreatedAt = now;
        ActivatedAt = status == NodeCredentialStatus.Active ? now : null;
    }

    public Guid Id { get; private set; }

    public NodeId NodeId { get; private set; }

    /// <summary>SubjectPublicKeyInfo (DER) of an ECDSA P-256 key.</summary>
    public byte[] PublicKey { get; private set; }

    public string Fingerprint { get; private set; }

    public NodeKeyStorage KeyStorage { get; private set; }

    public NodeCredentialStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public UserId? RevokedBy { get; private set; }

    public string? RevokeReason { get; private set; }

    /// <summary>A credential registered at enrollment: active from the start.</summary>
    public static NodeCredential Enrolled(NodeId node, byte[] publicKey, string fingerprint, NodeKeyStorage keyStorage, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        return new NodeCredential(Guid.CreateVersion7(now), node, publicKey, fingerprint, keyStorage, NodeCredentialStatus.Active, now);
    }

    /// <summary>
    /// A key registered by a rotation (docs/architecture/03): pending until the agent authenticates with it, so a
    /// rotation that fails halfway never leaves the node without a working credential.
    /// </summary>
    public static NodeCredential Pending(NodeId node, byte[] publicKey, string fingerprint, NodeKeyStorage keyStorage, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        return new NodeCredential(Guid.CreateVersion7(now), node, publicKey, fingerprint, keyStorage, NodeCredentialStatus.Pending, now);
    }

    public bool IsActive => Status == NodeCredentialStatus.Active;

    /// <summary>Active credentials, and pending ones (whose first authentication completes a rotation).</summary>
    public bool CanAuthenticate => Status is NodeCredentialStatus.Active or NodeCredentialStatus.Pending;

    public DateTimeOffset? SupersededAt { get; private set; }

    /// <summary>A pending credential becomes the node's active one.</summary>
    public void Activate(DateTimeOffset now)
    {
        if (Status != NodeCredentialStatus.Pending)
        {
            throw new InvalidOperationException($"Only a pending credential can be activated (this one is {Status}).");
        }

        Status = NodeCredentialStatus.Active;
        ActivatedAt = now;
    }

    /// <summary>Replaced by a newer key: it can no longer authenticate. A pending credential can be superseded too.</summary>
    public void Supersede(DateTimeOffset now)
    {
        if (Status is NodeCredentialStatus.Active or NodeCredentialStatus.Pending)
        {
            Status = NodeCredentialStatus.Superseded;
            SupersededAt = now;
        }
    }

    public void Used(DateTimeOffset now) => LastUsedAt = now;

    public Result Revoke(UserId? by, string? reason, DateTimeOffset now)
    {
        if (Status == NodeCredentialStatus.Revoked)
        {
            return Result.Success();
        }

        Status = NodeCredentialStatus.Revoked;
        RevokedAt = now;
        RevokedBy = by;
        var trimmed = reason?.Trim();
        RevokeReason = string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, MaxRevokeReasonLength)];
        return Result.Success();
    }
}

/// <summary>
/// Single-use enrollment token (docs/architecture/03): 32 random bytes shown once to the administrator; only its
/// SHA-256 is stored. Valid for a short time and consumed atomically by the first successful enrollment.
/// </summary>
public sealed class NodeEnrollmentToken
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);

    private NodeEnrollmentToken()
    {
        // For EF Core materialization.
        TokenHash = null!;
    }

    private NodeEnrollmentToken(NodeId node, byte[] tokenHash, UserId? createdBy, DateTimeOffset now, TimeSpan lifetime)
    {
        Id = Guid.CreateVersion7(now);
        NodeId = node;
        TokenHash = tokenHash;
        CreatedBy = createdBy;
        CreatedAt = now;
        ExpiresAt = now + lifetime;
    }

    public Guid Id { get; private set; }

    public NodeId NodeId { get; private set; }

    public byte[] TokenHash { get; private set; }

    public UserId? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    public Guid? ConsumedByCredentialId { get; private set; }

    /// <summary>Creates a token; the plain value is returned once and never stored.</summary>
    public static (NodeEnrollmentToken Token, string PlainValue) Issue(NodeId node, UserId? createdBy, DateTimeOffset now, TimeSpan? lifetime = null)
    {
        var plain = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (new NodeEnrollmentToken(node, Hash(plain), createdBy, now, lifetime ?? DefaultLifetime), plain);
    }

    public static byte[] Hash(string plainValue) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plainValue));

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;

    /// <summary>Records which credential the token produced (the atomic consumption itself happens in the database).</summary>
    public void Consumed(Guid credentialId, DateTimeOffset now)
    {
        ConsumedAt = now;
        ConsumedByCredentialId = credentialId;
    }
}
