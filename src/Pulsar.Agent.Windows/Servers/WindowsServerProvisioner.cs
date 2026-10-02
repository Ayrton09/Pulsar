using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// Server directories and accounts on Windows (docs/architecture/06, "Isolation"). The servers root is open to SYSTEM
/// and Administrators only; each server directory adds Modify for the server's own account, so a server cannot list or
/// read another one. OWNER RIGHTS is limited to Modify too: a server owns the files it creates but cannot change who
/// may read them. A development agent runs servers as the current user, who is granted the directories instead.
/// </summary>
public sealed partial class WindowsServerProvisioner(WindowsServerAccounts accounts, bool development, ILogger<WindowsServerProvisioner> logger)
    : IServerProvisioner
{
    private static readonly SecurityIdentifier s_system = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier s_ownerRights = new("S-1-3-4");

    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public ProvisionedServer Provision(string serversRoot, string shortId)
    {
        ServerShortId.Validate(shortId);
        var root = Root(serversRoot);
        Protect(root, null);

        var directory = Path.Combine(root, shortId);
        if (development)
        {
            Protect(directory, null);
            return new ProvisionedServer(directory, ProcessIdentity.CurrentUser);
        }

        if (WindowsServerAccounts.IsDomainController())
        {
            throw new GameProcessException("server.domain_controller",
                "Game servers cannot run on a domain controller: it has no local accounts to isolate them with.");
        }

        var name = WindowsServerAccounts.AccountName(shortId);
        var sid = accounts.Ensure(name, $"Pulsar game server {shortId} (managed by the Pulsar agent)");
        Protect(directory, sid);
        LogProvisioned(shortId, directory, name);
        return new ProvisionedServer(directory, ProcessIdentity.Account(name));
    }

    public void Deprovision(string serversRoot, string shortId)
    {
        ServerShortId.Validate(shortId);
        var directory = Path.Combine(Root(serversRoot), shortId);
        DeleteTree(directory);

        if (!development)
        {
            accounts.Delete(WindowsServerAccounts.AccountName(shortId));
        }

        LogDeprovisioned(shortId);
    }

    /// <summary>
    /// Deletes a directory tree that a server (an untrusted account) could have filled with junctions and symbolic links:
    /// links are removed themselves, never descended into, so the agent (SYSTEM) cannot be tricked into deleting files
    /// outside the server directory. Read-only attributes are cleared on the way.
    /// </summary>
    internal static void DeleteTree(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            return;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete();
            return;
        }

        foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            if (entry is DirectoryInfo child && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteTree(child.FullName);
            }
            else
            {
                // A file, or a link (to a directory or a file): RemoveDirectory/DeleteFile remove the link only.
                entry.Delete();
            }
        }

        info.Attributes &= ~FileAttributes.ReadOnly;
        info.Delete();
    }

    private static string Root(string serversRoot)
    {
        if (!Path.IsPathFullyQualified(serversRoot))
        {
            throw new GameProcessException("server.invalid_servers_root", $"The servers root '{serversRoot}' must be an absolute path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(serversRoot));
    }

    private void Protect(string directory, SecurityIdentifier? account)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(s_system, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(s_administrators, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        if (development)
        {
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, Inherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        else
        {
            security.SetOwner(s_administrators);
        }

        if (account is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(s_ownerRights, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var info = new DirectoryInfo(directory);
        if (info.Exists)
        {
            info.SetAccessControl(security);
        }
        else
        {
            info.Create(security);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ShortId}: directory {Directory} ready for account {Account}")]
    private partial void LogProvisioned(string shortId, string directory, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ShortId}: directory and account removed")]
    private partial void LogDeprovisioned(string shortId);
}
