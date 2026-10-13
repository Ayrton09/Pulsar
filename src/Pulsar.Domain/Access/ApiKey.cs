using System.Security.Cryptography;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>
/// A key a user creates for scripts and tools (<c>Authorization: Bearer pulsar_…</c>; docs/architecture/02, "API keys").
/// It acts as its user, with the user's permissions as they are at each request, optionally narrowed to a list of
/// permissions. The plain value is shown once and never stored: only its SHA-256.
/// </summary>
public sealed class ApiKey
{
    public const string TokenPrefix = "pulsar_";
    public const int MaxNameLength = 64;
    public const int MaxPerUser = 25;

    /// <summary>How much of the plain value is kept to recognize a key in lists (<c>pulsar_</c> and 6 characters).</summary>
    public const int VisiblePrefixLength = 13;

    private ApiKey()
    {
        // For EF Core materialization.
        Name = null!;
        Prefix = null!;
        TokenHash = null!;
    }

    private ApiKey(UserId user, string name, string prefix, byte[] tokenHash, IReadOnlyList<string>? permissions, DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        Id = new ApiKeyId(Guid.CreateVersion7(now));
        UserId = user;
        Name = name;
        Prefix = prefix;
        TokenHash = tokenHash;
        Permissions = permissions;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    public ApiKeyId Id { get; private set; }

    public UserId UserId { get; private set; }

    public string Name { get; private set; }

    /// <summary>The first characters of the plain value, to tell keys apart (not enough to guess the rest).</summary>
    public string Prefix { get; private set; }

    public byte[] TokenHash { get; private set; }

    /// <summary>The only permissions the key may use (still only where its user has them); null: all of the user's.</summary>
    public IReadOnlyList<string>? Permissions { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Creates a key; the plain value is returned once and never stored.</summary>
    public static Result<(ApiKey Key, string PlainValue)> Create(UserId user, string? name, IReadOnlyCollection<string>? permissions,
        DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || !DisplayNames.IsDisplayable(trimmed))
        {
            return AccessErrors.ApiKeyNameInvalid;
        }

        if (permissions is not null && (permissions.Count == 0 || permissions.Any(p => !PermissionCatalog.Exists(p))))
        {
            return AccessErrors.ApiKeyPermissionsInvalid;
        }

        if (expiresAt is { } expiry && expiry <= now)
        {
            return AccessErrors.ApiKeyExpiryInvalid;
        }

        var plain = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var key = new ApiKey(user, trimmed, plain[..VisiblePrefixLength], Hash(plain),
            permissions?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), expiresAt, now);
        return (key, plain);
    }

    /// <summary>Whether a bearer value has the shape of a key (and so is looked up as one).</summary>
    public static bool LooksLikeKey(string value) => value.StartsWith(TokenPrefix, StringComparison.Ordinal) && value.Length is > 20 and < 128;

    public static byte[] Hash(string plainValue) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plainValue));

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is not { } expiry || now < expiry);

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
