using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
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
/// Elsewhere (or to move the key ring between machines) <c>DataProtection:CertificatePath</c> names a PFX file whose
/// certificate encrypts the keys (its password in <c>DataProtection:CertificatePassword</c>); without one, the keys are
/// protected only by the directory's permissions (owner only).
/// </summary>
internal static class DataProtectionSetup
{
    public const string KeysPathKey = "DataProtection:KeysPath";
    public const string CertificatePathKey = "DataProtection:CertificatePath";
    public const string CertificatePasswordKey = "DataProtection:CertificatePassword";

    /// <summary>The key ring's folder: <c>DataProtection:KeysPath</c>, or the default for this system.</summary>
    public static string KeysPath(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = configuration[KeysPathKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            path = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pulsar", "Panel", "keys")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulsar", "keys");
        }

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Creates <paramref name="directory"/> for the panel's account alone (and SYSTEM and Administrators on Windows; the
    /// owner only elsewhere), or makes an existing one so.
    /// </summary>
    public static void CreatePrivate(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (OperatingSystem.IsWindows())
        {
            directory.Create();
            Restrict(directory);
        }
        else
        {
            Directory.CreateDirectory(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void AddPulsarDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var directory = new DirectoryInfo(KeysPath(configuration));
        if (!directory.Exists)
        {
            if (OperatingSystem.IsWindows())
            {
                directory.Create();
                Restrict(directory);
            }
            else
            {
                Directory.CreateDirectory(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var builder = services.AddDataProtection().SetApplicationName("Pulsar").PersistKeysToFileSystem(directory);
        var certificatePath = configuration[CertificatePathKey];
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, configuration[CertificatePasswordKey]);
            builder.ProtectKeysWithCertificate(certificate);
        }
        else if (OperatingSystem.IsWindows())
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
