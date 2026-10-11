using System.Net;
using System.Net.Sockets;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>
/// S3-compatible storage backups are copied to (Phase 10): AWS S3, Cloudflare R2, Backblaze B2, MinIO, SeaweedFS… A
/// server chooses one (or none); its node uploads each new backup there and fetches it back when it no longer has it.
/// The secret access key is a secret: the application encrypts it before it reaches the storage.
/// </summary>
public sealed class BackupStorage
{
    public const int MaxNameLength = 64;
    public const int MaxEndpointLength = 255;
    public const int MaxRegionLength = 64;
    public const int MaxPrefixLength = 200;
    public const int MaxAccessKeyLength = 128;
    public const int MaxStorages = 20;

    private BackupStorage()
    {
        // For EF Core materialization.
        Name = null!;
        Region = null!;
        Bucket = null!;
        Prefix = null!;
        AccessKeyId = null!;
        SecretProtected = null!;
    }

    private BackupStorage(DateTimeOffset now)
    {
        Id = BackupStorageId.New();
        CreatedAt = now;
        Name = null!;
        Region = null!;
        Bucket = null!;
        Prefix = null!;
        AccessKeyId = null!;
        SecretProtected = null!;
    }

    public BackupStorageId Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>The service's address (https://…, or http://… on the local network); null for AWS S3 itself.</summary>
    public string? Endpoint { get; private set; }

    /// <summary>AWS region, or what the service asks for ("auto" for R2, "us-east-1" for most others).</summary>
    public string Region { get; private set; }

    public string Bucket { get; private set; }

    /// <summary>Where in the bucket Pulsar writes ("" or "folder/"); each backup is <c>{prefix}{server}/{backup}.zip</c>.</summary>
    public string Prefix { get; private set; }

    public string AccessKeyId { get; private set; }

    public byte[] SecretProtected { get; private set; }

    /// <summary>The bucket in the path rather than in the host name (most S3-compatible services want it).</summary>
    public bool PathStyle { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<BackupStorage> Create(string name, string? endpoint, string region, string bucket, string? prefix, string accessKeyId,
        byte[]? secretProtected, bool pathStyle, DateTimeOffset now)
    {
        if (secretProtected is null)
        {
            return BackupStorageErrors.SecretRequired;
        }

        var storage = new BackupStorage(now);
        var set = storage.Change(name, endpoint, region, bucket, prefix, accessKeyId, secretProtected, pathStyle, now);
        return set.IsSuccess ? storage : set.Error!;
    }

    /// <summary>
    /// Replaces the settings; a null <paramref name="secretProtected"/> keeps the current secret. While backups have
    /// copies here (<paramref name="holdsCopies"/>) the endpoint and the bucket stay: their copies are found there,
    /// under the key each one kept (its prefix included). Credentials, region, path style and the prefix of new
    /// copies may change (the same bucket, reached differently).
    /// </summary>
    public Result Change(string name, string? endpoint, string region, string bucket, string? prefix, string accessKeyId, byte[]? secretProtected,
        bool pathStyle, DateTimeOffset now, bool holdsCopies = false)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            return BackupStorageErrors.NameInvalid;
        }

        var address = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim().TrimEnd('/');
        if (address is not null && !IsEndpoint(address))
        {
            return BackupStorageErrors.EndpointInvalid;
        }

        if (address is not null && !IsSafeEndpoint(address))
        {
            return BackupStorageErrors.EndpointNeedsHttps;
        }

        var zone = region?.Trim() ?? string.Empty;
        if (zone.Length is 0 or > MaxRegionLength || !zone.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return BackupStorageErrors.RegionInvalid;
        }

        var bucketName = bucket?.Trim() ?? string.Empty;
        if (!IsBucketName(bucketName))
        {
            return BackupStorageErrors.BucketInvalid;
        }

        var folder = NormalizePrefix(prefix);
        if (folder is null)
        {
            return BackupStorageErrors.PrefixInvalid;
        }

        var keyId = accessKeyId?.Trim() ?? string.Empty;
        if (keyId.Length is 0 or > MaxAccessKeyLength || keyId.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return BackupStorageErrors.AccessKeyInvalid;
        }

