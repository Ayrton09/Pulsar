using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Server folders and accounts on Linux (docs/architecture/06, "Isolation"). The servers root is root's (others may only
/// pass through it, mode 0711); each server's folder is its own user's, closed to the others (0750, its own group): a
/// server cannot list or read another one. A development agent runs servers as the current user, who owns the folders
/// instead. Each provisioning checks again that neither the servers root nor the server's folder is a symbolic link.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxServerProvisioner(LinuxServerAccounts accounts, bool development, ILogger<LinuxServerProvisioner> logger)
    : IServerProvisioner
{
    private const uint ServerFolderMode = 0x1E8; // 0750
    private const uint RootMode = 0x1C9; // 0711

    /// <summary>The programs it runs, and how a test replaces them.</summary>
    internal Func<string, IReadOnlyList<string>, string> Run { get; init; } = (program, arguments) => SystemdUnits.Run(program, arguments, TimeSpan.FromMinutes(30));

    public ProvisionedServer Provision(string serversRoot, string shortId)
    {
        ServerShortId.Validate(shortId);
        var root = Root(serversRoot);
        var directory = Path.Combine(root, shortId);
        if (development)
        {
            Folder(root, RootMode, Libc.geteuid(), Libc.getegid());
            Folder(directory, ServerFolderMode, Libc.geteuid(), Libc.getegid());
            return new ProvisionedServer(directory, ProcessIdentity.CurrentUser);
        }

        Folder(root, RootMode, 0, 0);
        var name = LinuxServerAccounts.AccountName(shortId);
        var account = accounts.Ensure(name, shortId, directory);
        Folder(directory, ServerFolderMode, account.Uid, account.Gid);
        LogProvisioned(shortId, directory, name);
        return new ProvisionedServer(directory, ProcessIdentity.Account(name));
    }

    public ProvisionedServer Describe(string serversRoot, string shortId)
    {
        ServerShortId.Validate(shortId);
        return new ProvisionedServer(Path.Combine(Root(serversRoot), shortId),
            development ? ProcessIdentity.CurrentUser : ProcessIdentity.Account(LinuxServerAccounts.AccountName(shortId)));
    }

    public void Deprovision(string serversRoot, string shortId)
    {
        ServerShortId.Validate(shortId);
        var directory = Path.Combine(Root(serversRoot), shortId);
        if (!development)
        {
            // Nothing of the server may still run in its folder while it goes (or as its account once it is gone).
            _ = LinuxGameProcessManager.EndProcessesOf(accounts, LinuxServerAccounts.AccountName(shortId));
        }

        DeleteTree(directory);
        if (!development)
        {
            accounts.Delete(LinuxServerAccounts.AccountName(shortId));
        }

        LogDeprovisioned(shortId);
    }

    /// <summary>
    /// Deletes a folder its server's user filled (and could fill with links, or swap folders in while it goes): GNU
    /// <c>rm</c> walks it through the folders' own descriptors and never follows a link (it deletes the link), on this file
    /// system only, so the agent (root) never deletes anything outside the server's folder.
    /// </summary>
    internal void DeleteTree(string directory)
    {
        if (Libc.Lstat(directory) is not { } stat)
        {
            return;
        }

        if (stat.IsSymbolicLink)
        {
            // In root's servers root nobody else can put one; the link goes, never what it leads to.
            File.Delete(directory);
            return;
        }

        try
        {
            Run("rm", ["-r", "-f", "--one-file-system", "--preserve-root=all", "--", directory]);
        }
        catch (Win32Exception ex)
        {
            throw new GameProcessException("server.delete_failed", $"The server's folder {directory} could not be deleted: {ex.Message}", ex);
        }
    }

    private static string Root(string serversRoot)
    {
        if (!Path.IsPathFullyQualified(serversRoot))
        {
            throw new GameProcessException("server.invalid_servers_root", $"The servers root '{serversRoot}' must be an absolute path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(serversRoot));
    }

    /// <summary>The folder (made when missing) with this owner and mode, never through a link.</summary>
    private static void Folder(string directory, uint mode, uint uid, uint gid)
    {
        if (Libc.Lstat(directory) is null)
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (Libc.Lstat(directory) is not { IsDirectory: true, IsSymbolicLink: false })
        {
            throw new GameProcessException("server.unsafe_directory", $"{directory} is a symbolic link or not a folder: the agent never uses it.");
        }

        try
        {
            Libc.Lchown(directory, uid, gid);
            Libc.ChmodNoFollow(directory, mode);
        }
        catch (IOException ex)
        {
            throw new GameProcessException("server.unsafe_directory", ex.Message, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ShortId}: directory {Directory} ready for account {Account}")]
    private partial void LogProvisioned(string shortId, string directory, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ShortId}: removed from this machine")]
    private partial void LogDeprovisioned(string shortId);
}
