using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Panel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authorization;
using Windows.Win32.Storage.FileSystem;

namespace Pulsar.Agent.Windows;

/// <summary>
/// The agent's folders: SYSTEM and Administrators only, inheritance from the parent disabled (docs/architecture/03).
/// Development installs also grant the current user, who runs the agent without elevation. What the agent's own folders
/// already hold is reset to inherit from them (owned by Administrators), a junction or symbolic link in them is refused,
/// and a production folder must not lie in a folder anyone else could replace: the SYSTEM service trusts what it loads
/// from there. Other content is never rewritten: in the state directory it keeps its rules (a development agent's
/// servers), in the servers root each server directory keeps its own, and a backups or servers root that holds someone
/// else's files is refused.
/// </summary>
public sealed class WindowsDirectorySecurity : IAgentDirectorySecurity
{
    // Junctions and symbolic links point elsewhere; other reparse points (deduplicated files, cloud placeholders) are
    // ordinary files and folders to whoever opens them, as in the file manager.
    private const uint NameSurrogate = 0x20000000;
    private const uint ReadAttributes = 0x0080;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;

    private static readonly EnumerationOptions s_everything = new() { AttributesToSkip = 0 };

    public void SecureState(string directory, IReadOnlyCollection<string> ownFolders, bool development)
    {
        ArgumentNullException.ThrowIfNull(ownFolders);
        directory = Checked(directory, development);
        var info = new DirectoryInfo(directory);
        var existed = info.Exists;
        List<DirectoryInfo> own = [];
        if (existed)
        {
            RefuseLink(info);
            // What sits at the top inherits the folder's rules: none of it may point elsewhere. Only the agent's own
            // folders are walked and reset; anything else keeps the rules it has.
            foreach (var entry in info.EnumerateFileSystemInfos("*", s_everything))
            {
                RefuseLink(entry);
            }

            own = [.. ownFolders.Select(f => new DirectoryInfo(Path.GetFullPath(f))).Where(f => f.Exists)];
            foreach (var folder in own)
            {
                RefuseLinks(folder);
            }
        }

        var owner = Apply(info, development);
        foreach (var folder in own)
        {
            // Top down: once a folder is reset, nobody else can put something else in place of what it holds.
            Inherit(folder, owner);
            Reset(folder, owner);
        }
    }

    public void SecureFolder(string directory, bool development)
    {
        directory = Checked(directory, development);
        var info = new DirectoryInfo(directory);
        var existed = info.Exists;
        if (existed)
        {
            RefuseLink(info);
            // Restricting the folder changes what its content inherits, and the agent cannot tell its files from planted
            // ones: a folder with content is taken only when it already is the agent's (an earlier enrollment).
            if (info.EnumerateFileSystemInfos("*", s_everything).Any() && !IsRestricted(info, development))
            {
                throw new AgentException($"""
                    {directory} already holds files other accounts can open. The agent keeps this folder to SYSTEM and Administrators, which would change who can open them, and it would trust whatever is there.
                    Choose an empty or new folder and enroll again.
                    """);
            }

            RefuseLinks(info);
        }

        var owner = Apply(info, development);
        if (existed)
        {
            Reset(info, owner);
        }
    }

    public void SecureServersRoot(string directory, bool development)
    {
        directory = Checked(directory, development);
        var info = new DirectoryInfo(directory);
        if (info.Exists)
        {
            RefuseLink(info);
            if (info.EnumerateFileSystemInfos("*", s_everything).Any() && !IsRestricted(info, development))
            {
                throw new AgentException($"""
                    {directory} already holds files other accounts can open. The agent keeps its servers root to SYSTEM and Administrators, which would change who can open them.
                    Choose an empty or new folder (--servers-root) and enroll again.
                    """);
            }
        }

        // Only the folder itself: each server directory in it keeps its own rules (the server's account is granted there).
        try
        {
            SetRules(directory, Rules(development));
        }
        catch (UnsafeFolderException ex)
        {
            throw new AgentException(ex.Message);
        }
    }

    /// <summary>Who, besides SYSTEM, Administrators and TrustedInstaller, could replace or change a folder above <paramref name="directory"/>.</summary>
    public static IReadOnlyList<string> FindReplaceableAncestors(string directory)
    {
        var findings = new List<string>();
        for (var ancestor = new DirectoryInfo(Path.GetFullPath(directory)).Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists)
            {
                ServiceBinarySecurity.Inspect(ancestor.FullName, ancestor.GetAccessControl(), ServiceBinarySecurity.AncestorRights(isRoot: ancestor.Parent is null),
                    findings);
            }
        }

