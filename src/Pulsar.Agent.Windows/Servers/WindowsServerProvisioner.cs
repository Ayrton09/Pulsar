using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// Server directories and accounts on Windows (docs/architecture/06, "Isolation"). The servers root is open to SYSTEM
/// and Administrators only; each server directory adds Modify for the server's own account, so a server cannot list or
/// read another one. OWNER RIGHTS is limited to Modify too: a server owns the files it creates but cannot change who
/// may read them. A development agent runs servers as the current user, who is granted the directories instead. Each
/// provisioning checks again that neither the servers root nor the server directory is a junction or link.
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

        // The first logon creates the account's profile, which can take seconds: done here (an operation) rather than at
        // the first start, and a machine policy that stops the account from logging on shows up now.
        using (accounts.LogOn(name))
        {
        }

        LogProvisioned(shortId, directory, name);
        return new ProvisionedServer(directory, ProcessIdentity.Account(name));
    }

    public ProvisionedServer Describe(string serversRoot, string shortId)
    {
        ServerShortId.Validate(shortId);
        return new ProvisionedServer(Path.Combine(Root(serversRoot), shortId),
            development ? ProcessIdentity.CurrentUser : ProcessIdentity.Account(WindowsServerAccounts.AccountName(shortId)));
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
    /// Deletes a directory tree that a server (an untrusted account) could have filled with junctions and symbolic links,
    /// or could swap folders in while it is deleted: every entry is opened by handle without following a link, checked to
    /// still be inside, and deleted through that handle (links themselves, never their targets), so the agent (SYSTEM)
    /// cannot be tricked into deleting files outside the server directory. Read-only files are deleted too.
    /// </summary>
    /// <param name="beforeListing">Tests: runs before each directory's entries are listed.</param>
    internal static void DeleteTree(string directory, Action<string>? beforeListing = null) =>
        WindowsServerFileManager.DeleteDirectoryTree(directory, beforeListing);

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

        // Through a handle that does not follow a link: a junction a user put in the place of the servers root (before the
        // first server, if its parent lets them) or a server folder never gets these rules or this owner on its target.
        try
        {
            WindowsDirectorySecurity.SetRules(directory, security);
        }
        catch (UnsafeFolderException ex)
        {
            throw new GameProcessException("server.unsafe_directory", ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ShortId}: directory {Directory} ready for account {Account}")]
    private partial void LogProvisioned(string shortId, string directory, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ShortId}: removed from this machine")]
    private partial void LogDeprovisioned(string shortId);
}
