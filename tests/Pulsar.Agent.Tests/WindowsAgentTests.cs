using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
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
            new WindowsDirectorySecurity().Secure(directory, development: true);

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
