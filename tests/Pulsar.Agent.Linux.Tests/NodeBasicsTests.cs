using System.Security.Cryptography;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>The machine, the node key, the state's encryption and the agent's folders on Linux.</summary>
public sealed class NodeBasicsTests
{
    private const UnixFileMode Mode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    [LinuxFact]
    public void TheMachineIsReadFromProcAndOsRelease()
    {
        var root = LinuxOnly.NewFolder();
        Write(root, "etc/os-release", "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.3 LTS\"\nID=ubuntu\n");
        Write(root, "proc/sys/kernel/osrelease", "6.8.0-48-generic\n");
        Write(root, "proc/cpuinfo", "processor\t: 0\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz\n");
        Write(root, "proc/meminfo", "MemTotal:       16303908 kB\nMemFree:         1000000 kB\nMemAvailable:    8151954 kB\n");
        Write(root, "proc/stat", "cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 50 0 50 400 0 0 0 0 0 0\n");
        Write(root, "proc/uptime", "3600.55 7000.10\n");
        var system = new LinuxNodeSystem(TimeProvider.System, root);

        var inventory = system.GetInventory("/var/lib/pulsar/servers");
        Assert.Equal(("Ubuntu 24.04.3 LTS", "6.8.0-48-generic", "Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", 16303908L * 1024, "linux-x64"),
            (inventory.OsDescription, inventory.OsBuild, inventory.CpuModel, inventory.MemoryTotalBytes, inventory.Platform));

        var first = system.SampleMetrics();
        Assert.Equal(20.0, first.CpuPercent);
        Assert.Equal((16303908L - 8151954) * 1024, first.MemoryUsedBytes);
        Assert.Equal(3600, (int)first.Uptime.TotalSeconds);

        // The next sample is the share since the first one.
        Write(root, "proc/stat", "cpu  150 0 150 900 0 0 0 0 0 0\n");
        Assert.Equal(50.0, system.SampleMetrics().CpuPercent);
    }

    [LinuxFact]
    public void AMachineWithoutOsReleaseIsJustLinux()
    {
        var root = LinuxOnly.NewFolder();
        Write(root, "proc/meminfo", "MemTotal: 1024 kB\n");

        Assert.Equal("Linux", new LinuxNodeSystem(TimeProvider.System, root).GetInventory(null).OsDescription);
    }

    [LinuxFact]
    public void TheMachinesOwnFilesAreRead()
    {
        var inventory = new LinuxNodeSystem(TimeProvider.System).GetInventory(null);

        Assert.True(inventory.MemoryTotalBytes > 0);
        Assert.NotEqual("Linux", inventory.OsDescription);
    }

    [LinuxFact]
    public void ANodeKeyIsAFileOnlyItsOwnerReadsAndItSigns()
    {
        var store = new FileNodeKeyStore(Path.Combine(LinuxOnly.NewFolder(), "keys"));
        var name = $"PulsarAgent-{Guid.NewGuid():N}";

        using var key = store.Create(name, NodeKeyScope.Machine);

        Assert.Equal(new NodeKeyReference(name, NodeKeyScope.Machine, NodeKeyStorage.File), key.Reference);
        var file = Path.Combine(store.Directory, name + ".p8");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(store.Directory));
        Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp"));

        using var opened = store.Open(key.Reference);
        var data = "signed"u8.ToArray();
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(opened.PublicKey, out _);
        Assert.True(verifier.VerifyData(data, key.Sign(data), HashAlgorithmName.SHA256, ProtocolCrypto.SignatureFormat));
        using var certificate = opened.CreateCertificate();
        Assert.True(certificate.HasPrivateKey);