        if (holdsCopies && (!string.Equals(Endpoint, address, StringComparison.OrdinalIgnoreCase) || !string.Equals(Bucket, bucketName, StringComparison.Ordinal)))
        {
            return BackupStorageErrors.HoldsCopies;
        }

        Name = trimmed;
        Endpoint = address;
        Region = zone;
        Bucket = bucketName;
        Prefix = folder;
        AccessKeyId = keyId;
        if (secretProtected is not null)
        {
            SecretProtected = secretProtected;
        }

        PathStyle = pathStyle;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>The object key of a backup: <c>{prefix}{server short id}/{backup id}.zip</c>.</summary>
    public string KeyFor(string serverShortId, BackupId backupId) => $"{Prefix}{serverShortId}/{backupId.Value:N}.zip";

    /// <summary>An http or https address with no user info, query or fragment.</summary>
    public static bool IsEndpoint(string address) =>
        address.Length <= MaxEndpointLength
        && Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath == "/";

    /// <summary>
    /// Https, or plain http to a loopback, private or link-local address (storage on the same machine or the LAN). A host
    /// name passes here: the panel cannot resolve it as the node would, so the node checks the addresses it resolves to.
    /// </summary>
    public static bool IsSafeEndpoint(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        if (uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsPrivateAddress(ip);
    }

    /// <summary>Loopback (127/8, ::1), private (10/8, 172.16/12, 192.168/16, fc00::/7) or link-local (169.254/16, fe80::/10).</summary>
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] is 10 or 127
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (IPAddress.IsLoopback(address) || address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal);
    }

    /// <summary>S3's naming rules: 3–63 lowercase letters, digits, dots and hyphens, starting and ending with a letter or digit.</summary>
    public static bool IsBucketName(string name) =>
        name.Length is >= 3 and <= 63
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-')
        && char.IsAsciiLetterOrDigit(name[0]) && char.IsAsciiLetterOrDigit(name[^1])
        && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>"" or "a/b/" (a trailing slash added); null when not allowed.</summary>
    public static string? NormalizePrefix(string? prefix)
    {
        var value = prefix?.Trim().Trim('/') ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var parts = value.Split('/');
        return value.Length < MaxPrefixLength
            && parts.All(p => p.Length > 0 && p != "." && p != ".." && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            ? value + "/"
            : null;
    }
}

public static class BackupStorageErrors
{
    public static DomainError NotFound { get; } = new("backup_storage.not_found", "The backup storage does not exist.");

    public static DomainError NameInvalid { get; } =
        new("backup_storage.name_invalid", $"A storage name is one line of 1 to {BackupStorage.MaxNameLength} characters.");

    public static DomainError EndpointInvalid { get; } =
        new("backup_storage.endpoint_invalid", "The endpoint is the service's address (e.g. https://<account>.r2.cloudflarestorage.com), or empty for AWS S3.");

    public static DomainError EndpointNeedsHttps { get; } =
        new("backup_storage.endpoint_needs_https",
            "Plain http is only for storage on this machine or your local network (a private address): use https for a public address.");

    public static DomainError RegionInvalid { get; } = new("backup_storage.region_invalid", "The region is a name like us-east-1 or auto.");

    public static DomainError BucketInvalid { get; } =
        new("backup_storage.bucket_invalid", "A bucket name is 3 to 63 lowercase letters, digits, dots and hyphens.");

    public static DomainError PrefixInvalid { get; } =
        new("backup_storage.prefix_invalid", "The folder is made of letters, digits, dots, hyphens and underscores, separated by slashes.");

    public static DomainError AccessKeyInvalid { get; } = new("backup_storage.access_key_invalid", "The access key id is not valid.");

    public static DomainError SecretRequired { get; } = new("backup_storage.secret_required", "The secret access key is needed.");

    public static DomainError InUse { get; } =
        new("backup_storage.in_use", "Servers or backups use this storage: choose another storage for them, or delete their backups, first.");

    public static DomainError TooMany { get; } = new("backup_storage.too_many", $"The panel has at most {BackupStorage.MaxStorages} storages.");

    public static DomainError HoldsCopies { get; } =
        new("backup_storage.holds_copies",
            "Backups have copies in this storage: its endpoint and bucket stay while they do. Add another storage for a new bucket.");
}
