using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Databases;

/// <summary>
/// A MySQL or MariaDB server the panel creates game databases on (Phase 13), with an account allowed to create
/// databases and users. The panel connects to it itself; game servers connect to <see cref="ServerAddress"/>, which may
/// differ (the database on the node itself is 127.0.0.1 for its games). The password is a secret: the application
/// encrypts it before it reaches the storage.
/// </summary>
public sealed partial class DatabaseHost
{
    public const int MaxNameLength = 64;
    public const int MaxHostLength = 255;
    public const int MaxUserNameLength = 80;
    public const int MaxHosts = 20;
    public const int DefaultPort = 3306;

    /// <summary>Where game servers' database users may connect from, as MySQL writes it (<c>%</c>: anywhere).</summary>
    public const string AnyHost = "%";

    private DatabaseHost()
    {
        // For EF Core materialization.
        Name = null!;
        Host = null!;
        UserName = null!;
        PasswordProtected = null!;
        UserHost = null!;
        NodeIds = [];
    }

    private DatabaseHost(DateTimeOffset now)
        : this()
    {
        Id = DatabaseHostId.New();
        CreatedAt = now;
    }

    public DatabaseHostId Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>The address the panel connects to.</summary>
    public string Host { get; private set; }

    public int Port { get; private set; }

    public string UserName { get; private set; }

    public byte[] PasswordProtected { get; private set; }

    /// <summary>The address game servers connect to, when not <see cref="Host"/>; their port is <see cref="ServerPort"/> or <see cref="Port"/>.</summary>
    public string? ServerAddress { get; private set; }

    public int? ServerPort { get; private set; }

    /// <summary>Where the users it creates may connect from (<c>%</c>, an address, or a pattern such as <c>10.0.0.%</c>).</summary>
    public string UserHost { get; private set; }

    /// <summary>The nodes whose servers may use it; empty: every node.</summary>
    public List<Guid> NodeIds { get; private set; }

    /// <summary>
    /// The panel refuses to connect without TLS. Off: TLS when the server offers it, plain text otherwise (and the
    /// account's password with it). Neither checks the server's certificate.
    /// </summary>
    public bool RequireTls { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <param name="requireTls">Null: required unless <paramref name="host"/> is on this machine or a private network.</param>
    public static Result<DatabaseHost> Create(string name, string host, int port, string userName, byte[]? passwordProtected, string? serverAddress,
        int? serverPort, string? userHost, IReadOnlyCollection<Guid> nodeIds, DateTimeOffset now, bool? requireTls = null)
    {
        if (passwordProtected is null)
        {
            return DatabaseErrors.HostPasswordRequired;
        }

        var created = new DatabaseHost(now);
        var set = created.Change(name, host, port, userName, passwordProtected, serverAddress, serverPort, userHost, nodeIds, now,
            requireTls ?? !IsLocalAddress(host?.Trim() ?? string.Empty));
        return set.IsSuccess ? created : set.Error!;
    }

    /// <summary>
    /// Replaces the settings; a null <paramref name="passwordProtected"/> keeps the current password, a null
    /// <paramref name="requireTls"/> the current choice.
    /// </summary>
    public Result Change(string name, string host, int port, string userName, byte[]? passwordProtected, string? serverAddress, int? serverPort,
        string? userHost, IReadOnlyCollection<Guid> nodeIds, DateTimeOffset now, bool? requireTls = null)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            return DatabaseErrors.HostNameInvalid;
        }

        var address = host?.Trim() ?? string.Empty;
        var publicAddress = string.IsNullOrWhiteSpace(serverAddress) ? null : serverAddress.Trim();
        if (!IsAddress(address) || (publicAddress is not null && !IsAddress(publicAddress)))
        {
            return DatabaseErrors.HostAddressInvalid;
        }

        if (port is < 1 or > 65535 || serverPort is < 1 or > 65535)
        {
            return DatabaseErrors.HostPortInvalid;
        }

        var user = userName?.Trim() ?? string.Empty;
        if (user.Length is 0 or > MaxUserNameLength || user.Any(c => char.IsControl(c) || c is '\'' or '`' or '"' or '\\'))
        {
            return DatabaseErrors.HostUserInvalid;
        }

        var from = string.IsNullOrWhiteSpace(userHost) ? AnyHost : userHost.Trim();
        if (!UserHostPattern().IsMatch(from))
        {
            return DatabaseErrors.UserHostInvalid;
        }

