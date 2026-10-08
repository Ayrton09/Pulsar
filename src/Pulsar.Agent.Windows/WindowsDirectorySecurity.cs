using System.Security.AccessControl;
using System.Security.Principal;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Panel;

namespace Pulsar.Agent.Windows;

/// <summary>
/// The agent state directory: SYSTEM and Administrators only, inheritance from the parent disabled
/// (docs/architecture/03). Development installs also grant the current user, who runs the agent without elevation.
/// What the directory already holds is reset to inherit from it (owned by Administrators), a junction or link in it is
/// refused, and a production directory must not lie in a folder anyone else could replace: the SYSTEM service trusts
/// what it loads from there.
/// </summary>
public sealed class WindowsDirectorySecurity : IAgentDirectorySecurity
{
    public void Secure(string directory, bool development)
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

        var existing = new DirectoryInfo(directory);
        if (existing.Exists)
        {
            RefuseLinks(existing);
        }

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

        var info = new DirectoryInfo(directory);
        if (info.Exists)
        {
            info.SetAccessControl(security);
            // Top down: once a folder is reset, nobody else can put something else in place of what it holds.
            Reset(info, development ? null : administrators);
        }
        else
        {
            info.Create(security);
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

    private static void RefuseLinks(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new AgentException($"{directory.FullName} is a junction or link: the agent's state must be a plain folder.");
        }

        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry is DirectoryInfo child)
            {
                RefuseLinks(child);
            }
            else if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new AgentException($"{entry.FullName} is a link: the agent's state must hold plain files.");
            }
        }
    }

    /// <summary>Everything below <paramref name="directory"/> inherits its rules only (and is owned by <paramref name="owner"/>).</summary>
    private static void Reset(DirectoryInfo directory, SecurityIdentifier? owner)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            // Checked again right before: rules set on a link would land on its target.
            entry.Refresh();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new AgentException($"{entry.FullName} is a junction or link: the agent's state must hold plain files and folders.");
            }

            if (entry is DirectoryInfo child)
            {
                var security = new DirectorySecurity();
                Inherit(security, owner);
                child.SetAccessControl(security);
                Reset(child, owner);
            }
            else
            {
                var security = new FileSecurity();
                Inherit(security, owner);
                ((FileInfo)entry).SetAccessControl(security);
            }
        }
    }

    private static void Inherit(FileSystemSecurity security, SecurityIdentifier? owner)
    {
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        if (owner is not null)
        {
            security.SetOwner(owner);
        }
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