        store.Delete(key.Reference);
        Assert.False(File.Exists(file));
        Assert.Throws<CryptographicException>(() => store.Open(key.Reference));
    }

    [LinuxFact]
    public void AKeyOthersCanReadIsNotOpened()
    {
        var store = new FileNodeKeyStore(Path.Combine(LinuxOnly.NewFolder(), "keys"));
        using var key = store.Create($"PulsarAgent-{Guid.NewGuid():N}", NodeKeyScope.Machine);
        File.SetUnixFileMode(Path.Combine(store.Directory, key.Reference.Name + ".p8"), Mode644);

        var refused = Assert.Throws<CryptographicException>(() => store.Open(key.Reference));
        Assert.Contains("other users", refused.Message, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void AKeyThatIsALinkIsNotOpened()
    {
        var store = new FileNodeKeyStore(Path.Combine(LinuxOnly.NewFolder(), "keys"));
        using var key = store.Create($"PulsarAgent-{Guid.NewGuid():N}", NodeKeyScope.Machine);
        var file = Path.Combine(store.Directory, key.Reference.Name + ".p8");
        var elsewhere = Path.Combine(LinuxOnly.NewFolder(), "key.p8");
        File.Move(file, elsewhere);
        File.CreateSymbolicLink(file, elsewhere);

        Assert.Throws<CryptographicException>(() => store.Open(key.Reference));
    }

    [LinuxTheory]
    [InlineData("../PulsarAgent-00000000000000000000000000000000")]
    [InlineData("PulsarAgent-x")]
    [InlineData("other")]
    public void OnlyTheAgentsKeyNamesAreFiles(string name)
    {
        var store = new FileNodeKeyStore(Path.Combine(LinuxOnly.NewFolder(), "keys"));

        Assert.Throws<CryptographicException>(() => store.Create(name, NodeKeyScope.Machine));
        Assert.Throws<CryptographicException>(() => store.Open(new NodeKeyReference(name, NodeKeyScope.Machine, NodeKeyStorage.File)));
    }

    [LinuxFact]
    public void TheStateIsEncryptedWithAKeyOnlyTheAgentReads()
    {
        var keyFile = Path.Combine(LinuxOnly.NewFolder(), "config", "state.key");
        var protector = new FileKeyStateProtector(keyFile);
        var secret = "rcon password"u8.ToArray();

        var protectedData = protector.Protect(secret);

        Assert.DoesNotContain("rcon", System.Text.Encoding.UTF8.GetString(protectedData), StringComparison.Ordinal);
        Assert.Equal(secret, new FileKeyStateProtector(keyFile).Unprotect(protectedData));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
        Assert.Equal(32, new FileInfo(keyFile).Length);

        protectedData[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(protectedData));
    }

    [LinuxFact]
    public void WithoutItsKeyTheStateIsNotReadAndNoNewKeyIsMade()
    {
        var folder = LinuxOnly.NewFolder();
        var data = new FileKeyStateProtector(Path.Combine(folder, "state.key")).Protect([1, 2, 3]);
        File.Delete(Path.Combine(folder, "state.key"));

        Assert.Throws<CryptographicException>(() => new FileKeyStateProtector(Path.Combine(folder, "state.key")).Unprotect(data));
        Assert.False(File.Exists(Path.Combine(folder, "state.key")));
    }

    [LinuxFact]
    public void AStateKeyOthersCanReadIsRefused()
    {
        var keyFile = Path.Combine(LinuxOnly.NewFolder(), "state.key");
        var data = new FileKeyStateProtector(keyFile).Protect([1]);
        File.SetUnixFileMode(keyFile, Mode644);

        Assert.Throws<CryptographicException>(() => new FileKeyStateProtector(keyFile).Unprotect(data));
    }

    [LinuxFact]
    public void TheStateFolderAndTheAgentsOwnFoldersAreClosedToOthers()
    {
        var state = Path.Combine(LinuxOnly.NewFolder(), "agent");
        var config = Path.Combine(state, "config");
        Directory.CreateDirectory(Path.Combine(config, "keys"));
        File.WriteAllText(Path.Combine(config, "agent.json"), "{}");
        File.SetUnixFileMode(Path.Combine(config, "agent.json"), Mode644);
        // Something else in the state folder keeps its own mode (a development agent's servers).
        var servers = Path.Combine(state, "servers");
        Directory.CreateDirectory(servers);
        File.SetUnixFileMode(servers, Mode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        new LinuxDirectorySecurity().SecureState(state, [config], development: true);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(state));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(config));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(config, "agent.json")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(config, "keys")));
        Assert.True(File.GetUnixFileMode(servers).HasFlag(UnixFileMode.OtherExecute));
    }

    [LinuxFact]
    public void ALinkInTheAgentsFoldersIsRefused()
    {
        var state = Path.Combine(LinuxOnly.NewFolder(), "agent");
        var config = Path.Combine(state, "config");
        Directory.CreateDirectory(config);
        File.CreateSymbolicLink(Path.Combine(config, "agent.json"), "/etc/passwd");

        Assert.Throws<AgentException>(() => new LinuxDirectorySecurity().SecureState(state, [config], development: true));
        Assert.Equal("/etc/passwd", new FileInfo(Path.Combine(config, "agent.json")).LinkTarget);
    }

    [LinuxFact]
    public void AStateFolderThatIsALinkIsRefused()
    {
        var target = LinuxOnly.NewFolder();
        var state = Path.Combine(LinuxOnly.NewFolder(), "agent");
        Directory.CreateSymbolicLink(state, target);

        Assert.Throws<AgentException>(() => new LinuxDirectorySecurity().SecureState(state, [], development: true));
    }

    [LinuxFact]
    public void AFolderWithSomeoneElsesFilesIsNotTakenForBackups()
    {
        var backups = LinuxOnly.NewFolder();
        File.WriteAllText(Path.Combine(backups, "theirs.txt"), "x");
        File.SetUnixFileMode(backups, Mode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        Assert.Throws<AgentException>(() => new LinuxDirectorySecurity().SecureFolder(backups, development: true));

        var empty = Path.Combine(LinuxOnly.NewFolder(), "backups");
        new LinuxDirectorySecurity().SecureFolder(empty, development: true);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(empty));
    }

    [LinuxFact]
    public void OthersOnlyPassThroughTheServersRoot()
    {
        var root = Path.Combine(LinuxOnly.NewFolder(), "servers");

        new LinuxDirectorySecurity().SecureServersRoot(root, development: true);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
            File.GetUnixFileMode(root));
    }

    [LinuxFact]
    public void AFolderUnderOneOthersCanWriteIsNotTheServicesState()
    {
        // The system's temporary folder is everyone's (and the tests' folders are their user's, not root's).
        var findings = LinuxDirectorySecurity.FindReplaceableAncestors(Path.Combine(LinuxOnly.NewFolder(), "agent"));

        Assert.NotEmpty(findings);
        Assert.Empty(LinuxDirectorySecurity.FindReplaceableAncestors("/usr/lib/x"));
    }

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