        Name = trimmed;
        Host = address;
        Port = port;
        UserName = user;
        if (passwordProtected is not null)
        {
            PasswordProtected = passwordProtected;
        }

        ServerAddress = publicAddress;
        ServerPort = serverPort;
        UserHost = from;
        NodeIds = [.. nodeIds.Distinct()];
        RequireTls = requireTls ?? RequireTls;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Whether servers of <paramref name="node"/> may have databases here.</summary>
    public bool Serves(NodeId node) => NodeIds.Count == 0 || NodeIds.Contains(node.Value);

    /// <summary>What a game server puts in its configuration: the host and port it connects to.</summary>
    public (string Host, int Port) AddressForServers => (ServerAddress ?? Host, ServerPort ?? Port);

    /// <summary>A host name or an IP address (no scheme, port, path or spaces).</summary>
    public static bool IsAddress(string value) =>
        value.Length is > 0 and <= MaxHostLength
        && (Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6);

    /// <summary>
    /// <c>localhost</c>, or a loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7), or link-local address: where a
    /// home or LAN setup keeps its database, often without TLS. Host names other than localhost are not resolved.
    /// </summary>
    public static bool IsLocalAddress(string value)
    {
        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal;
    }

    // %, an IPv4 or IPv6 address or pattern, or a host name: letters, digits, dots, colons, hyphens, % and _ only.
    [GeneratedRegex("^[A-Za-z0-9.:%_-]{1,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserHostPattern();
}

public static class DatabaseErrors
{
    public static DomainError HostNotFound { get; } = new("database_host.not_found", "The database host does not exist.");

    public static DomainError HostNameInvalid { get; } =
        new("database_host.name_invalid", $"A database host name is one line of 1 to {DatabaseHost.MaxNameLength} characters.");

    public static DomainError HostAddressInvalid { get; } =
        new("database_host.address_invalid", "An address is a host name or an IP address, without a port.");

    public static DomainError HostPortInvalid { get; } = new("database_host.port_invalid", "A port is a number from 1 to 65535.");

    public static DomainError HostUserInvalid { get; } =
        new("database_host.user_invalid", $"The user name is 1 to {DatabaseHost.MaxUserNameLength} characters, without quotes or backslashes.");

    public static DomainError UserHostInvalid { get; } =
        new("database_host.user_host_invalid", "Connections from is %, an address or a pattern such as 10.0.0.% (letters, digits, . : - _ %).");

    public static DomainError HostPasswordRequired { get; } = new("database_host.password_required", "The password is needed.");

    public static DomainError HostInUse { get; } =
        new("database_host.in_use", "Servers have databases on this host: delete them first.");

    public static DomainError TooManyHosts { get; } = new("database_host.too_many", $"The panel has at most {DatabaseHost.MaxHosts} database hosts.");

    public static DomainError HostPasswordUnreadable { get; } =
        new("database_host.password_unreadable", "The host's password cannot be read with this panel's keys: set it again.");

    public static DomainError HostNameTaken { get; } = new("database_host.name_taken", "Another database host already has this name.");

    /// <summary>The host did not answer, or refused what the panel asked (<paramref name="reason"/> is what it said).</summary>
    public static DomainError Unreachable(string reason) =>
        new("database_host.unreachable", $"The panel could not work with the database host: {reason}");

    /// <summary>The same for a server's databases: what the host said stays in the panel's log.</summary>
    public static DomainError HostUnreachable { get; } =
        new("database.host_unreachable", "The panel could not work with the database host. An administrator finds why in the panel's log.");

    public static DomainError NotFound { get; } = new("database.not_found", "The database does not exist.");

    public static DomainError NameInvalid { get; } =
        new("database.name_invalid", $"A database name is 1 to {ServerDatabase.MaxSuffixLength} lowercase letters, digits and underscores.");

    public static DomainError NameTaken { get; } = new("database.name_taken", "The server already has a database with that name.");

    public static DomainError LimitReached { get; } = new("database.limit_reached", "The server has as many databases as it may have.");

    public static DomainError HostNotAllowed { get; } =
        new("database.host_not_allowed", "Servers of this node cannot use that database host.");

    public static DomainError PasswordUnreadable { get; } =
        new("database.password_unreadable", "The password cannot be read with this panel's keys: rotate it to get a new one.");
}
