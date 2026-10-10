using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Windows;
using Pulsar.Contracts;

namespace Pulsar.Agent.Tests;

/// <summary>Runs only on Windows; elsewhere (the Linux CI leg) it is reported as skipped.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Windows only.";
        SkipUnless = nameof(IsWindows);
        SkipType = typeof(WindowsFactAttribute);
    }

    public static bool IsWindows => OperatingSystem.IsWindows();
}

/// <summary>
/// The Windows-specific parts of the agent against the real OS. Keys are created in the current user's store (machine
/// keys need elevation) and deleted afterwards; the machine-scope path is exercised by the agent run in Windows Sandbox.
/// </summary>
public sealed class WindowsAgentTests
{
    [WindowsFact]
    public void NodeKeysAreNonExportableP256KeysThatSignForTheProtocol()
    {
        var store = new CngNodeKeyStore(NullLogger<CngNodeKeyStore>.Instance);
        var name = $"PulsarAgentTest-{Guid.NewGuid():N}";
        NodeKeyReference reference;
        byte[] publicKey;
        using (var key = store.Create(name, NodeKeyScope.User))
        {
            reference = key.Reference;
            publicKey = key.PublicKey;
            Assert.Contains(reference.Storage, new[] { NodeKeyStorage.Tpm, NodeKeyStorage.Software });
            TestContext.Current.SendDiagnosticMessage($"Node key storage on this machine: {reference.Storage}");

            var data = ProtocolCrypto.SignedData(AgentProtocol.Purposes.Authenticate, [1, 2, 3]);
            Assert.True(ProtocolCrypto.Verify(publicKey, data, key.Sign(data)));
        }

        try
        {
            using var reopened = store.Open(reference);
            Assert.Equal(publicKey, reopened.PublicKey);

            using var cng = CngKey.Open(name, Provider(reference.Storage));
            Assert.Equal(CngExportPolicies.None, cng.ExportPolicy);
            using var ecdsa = new ECDsaCng(cng);
            Assert.ThrowsAny<CryptographicException>(() => ecdsa.ExportParameters(includePrivateParameters: true));
        }
        finally
        {
            store.Delete(reference);
        }

        Assert.ThrowsAny<CryptographicException>(() => store.Open(reference));
        store.Delete(reference); // deleting a missing key is a no-op
    }

