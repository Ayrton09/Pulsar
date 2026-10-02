using System.Security.AccessControl;
using System.Security.Principal;
using Pulsar.Agent.Core;

namespace Pulsar.Agent.Windows;

/// <summary>
/// The agent state directory: SYSTEM and Administrators only, inheritance from the parent disabled
/// (docs/architecture/03). Development installs also grant the current user, who runs the agent without elevation.
/// </summary>
public sealed class WindowsDirectorySecurity : IAgentDirectorySecurity
{
    public void Secure(string directory, bool development)
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
