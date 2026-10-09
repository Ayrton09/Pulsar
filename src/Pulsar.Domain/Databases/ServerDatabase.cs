using System.Security.Cryptography;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Databases;

/// <summary>
/// A MySQL/MariaDB database of one server, on a <see cref="DatabaseHost"/>, with a user of the same name that may use
/// it alone. The name is <c>s{server short id}_{suffix}</c>: the suffix is what the server's owner chose (lowercase
/// letters, digits and underscores only), and the prefix keeps every server's databases apart on a shared host.
/// </summary>
public sealed class ServerDatabase
{
    /// <summary>MySQL user names have at most 32 characters: "s" + 8 + "_" leaves 23.</summary>
    public const int MaxSuffixLength = 20;

    public const int PasswordLength = 24;

    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private ServerDatabase()
    {
        // For EF Core materialization.
        Name = null!;
        UserHost = null!;
        PasswordProtected = null!;
    }

    private ServerDatabase(ServerId serverId, DatabaseHostId hostId, string name, string userHost, byte[] passwordProtected, DateTimeOffset now)
    {
        Id = ServerDatabaseId.New();
        ServerId = serverId;
        HostId = hostId;
        Name = name;
        UserHost = userHost;
        PasswordProtected = passwordProtected;
        CreatedAt = now;
        PasswordChangedAt = now;
    }

    public ServerDatabaseId Id { get; private set; }

    public ServerId ServerId { get; private set; }

    public DatabaseHostId HostId { get; private set; }

    /// <summary>The database's name, which is also its user's.</summary>
    public string Name { get; private set; }

    /// <summary>Where its user may connect from (the host's setting when it was created).</summary>
    public string UserHost { get; private set; }

    public byte[] PasswordProtected { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset PasswordChangedAt { get; private set; }

    public static Result<ServerDatabase> Create(ServerId server, string serverShortId, DatabaseHost host, string suffix, byte[] passwordProtected,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(passwordProtected);
        var name = NameFor(serverShortId, suffix);
        return name is null
            ? DatabaseErrors.NameInvalid
            : new ServerDatabase(server, host.Id, name, host.UserHost, passwordProtected, now);
    }

    /// <summary>The full name for a server's suffix, or null when the suffix is not allowed.</summary>
    public static string? NameFor(string serverShortId, string? suffix)
    {
        ArgumentNullException.ThrowIfNull(serverShortId);
        var value = suffix?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= MaxSuffixLength && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
            && serverShortId.All(char.IsAsciiLetterOrDigit)
            ? $"s{serverShortId.ToLowerInvariant()}_{value}"
            : null;
    }

    /// <summary>A new random password: letters and digits only (nothing to escape in a game's configuration file).</summary>
    public static string NewPassword() => RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);

    public void PasswordChanged(byte[] passwordProtected, DateTimeOffset now)
    {
        PasswordProtected = passwordProtected ?? throw new ArgumentNullException(nameof(passwordProtected));
        PasswordChangedAt = now;
    }
}
