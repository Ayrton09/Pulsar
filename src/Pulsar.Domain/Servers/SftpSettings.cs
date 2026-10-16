using System.Globalization;
using System.Net;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>
/// The panel's SFTP server (one row, Phase 17): on by default, on port 2022, and its host key. The key's private half is
/// stored encrypted with ASP.NET Core Data Protection (ADR-0011), like the panel signing key; it is made on the first
/// start and kept, so its fingerprint stays the one users checked at their first connection.
/// </summary>
public sealed class SftpSettings
{
    public const int SingletonId = 1;
    public const int DefaultPort = 2022;
    public const int MaxHostLength = 253;

    private SftpSettings()
    {
        // For EF Core materialization.
    }

    public int Id { get; private set; }

    public bool Enabled { get; private set; }

    public int Port { get; private set; }

    /// <summary>The host name users connect to, for the connection hint; null: the host the panel is reached by.</summary>
    public string? PublicHost { get; private set; }

    /// <summary>The host key's public key (SubjectPublicKeyInfo, DER); null until the first start made one.</summary>
    public byte[]? HostPublicKey { get; private set; }

    /// <summary>The host key's private key (PKCS#8) encrypted with Data Protection.</summary>
    public byte[]? ProtectedHostKey { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SftpSettings Default(DateTimeOffset now) => new() { Id = SingletonId, Enabled = true, Port = DefaultPort, UpdatedAt = now };

    /// <summary>Why the settings cannot be used, or null.</summary>
    public static DomainError? Validate(int port, string? publicHost)
    {
        if (port is < IPEndPoint.MinPort + 1 or > IPEndPoint.MaxPort)
        {
            return SftpErrors.PortInvalid;
        }

        return publicHost is not null && !IsValidHost(publicHost) ? SftpErrors.HostInvalid : null;
    }

    /// <summary>A DNS name or an IP address, as a user would type it after <c>sftp://name@</c>.</summary>
    public static bool IsValidHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Length is 0 or > MaxHostLength || host.Any(c => c > 0x7E || c <= ' '))
        {
            return false;
        }

        var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return Uri.CheckHostName(bare) switch
        {
            UriHostNameType.Dns => bare.Split('.').All(label => label.Length is > 0 and <= 63),
            UriHostNameType.IPv4 => IPAddress.TryParse(bare, out _) && bare.Count(c => c == '.') == 3,
            UriHostNameType.IPv6 => IPAddress.TryParse(bare, out _),
            _ => false,
        };
    }

    /// <summary>False when nothing changed. The caller validated the values (<see cref="Validate"/>).</summary>
    public bool Change(bool enabled, int port, string? publicHost, DateTimeOffset now)
    {
        if (Validate(port, publicHost) is { } error)
        {
            throw new ArgumentException(error.Message, nameof(port));
        }

        var host = publicHost?.ToLower(CultureInfo.InvariantCulture);
        if (Enabled == enabled && Port == port && PublicHost == host)
        {
            return false;
        }

        Enabled = enabled;
        Port = port;
        PublicHost = host;
        UpdatedAt = now;
        return true;
    }

    /// <summary>The host key made on the first start; never replaced (users pinned its fingerprint).</summary>
    public void SetHostKey(byte[] publicKey, byte[] protectedPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(protectedPrivateKey);
        if (HostPublicKey is not null)
        {
            throw new InvalidOperationException("The SFTP host key already exists.");
        }

        HostPublicKey = publicKey;
        ProtectedHostKey = protectedPrivateKey;
    }
}

public static class SftpErrors
{
    public static DomainError PortInvalid { get; } = new("sftp.invalid_port", "The SFTP port must be between 1 and 65535.");

    public static DomainError HostInvalid { get; } =
        new("sftp.invalid_host", "The public host must be a host name or an IP address, without a scheme, port or path.");
}
