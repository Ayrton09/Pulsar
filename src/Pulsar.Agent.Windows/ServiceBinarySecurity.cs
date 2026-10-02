using System.Security.AccessControl;
using System.Security.Principal;

namespace Pulsar.Agent.Windows;

/// <summary>
/// The service runs as SYSTEM, so whoever can replace its executable gets SYSTEM. Before installing it, the agent checks
/// that only SYSTEM, Administrators and TrustedInstaller can modify the executable and its folder, or rename any folder
/// above it (which would let someone put another folder in its place). A download folder, or a folder created directly
/// under C:\ (which inherits "Modify" for Authenticated Users), fails the check; C:\Program Files\Pulsar\Agent passes.
/// </summary>
public static class ServiceBinarySecurity
{
    private const FileSystemRights ModifyRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // Renaming or replacing a folder needs one of these on the folder itself or delete-child on its parent.
    private const FileSystemRights ReplaceRights = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier[] s_trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
    ];

    /// <summary>Who, besides the trusted principals, could tamper with the service executable; empty when it is safe.</summary>
    public static IReadOnlyList<string> FindUntrustedWriters(string executablePath)
    {
        var findings = new List<string>();
        var file = new FileInfo(executablePath);
        Inspect(file.FullName, file.GetAccessControl(), ModifyRights, findings);

        var directory = file.Directory;
        if (directory is not null)
        {
            Inspect(directory.FullName, directory.GetAccessControl(), ModifyRights, findings);
            for (var ancestor = directory.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                Inspect(ancestor.FullName, ancestor.GetAccessControl(), AncestorRights(isRoot: ancestor.Parent is null), findings);
            }
        }

        return findings;
    }

    /// <summary>
    /// Rights on a folder above the executable that would let someone put another folder in its place. A drive root
    /// cannot be renamed or deleted, so "delete" there is harmless; some images (Windows Sandbox) grant
    /// Authenticated Users "Modify" on C:\ itself, which includes it but not delete-child.
    /// </summary>
    internal static FileSystemRights AncestorRights(bool isRoot) =>
        isRoot ? ReplaceRights & ~FileSystemRights.Delete : ReplaceRights;

    internal static FileSystemRights ExecutableRights => ModifyRights;

    internal static void Inspect(string path, FileSystemSecurity security, FileSystemRights dangerous, List<string> findings)
    {
        // The owner can always rewrite the DACL.
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !IsTrusted(owner))
        {
            findings.Add($"{path} is owned by {Name(owner)}");
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            // Inherit-only entries (e.g. CREATOR OWNER) apply to children created later, not to this object.
            if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                || (rule.FileSystemRights & dangerous) == 0 || IsTrusted((SecurityIdentifier)rule.IdentityReference))
            {
                continue;
            }

            findings.Add($"{Name((SecurityIdentifier)rule.IdentityReference)} can modify {path}");
        }
    }

    private static bool IsTrusted(SecurityIdentifier sid) => s_trusted.Contains(sid);

    private static string Name(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
