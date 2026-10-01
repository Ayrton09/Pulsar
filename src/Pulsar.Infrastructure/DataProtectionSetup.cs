using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pulsar.Infrastructure;

/// <summary>
/// ASP.NET Core Data Protection (ADR-0011): encrypts the panel signing key, future secrets and the auth cookies.
/// The key ring lives in <c>DataProtection:KeysPath</c> (default: %ProgramData%\Pulsar\Panel\keys on Windows) and must
/// be backed up with the database. On Windows the keys are encrypted with machine DPAPI, which any process on the
/// machine can decrypt: the directory ACL (SYSTEM, Administrators and the account that created it) is what protects it.
/// </summary>
internal static class DataProtectionSetup
{
    public const string KeysPathKey = "DataProtection:KeysPath";

    public static void AddPulsarDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var path = configuration[KeysPathKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            path = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pulsar", "Panel", "keys")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulsar", "keys");
        }

        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists)
        {
            directory.Create();
            if (OperatingSystem.IsWindows())
            {
                Restrict(directory);
            }
        }

        var builder = services.AddDataProtection().SetApplicationName("Pulsar").PersistKeysToFileSystem(directory);
        if (OperatingSystem.IsWindows())
        {
            builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Restrict(DirectoryInfo directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     WindowsIdentity.GetCurrent().User!,
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        directory.SetAccessControl(security);
    }
}
