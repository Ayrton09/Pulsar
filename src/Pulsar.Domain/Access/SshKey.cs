using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>
/// A public key a user added to sign in to SFTP with (docs/guides/sftp.md, "SSH keys"). It alone signs its user in, a
/// user with two-factor authentication too: the key is the second factor, and adding one takes the password and, with
/// two-factor authentication on, the authenticator code. Kept as its blob (SSH's wire format) and fingerprint; a user has
/// at most <see cref="MaxPerUser"/> keys, and a key is on one account only.
/// </summary>
public sealed class SshKey
{
    public const int MaxNameLength = 64;
    public const int MaxPerUser = 10;

    /// <summary><c>SHA256:</c> and 43 characters of base64.</summary>
    public const int FingerprintLength = 50;

    private SshKey()
    {
        // For EF Core materialization.
        Name = null!;
        Type = null!;
        PublicKey = null!;
        Fingerprint = null!;
    }

    private SshKey(UserId user, string name, SshPublicKey key, DateTimeOffset now)
    {
        Id = new SshKeyId(Guid.CreateVersion7(now));
        UserId = user;
        Name = name;
        Type = key.Type;
        PublicKey = key.Blob;
        Fingerprint = key.Fingerprint;
        CreatedAt = now;
    }

    public SshKeyId Id { get; private set; }

    public UserId UserId { get; private set; }

    public string Name { get; private set; }

    /// <summary>The key's type (<c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256</c>…, <c>ssh-rsa</c>).</summary>
    public string Type { get; private set; }

    /// <summary>The key's blob in SSH's wire format (<see cref="SshPublicKey.Blob"/>).</summary>
    public byte[] PublicKey { get; private set; }

    /// <summary>Its SHA-256 fingerprint as OpenSSH prints it; one per user.</summary>
    public string Fingerprint { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>
    /// A key named <paramref name="name"/>, or after the key's comment when no name is given (and the comment is a name
    /// that fits: one line of at most <see cref="MaxNameLength"/> characters).
    /// </summary>
    public static Result<SshKey> Create(UserId user, string? name, SshPublicKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        var trimmed = string.IsNullOrWhiteSpace(name) ? key.Comment?.Trim() : name.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || !DisplayNames.IsDisplayable(trimmed))
        {
            return AccessErrors.SshKeyNameInvalid;
        }

        return new SshKey(user, trimmed, key, now);
    }
}