        return findings;
    }

    /// <summary>
    /// Gives a folder <paramref name="security"/> (creating it with those rules when missing) through a handle opened
    /// without following a link: a junction or symbolic link in its place, whoever put it there and whenever, is refused,
    /// and the rules never land on what it points at. Inheritable rules reach what the folder holds, as with a path.
    /// </summary>
    /// <exception cref="IOException">It is a link or not a folder (<see cref="UnsafeFolderException"/>), or cannot be opened.</exception>
    internal static unsafe void SetRules(string directory, DirectorySecurity security)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists && !File.Exists(directory))
        {
            try
            {
                info.Create(security);
            }
            catch (IOException)
            {
                // Something took the name meanwhile (or a link to nowhere has it): opening it below tells which.
            }
        }

        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var dacl = new byte[descriptor.DiscretionaryAcl!.BinaryLength];
        descriptor.DiscretionaryAcl.GetBinaryForm(dacl, 0);
        var owner = new byte[descriptor.Owner?.BinaryLength ?? 0];
        descriptor.Owner?.GetBinaryForm(owner, 0);

        using var handle = PInvoke.CreateFile(directory, ReadAttributes | ReadControl | WriteDac | (owner.Length > 0 ? WriteOwner : 0),
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        FILE_ATTRIBUTE_TAG_INFO tag;
        if (handle.IsInvalid
            || !PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &tag,
                (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            throw new IOException($"Cannot open {directory}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        if (IsLink(tag.FileAttributes, tag.ReparseTag))
        {
            throw new UnsafeFolderException($"{directory} is a junction or link: it must be a plain folder.");
        }

        if ((tag.FileAttributes & (uint)FileAttributes.Directory) == 0)
        {
            throw new UnsafeFolderException($"{directory} is a file, not a folder.");
        }

        var sections = OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION
            | (descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
                ? OBJECT_SECURITY_INFORMATION.PROTECTED_DACL_SECURITY_INFORMATION
                : OBJECT_SECURITY_INFORMATION.UNPROTECTED_DACL_SECURITY_INFORMATION)
            | (owner.Length > 0 ? OBJECT_SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION : 0);
        fixed (byte* daclPointer = dacl)
        fixed (byte* ownerPointer = owner)
        {
            var error = PInvoke.SetSecurityInfo((HANDLE)handle.DangerousGetHandle(), SE_OBJECT_TYPE.SE_FILE_OBJECT, sections,
                owner.Length > 0 ? (PSID)ownerPointer : default, default, (ACL*)daclPointer, null);
            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                throw new UnauthorizedAccessException($"Cannot set who may open {directory}: {new Win32Exception((int)error).Message}");
            }
        }
    }

    /// <summary>A junction or symbolic link (a name surrogate), as opposed to an ordinary file or other reparse point.</summary>
    internal static bool IsLink(uint attributes, uint reparseTag) =>
        (attributes & (uint)FileAttributes.ReparsePoint) != 0 && (reparseTag & NameSurrogate) != 0;

    private static string Checked(string directory, bool development)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!development && FindReplaceableAncestors(directory) is { Count: > 0 } findings)
        {
            throw new AgentException($"""
                {directory} is inside folders other accounts could replace or change, so the state the service loads could be swapped:
                  {string.Join(Environment.NewLine + "  ", findings)}
                Remove those rights (or delete the folder if someone else created it) and enroll again.
                """);
        }

        return directory;
    }

    /// <summary>Gives the folder its rules (creating it if missing); returns the owner its content gets, if any.</summary>
    private static SecurityIdentifier? Apply(DirectoryInfo info, bool development)
    {
        var security = Rules(development);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        try
        {
            // By handle, opened without following a link: one that took the folder's place since it was checked is refused
            // instead of getting the rules (a path would lead to its target).
            SetRules(info.FullName, security);
        }
        catch (UnsafeFolderException ex)
        {
            throw new AgentException(ex.Message);
        }

        return development ? null : administrators;
    }

    /// <summary>SYSTEM and Administrators (and the current user, in development), nothing inherited from the parent.</summary>
    private static DirectorySecurity Rules(bool development)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        Allow(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Allow(security, administrators);
        if (development)
        {
            Allow(security, WindowsIdentity.GetCurrent().User!);
        }
        else
        {
            // Otherwise the creating administrator would own it and could be granted access back implicitly.
            security.SetOwner(administrators);
        }

        return security;
    }

    /// <summary>Only the agent's accounts may open it, and it does not inherit from its parent: what the agent made.</summary>
    private static bool IsRestricted(DirectoryInfo directory, bool development)
    {
        HashSet<SecurityIdentifier> trusted =
        [
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        ];
        if (development)
        {
            trusted.Add(WindowsIdentity.GetCurrent().User!);
        }

        var security = directory.GetAccessControl();
        return security.AreAccessRulesProtected
            && security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .All(r => r.AccessControlType == AccessControlType.Deny || trusted.Contains((SecurityIdentifier)r.IdentityReference));
    }

    private static void RefuseLink(FileSystemInfo entry)
    {
        if (IsLink(entry))
        {
            throw new AgentException($"{entry.FullName} is a junction or link: the agent's folders must hold plain files and folders.");
        }
    }

    private static void RefuseLinks(DirectoryInfo directory)
    {
        RefuseLink(directory);
        foreach (var entry in directory.EnumerateFileSystemInfos("*", s_everything))
        {
            if (entry is DirectoryInfo child)
            {
                RefuseLinks(child);
            }
            else
            {
                RefuseLink(entry);
            }
        }
    }

    /// <summary>Everything below <paramref name="directory"/> inherits its rules only (and is owned by <paramref name="owner"/>).</summary>
    private static void Reset(DirectoryInfo directory, SecurityIdentifier? owner)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", s_everything))
        {
            Inherit(entry, owner);
            if (entry is DirectoryInfo child)
            {
                Reset(child, owner);
            }
        }
    }

    /// <summary>
    /// The entry keeps no rules of its own: it inherits its folder's (and is owned by <paramref name="owner"/>). Through a
    /// handle opened without following a link and checked to be none, as <see cref="SetRules"/> does: a link that replaced
    /// the entry since it was listed would otherwise receive the rules through its path, and they would land on its target.
    /// </summary>
    private static unsafe void Inherit(FileSystemInfo entry, SecurityIdentifier? owner)
    {
        var ownerBytes = new byte[owner?.BinaryLength ?? 0];
        owner?.GetBinaryForm(ownerBytes, 0);
        // An empty, unprotected DACL: the explicit rules go and the parent's inheritable ones apply.
        var empty = new RawAcl(GenericAcl.AclRevision, 0);
        var dacl = new byte[empty.BinaryLength];
        empty.GetBinaryForm(dacl, 0);

        using var handle = PInvoke.CreateFile(entry.FullName, ReadAttributes | ReadControl | WriteDac | (owner is not null ? WriteOwner : 0),
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        FILE_ATTRIBUTE_TAG_INFO tag;
        if (handle.IsInvalid
            || !PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &tag,
                (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            throw new IOException($"Cannot open {entry.FullName}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        if (IsLink(tag.FileAttributes, tag.ReparseTag))
        {
            throw new AgentException($"{entry.FullName} is a junction or link: the agent's folders must hold plain files and folders.");
        }

        var sections = OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION | OBJECT_SECURITY_INFORMATION.UNPROTECTED_DACL_SECURITY_INFORMATION
            | (owner is not null ? OBJECT_SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION : 0);
        fixed (byte* daclPointer = dacl)
        fixed (byte* ownerPointer = ownerBytes)
        {
            var error = PInvoke.SetSecurityInfo((HANDLE)handle.DangerousGetHandle(), SE_OBJECT_TYPE.SE_FILE_OBJECT, sections,
                owner is not null ? (PSID)ownerPointer : default, default, (ACL*)daclPointer, null);
            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                throw new UnauthorizedAccessException($"Cannot reset who may open {entry.FullName}: {new Win32Exception((int)error).Message}");
            }
        }
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        entry.Refresh();
        return entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsLink((uint)entry.Attributes, ReparseTag(entry.FullName));
    }

    private static unsafe uint ReparseTag(string path)
    {
        using var handle = PInvoke.CreateFile(path, ReadAttributes,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        FILE_ATTRIBUTE_TAG_INFO info;
        if (handle.IsInvalid
            || !PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &info,
                (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            throw new AgentException($"Cannot tell what {path} is: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        return info.ReparseTag;
    }

    /// <summary>True when this process runs elevated (or as SYSTEM): production enrollment requires it.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem;
    }

    private static void Allow(DirectorySecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
}

/// <summary>A folder the agent was to secure is a junction, a link or a file.</summary>
public sealed class UnsafeFolderException(string message) : IOException(message);
