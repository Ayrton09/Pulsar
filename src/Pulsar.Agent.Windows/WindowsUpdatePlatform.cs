using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Pulsar.Agent.Core.Updates;

namespace Pulsar.Agent.Windows;

/// <summary>
/// Updating Pulsar on Windows (Phase 19): only the PulsarAgent service, running as SYSTEM, as the installer or the setups
/// installed it, updates; an agent the agent MSI maintains is updated with a newer package, and a development agent with
/// a new build. The release is unpacked in <c>%ProgramData%\Pulsar\Updates</c>, which only SYSTEM and Administrators can
/// change (the installer, which runs from there as SYSTEM, checks it again), and its installer runs detached, since it
/// stops this service.
/// </summary>
/// <param name="development">Whether this agent was enrolled with --dev.</param>
public sealed class WindowsUpdatePlatform(Func<bool> development) : IUpdatePlatform
{
    private const string PanelServiceKey = @"SYSTEM\CurrentControlSet\Services\PulsarPanel";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly SecurityIdentifier s_system = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public (string Code, string Message)? CannotUpdate()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
        {
            return ("update.not_service", "This agent does not run as the PulsarAgent service: update it with the setups.");
        }

        if (development())
        {
            return ("update.development", "A development agent is updated with a new build, not from the releases.");
        }

        return InstalledByPackage()
            ? ("update.msi_managed", "The agent MSI maintains this agent: update it with a newer package (docs/guides/update.md).")
            : null;
    }

    public bool HostsPanel()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PanelServiceKey);
        return key is not null;
    }

    public string PrepareFolder(string updatesRoot, string name)
    {
        var root = new DirectoryInfo(Path.GetFullPath(updatesRoot));
        if (root.Exists)
        {
            if (!IsPrivate(root))
            {
                throw new InvalidOperationException(
                    $"{root.FullName} could be changed by accounts other than SYSTEM and Administrators (or is a link): delete it, and update again.");
            }
        }
        else
        {
            // Made with its rules, so that nobody else holds it even for a moment.
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { s_system, s_administrators })
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }

            root.Create(security);
        }

        var folder = new DirectoryInfo(Path.Combine(root.FullName, name));
        if (folder.Exists)
        {
            throw new IOException($"{folder.FullName} exists already.");
        }

        folder.Create();
        return folder.FullName;
    }

    public void StartInstaller(string script, string workingDirectory)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Update", "-Unattended" })
        {
            start.ArgumentList.Add(argument);
        }

        // Not waited for: it stops this service, and outlives it (the agent's own process is in no job).
        using var _ = Process.Start(start) ?? throw new InvalidOperationException("The installer did not start.");
    }

    /// <summary>The installer's own rule (Test-PrivateFolder): not a link, owned by SYSTEM or Administrators, rules not inherited, allowing them alone.</summary>
    internal static bool IsPrivate(DirectoryInfo folder)
    {
        if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var security = folder.GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || (owner != s_system && owner != s_administrators)
            || !security.AreAccessRulesProtected)
        {
            return false;
        }

        var allowed = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => r.IdentityReference).ToList();
        return allowed.Count > 0 && allowed.All(sid => sid.Equals(s_system) || sid.Equals(s_administrators));
    }

    /// <summary>As Install-Pulsar.ps1's Get-AgentPackage: an installed "Pulsar Agent" product that Windows Installer maintains.</summary>
    private static bool InstalledByPackage()
    {
        using var products = Registry.LocalMachine.OpenSubKey(UninstallKey);
        if (products is null)
        {
            return false;
        }

        foreach (var name in products.GetSubKeyNames())
        {
            using var product = products.OpenSubKey(name);
            if (product?.GetValue("DisplayName") is "Pulsar Agent" && product.GetValue("WindowsInstaller") is int and 1)
            {
                return true;
            }
        }

        return false;
    }
}