    [WindowsFact]
    public async Task ADevelopmentAgentThatNeverHadAServerUnenrolls()
    {
        // Its state directory has no state folder: unenroll must not trip over the files it looks for there.
        var store = new CngNodeKeyStore(NullLogger<CngNodeKeyStore>.Instance);
        var root = Directory.CreateTempSubdirectory("pulsar-unenroll-").FullName;
        try
        {
            var paths = new AgentPaths(root);
            NodeKeyReference key;
            using (var created = store.Create($"PulsarAgentTest-{Guid.NewGuid():N}", NodeKeyScope.User))
            {
                key = created.Reference;
            }

            new AgentConfigStore(paths).Save(new AgentConfig(new Uri("https://panel.example.com/"), Guid.NewGuid(), Guid.NewGuid(), key, [], null,
                DateTimeOffset.UtcNow, Development: true));
            Directory.CreateDirectory(Path.Combine(paths.HostProgramsDirectory, "0123456789abcdef"));

            var exit = await AgentCommands.UnenrollAsync(CommandLine.Parse(["unenroll", "--data-dir", root], AgentCommands.Flags));

            Assert.Equal(0, exit);
            Assert.False(File.Exists(paths.ConfigFile));
            Assert.False(Directory.Exists(paths.HostProgramsDirectory));
            Assert.ThrowsAny<CryptographicException>(() => store.Open(key));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [WindowsFact]
    public void InventoryDescribesThisMachine()
    {
        var system = new WindowsNodeSystem(TimeProvider.System);

        var inventory = system.GetInventory(@"D:\PulsarServers");

        Assert.Equal(Environment.MachineName, inventory.Hostname);
        Assert.StartsWith("Windows", inventory.OsDescription, StringComparison.Ordinal);
        Assert.Matches(@"^\d+(\.\d+)?$", inventory.OsBuild);
        Assert.Equal(Environment.ProcessorCount, inventory.LogicalCores);
        Assert.True(inventory.MemoryTotalBytes > 512L * 1024 * 1024);
        Assert.Equal(@"D:\PulsarServers", inventory.ServersRoot);
        if (Environment.OSVersion.Version.Build >= 22000 && !inventory.OsDescription.Contains("Server", StringComparison.Ordinal))
        {
            Assert.StartsWith("Windows 11", inventory.OsDescription, StringComparison.Ordinal);
        }
    }

    [WindowsFact]
    public async Task MetricsAreWithinRange()
    {
        var system = new WindowsNodeSystem(TimeProvider.System);

        system.SampleMetrics();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var metrics = system.SampleMetrics();

        Assert.InRange(metrics.CpuPercent, 0, 100);
        Assert.InRange(metrics.MemoryUsedBytes, 1, metrics.MemoryTotalBytes);
        Assert.True(metrics.Uptime > TimeSpan.Zero);
    }

    [WindowsFact]
    public void DevelopmentStateDirectoryIsLimitedToSystemAdministratorsAndTheUser()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pulsar-agent-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            SecureState(directory, development: true);

            var security = new DirectoryInfo(directory).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToList();
            Assert.All(rules, r => Assert.False(r.IsInherited));
            Assert.Equal(
                new[]
                {
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    WindowsIdentity.GetCurrent().User!,
                }.Select(s => s.Value).Order(),
                rules.Select(r => r.IdentityReference.Value).Distinct().Order());

            // Still usable by the developer who enrolled it.
            File.WriteAllText(Path.Combine(directory, "probe.txt"), "ok");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void SecuringTheStateDirectoryAlsoResetsWhatIsAlreadyInIt()
    {
        // Someone created the folders first and gave themselves the configuration: the agent must not keep that.
        var directory = Path.Combine(Path.GetTempPath(), "pulsar-agent-acl-" + Guid.NewGuid().ToString("N"));
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        try
        {
            var config = Directory.CreateDirectory(Path.Combine(directory, "config"));
            var file = new FileInfo(Path.Combine(config.FullName, "agent.json"));
            File.WriteAllText(file.FullName, "{}");
            var open = new FileSecurity();
            open.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            open.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.FullControl, AccessControlType.Allow));
            open.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(open);

            SecureState(directory, development: true);

            var security = file.GetAccessControl();
            Assert.False(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.All(rules, r => Assert.True(r.IsInherited));
            Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(everyone));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void AStateDirectoryHoldingAJunctionIsRefused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pulsar-agent-acl-" + Guid.NewGuid().ToString("N"));
        var outside = Directory.CreateTempSubdirectory("pulsar-agent-outside-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "state"));
            Junction(Path.Combine(directory, "state", "servers"), outside.FullName);
            var before = outside.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            Assert.Throws<Core.Panel.AgentException>(() => SecureState(directory, development: true));

            Assert.Equal(before, outside.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally
        {
            Directory.Delete(Path.Combine(directory, "state", "servers"));
            Directory.Delete(directory, recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [WindowsFact]
    public void AProductionStateDirectoryInsideAFolderOthersCanReplaceIsRefused()
    {
        // The temp folder belongs to the current user, who could put another folder in the state directory's place.
        var directory = Path.Combine(Path.GetTempPath(), "pulsar-agent-acl-" + Guid.NewGuid().ToString("N"), "Agent");

        var refused = Assert.Throws<Core.Panel.AgentException>(() => SecureState(directory, development: false));

        Assert.Contains(Path.GetTempPath().TrimEnd('\\'), refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.GetDirectoryName(directory))); // nothing was created
        Assert.Empty(WindowsDirectorySecurity.FindReplaceableAncestors(Path.Combine(Environment.SystemDirectory, "config")));
    }

    [WindowsFact]
    public void SecuringTheStateDirectoryLeavesWhatIsNotTheAgentsAsItWas()
    {
        // A development agent keeps its servers in the state directory; their folders have rules of their own.
        var directory = Path.Combine(Path.GetTempPath(), "pulsar-agent-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var server = Directory.CreateDirectory(Path.Combine(directory, "servers", "k3x7m2qa"));
            var file = new FileInfo(Path.Combine(server.FullName, "server.cfg"));
            File.WriteAllText(file.FullName, "hostname test");
            file.SetAccessControl(Open());
            var before = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            SecureState(directory, development: true);

            Assert.Equal(before, file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void ABackupsRootHoldingSomeoneElsesFilesIsRefusedAndLeftAsItWas()
    {
        var directory = Directory.CreateTempSubdirectory("pulsar-backups-root-");
        try
        {
            var file = new FileInfo(Path.Combine(directory.FullName, "photos.zip"));
            File.WriteAllText(file.FullName, "the user's own");
            var folderBefore = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            var fileBefore = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);

            var refused = Assert.Throws<Core.Panel.AgentException>(() => new WindowsDirectorySecurity().SecureFolder(directory.FullName, development: true));

            Assert.Contains(directory.FullName, refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(folderBefore, directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            Assert.Equal(fileBefore, file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [WindowsFact]
    public void AnEmptyBackupsRootIsSecuredAndStaysAcceptedOnceItHoldsBackups()
    {
        var directory = Directory.CreateTempSubdirectory("pulsar-backups-root-");
        try
        {
            new WindowsDirectorySecurity().SecureFolder(directory.FullName, development: true);
            Assert.True(directory.GetAccessControl().AreAccessRulesProtected);
            Directory.CreateDirectory(Path.Combine(directory.FullName, "k3x7m2qa"));
            File.WriteAllText(Path.Combine(directory.FullName, "k3x7m2qa", "backup.zip"), "zip");

            // Enrolled again: the folder is already the agent's.
            new WindowsDirectorySecurity().SecureFolder(directory.FullName, development: true);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [WindowsFact]
    public void AReparsePointThatIsNotALinkIsAnOrdinaryFile()
    {
        // Deduplicated files and cloud placeholders are reparse points too, but they do not point elsewhere.
        var directory = Path.Combine(Path.GetTempPath(), "pulsar-agent-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = Directory.CreateDirectory(Path.Combine(directory, "config"));
            var file = Path.Combine(config.FullName, "agent.json");
            File.WriteAllText(file, "{}");
            SetThirdPartyReparsePoint(file);
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint));

            SecureState(directory, development: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0xA000000Cu, true)] // symbolic link
    [InlineData(0xA0000003u, true)] // junction (mount point)
    [InlineData(0x80000013u, false)] // deduplicated file
    [InlineData(0x9000101Au, false)] // OneDrive placeholder
    [InlineData(0x00001234u, false)] // a third party's
    public void OnlyNameSurrogatesAreLinks(uint tag, bool link) =>
        Assert.Equal(link, WindowsDirectorySecurity.IsLink((uint)FileAttributes.ReparsePoint, tag));

    [WindowsFact]
    public void TheRealProgramDataPassesTheAncestorCheckOfAProductionEnrollment()
    {
        // Read only: a production enrollment's state goes to %ProgramData%\Pulsar\Agent. A child that does not exist is
        // used (an earlier run may have made %ProgramData%\Pulsar with other rights), so only %ProgramData% and its drive
        // root are inspected, as they come on a stock Windows (Users may create folders there, not replace them).
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var directory = Path.Combine(programData, "Pulsar-" + Guid.NewGuid().ToString("N"), "Agent");

        Assert.Empty(WindowsDirectorySecurity.FindReplaceableAncestors(directory));
        Assert.False(Directory.Exists(Path.GetDirectoryName(directory)));
    }

    private static void SecureState(string directory, bool development) =>
        new WindowsDirectorySecurity().SecureState(directory, new Core.AgentPaths(directory).OwnFolders, development);

    private static FileSecurity Open()
    {
        var open = new FileSecurity();
        open.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        open.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl,
            AccessControlType.Allow));
        open.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return open;
    }

    /// <summary>A reparse point with a made-up third-party tag (no name surrogate bit), as a filter driver would set.</summary>
    private static void SetThirdPartyReparsePoint(string file)
    {
        const uint fsctlSetReparsePoint = 0x000900A4;
        using var handle = NativeTest.CreateFileW(file, 0x40000000 /* GENERIC_WRITE */, 0, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x00200000 /* FILE_FLAG_OPEN_REPARSE_POINT */, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        var buffer = new byte[24 + 4];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), 0x00001234u); // ReparseTag
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)4); // ReparseDataLength
        Guid.NewGuid().TryWriteBytes(buffer.AsSpan(8)); // ReparseGuid
        Assert.True(NativeTest.DeviceIoControl(handle, fsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            $"FSCTL_SET_REPARSE_POINT failed: {Marshal.GetLastPInvokeError()}");
    }

    private static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private static CngProvider Provider(string storage) =>
        storage == NodeKeyStorage.Tpm ? new CngProvider("Microsoft Platform Crypto Provider") : CngProvider.MicrosoftSoftwareKeyStorageProvider;
}

public sealed class ServiceBinarySecurityTests
{
    [WindowsFact]
    public void AnExecutableInAUserWritableFolderCannotHostTheService()
    {
        var directory = Directory.CreateTempSubdirectory("pulsar-service-binary-");
        try
        {
            var executable = Path.Combine(directory.FullName, "PulsarAgent.exe");
            File.WriteAllBytes(executable, []);

            var findings = Windows.ServiceBinarySecurity.FindUntrustedWriters(executable);

            Assert.Contains(findings, f => f.Contains(directory.FullName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [WindowsFact]
    public void SystemExecutablesAreSafe()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        Assert.Empty(Windows.ServiceBinarySecurity.FindUntrustedWriters(executable));
    }
}

public sealed class ServiceBinaryAclRulesTests
{
    private static readonly SecurityIdentifier s_authenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [WindowsFact]
    public void ModifyOnADriveRootDoesNotLetAnyoneReplaceFoldersBelowIt()
    {
        // As in Windows Sandbox: Authenticated Users have "Modify" on C:\ itself.
        var root = Acl(new FileSystemAccessRule(s_authenticatedUsers, FileSystemRights.Modify, AccessControlType.Allow));

        Assert.Empty(Inspect(root, Windows.ServiceBinarySecurity.AncestorRights(isRoot: true)));
        Assert.NotEmpty(Inspect(root, Windows.ServiceBinarySecurity.AncestorRights(isRoot: false)));
    }

    [WindowsFact]
    public void DeleteChildOnAnyAncestorIsUnsafe()
    {
        var folder = Acl(new FileSystemAccessRule(s_authenticatedUsers, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));

        Assert.NotEmpty(Inspect(folder, Windows.ServiceBinarySecurity.AncestorRights(isRoot: true)));
    }

    [WindowsFact]
    public void InheritOnlyEntriesDoNotApplyToTheFolderItself()
    {
        var folder = Acl(new FileSystemAccessRule(s_authenticatedUsers, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));

        Assert.Empty(Inspect(folder, Windows.ServiceBinarySecurity.ExecutableRights));
    }

    [WindowsFact]
    public void WriteOnTheExecutableFolderIsUnsafeButReadIsNot()
    {
        var readable = Acl(new FileSystemAccessRule(s_authenticatedUsers, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        var writable = Acl(new FileSystemAccessRule(s_authenticatedUsers, FileSystemRights.Write, AccessControlType.Allow));

        Assert.Empty(Inspect(readable, Windows.ServiceBinarySecurity.ExecutableRights));
        Assert.Single(Inspect(writable, Windows.ServiceBinarySecurity.ExecutableRights));
    }

    private static DirectorySecurity Acl(FileSystemAccessRule rule)
    {
        var security = new DirectorySecurity();
        security.SetOwner(s_administrators);
        security.AddAccessRule(rule);
        return security;
    }

    private static List<string> Inspect(DirectorySecurity security, FileSystemRights dangerous)
    {
        var findings = new List<string>();
        Windows.ServiceBinarySecurity.Inspect(@"C:\Example", security, dangerous, findings);
        return findings;
    }
}

internal static partial class NativeTest
{
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, byte[] input, int inputLength, nint output,
        int outputLength, out int returned, nint overlapped);
}
