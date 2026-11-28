using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux;

/// <summary>A server's Linux account: its name, user id and group id.</summary>
public sealed record LinuxAccount(string Name, uint Uid, uint Gid, string Home);

/// <summary>
/// The game servers' accounts on Linux (docs/architecture/06, "Isolation"): one system user per server,
/// <c>pulsar_&lt;shortId&gt;</c> as on Windows, with a group of its own, no password (it cannot log in) and no shell
/// (<c>nologin</c>). Its games run as it, so a server reaches its own folder and nothing of the others'. An account of that
/// name the agent did not make is never taken over.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxServerAccounts(ILogger<LinuxServerAccounts> logger)
{
    public const string Prefix = "pulsar_";

    // Written into the account's comment field: how the agent knows its own accounts.
    private const string Marker = "managed by the Pulsar agent";

    /// <summary>The programs it runs, and how a test replaces them.</summary>
    // A kept folder made root's on delete (chown -R) can be a large game's whole tree: they get as long as a deletion does.
    internal Func<string, IReadOnlyList<string>, string> Run { get; init; } = (program, arguments) => SystemdUnits.Run(program, arguments, TimeSpan.FromMinutes(30));

    /// <summary>Raised with the account's name once <see cref="Delete"/> removed it (the file access forgets its ids).</summary>
    public event Action<string>? Deleted;

    public static string AccountName(string shortId)
    {
        ServerShortId.Validate(shortId);
        return Prefix + shortId;
    }

    /// <summary>
    /// The account (created when missing, with <paramref name="home"/> as its home: the server's folder). One that exists
    /// but was not made by the agent is refused.
    /// </summary>
    /// <exception cref="GameProcessException"><c>server.account_unusable</c>: an account of that name is someone else's.</exception>
    public LinuxAccount Ensure(string name, string shortId, string home)
    {
        ValidateName(name);
        if (Find(name) is { } existing)
        {
            if (!existing.Comment.Contains(Marker, StringComparison.Ordinal) || existing.Shell != Nologin())
            {
                throw new GameProcessException("server.account_unusable",
                    $"The account {name} exists already and was not made by the Pulsar agent: Pulsar does not run a server as it. Delete or rename that account.");
            }

            LogAccountExists(name);
            return existing.Account;
        }

        try
        {
            Run("useradd", ["--system", "--user-group", "--no-create-home", "--home-dir", home, "--shell", Nologin(),
                "--comment", $"Pulsar game server {shortId} ({Marker})", name]);
        }
        catch (Win32Exception ex)
        {
            throw new GameProcessException("server.account_failed", $"The account {name} could not be created: {ex.Message}", ex);
        }

        LogAccountCreated(name);
        return Find(name)?.Account ?? throw new GameProcessException("server.account_failed", $"The account {name} was created but cannot be found.");
    }

    /// <summary>
    /// The account, or null when there is none. One of that name the agent did not make is never acted as.
    /// </summary>
    /// <exception cref="GameProcessException"><c>server.account_unusable</c>: an account of that name is someone else's.</exception>
    public LinuxAccount? Lookup(string name)
    {
        ValidateName(name);
        if (Find(name) is not { } existing)
        {
            return null;
        }

        return existing.Comment.Contains(Marker, StringComparison.Ordinal) && existing.Shell == Nologin()
            ? existing.Account
            : throw new GameProcessException("server.account_unusable",
                $"The account {name} was not made by the Pulsar agent: Pulsar does not act as it. Delete or rename that account.");
    }

    /// <summary>
    /// Removes the account and its group (only one the agent made). False when it did not exist.
    /// </summary>
    public bool Delete(string name)
    {
        ValidateName(name);
        if (Find(name) is not { } existing)
        {
            return false;
        }

        if (!existing.Comment.Contains(Marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The account {name} was not made by the Pulsar agent: it is left alone.");
        }

        try
        {
            // Its home is the server's folder, which goes on its own (or is kept, on unenroll): never -r. A folder kept
            // becomes root's first: a later account given the same user id must not own what this one left.
            if (Libc.Lstat(existing.Account.Home) is { IsDirectory: true })
            {
                Run("chown", ["-R", "-P", "root:root", "--", existing.Account.Home]);
            }

            Run("userdel", [name]);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"The account {name} could not be deleted: {ex.Message}", ex);
        }

        LogAccountDeleted(name);
        Deleted?.Invoke(name);
        return true;
    }

    private Entry? Find(string name)
    {
        string line;
        try
        {
            line = Run("getent", ["passwd", name]).Trim();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return null; // getent: no such key
        }

        // name:password:uid:gid:comment:home:shell
        var fields = line.Split(':');
        if (fields.Length != 7 || fields[0] != name
            || !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
            || !uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var gid))
        {
            throw new GameProcessException("server.account_unusable", $"The account {name} cannot be read from the system's user list.");
        }

        if (uid == 0 || gid == 0)
        {
            throw new GameProcessException("server.account_unusable", $"The account {name} has root's user or group id: Pulsar does not run a server as it.");
        }

        return new Entry(new LinuxAccount(name, uid, gid, fields[5]), fields[4], fields[6]);
    }

    private static string Nologin() => File.Exists("/usr/sbin/nologin") ? "/usr/sbin/nologin" : "/sbin/nologin";

    private static void ValidateName(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !ServerShortId.IsValid(name[Prefix.Length..]))
        {
            throw new ArgumentException($"'{name}' is not a server account's name.", nameof(name));
        }
    }

    private sealed record Entry(LinuxAccount Account, string Comment, string Shell);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the account {Account}")]
    private partial void LogAccountCreated(string account);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The account {Account} exists already")]
    private partial void LogAccountExists(string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted the account {Account}")]
    private partial void LogAccountDeleted(string account);
}
