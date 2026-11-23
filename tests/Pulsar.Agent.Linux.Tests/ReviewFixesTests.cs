using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>What the reviews of the Linux agent found, kept fixed.</summary>
public sealed class ReviewFixesTests
{
    [LinuxFact]
    public void AUdpSocketNoProcessIsSeenHoldingFailsTheCheck()
    {
        // A fake /proc: the game (pid 100) holds one socket on the port, and another socket there has no holder (passed on
        // to another process and closed): what is sent there may reach it.
        var proc = LinuxOnly.NewFolder();
        Directory.CreateDirectory(Path.Combine(proc, "net"));
        File.WriteAllText(Path.Combine(proc, "net", "udp"), """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
               1: 00000000:6987 00000000:0000 07 00000000:00000000 00:00000000 00000000  1001        0 5555 2 0000000000000000 0
               2: 00000000:6987 00000000:0000 07 00000000:00000000 00:00000000 00000000  1002        0 6666 2 0000000000000000 0
            """);
        Directory.CreateDirectory(Path.Combine(proc, "100", "fd"));
        File.CreateSymbolicLink(Path.Combine(proc, "100", "fd", "3"), "socket:[5555]");
        var peers = new LinuxLocalPeers(new LinuxServerAccounts(NullLogger<LinuxServerAccounts>.Instance), proc);

        var processes = peers.UdpProcesses(System.Net.IPAddress.Loopback, 27015);

        Assert.Contains(100, processes);
        Assert.Contains(LinuxLocalPeers.Unheld, processes);
    }

    [LinuxFact]
    public void AUdpPortHeldByTheGameAloneIsItsOwn()
    {
        var proc = LinuxOnly.NewFolder();
        Directory.CreateDirectory(Path.Combine(proc, "net"));
        File.WriteAllText(Path.Combine(proc, "net", "udp"), """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
               1: 00000000:6987 00000000:0000 07 00000000:00000000 00:00000000 00000000  1001        0 5555 2 0000000000000000 0
            """);
        Directory.CreateDirectory(Path.Combine(proc, "100", "fd"));
        File.CreateSymbolicLink(Path.Combine(proc, "100", "fd", "3"), "socket:[5555]");

        Assert.Equal([100], new LinuxLocalPeers(new LinuxServerAccounts(NullLogger<LinuxServerAccounts>.Instance), proc)
            .UdpProcesses(System.Net.IPAddress.Loopback, 27015));
    }

    [LinuxFact]
    public void ADollarInAPathIsNoVariableOfSystemds()
    {
        var unit = SystemdUnits.AgentUnitFile("/opt/agent/PulsarAgent", ["--data-dir", "/srv/$pulsar/${agent}"], TimeSpan.FromSeconds(60));

        Assert.Contains("\"/srv/$$pulsar/$${agent}\"", unit, StringComparison.Ordinal);
        Assert.Equal(["/opt/agent/PulsarAgent", "run", "--data-dir", "/srv/$pulsar/${agent}"], SystemdUnits.SplitExecLine(SystemdUnits.ExecStartOf(unit)!));
    }

    [LinuxFact]
    public void TheProgramsCopiesAreBesideTheStateFolderWhateverItsLastSlash()
    {
        Assert.Equal("/srv/agent-programs", new AgentPaths("/srv/agent/").HostProgramsDirectory);
        Assert.Equal("/srv/agent-programs", new AgentPaths("/srv/agent").HostProgramsDirectory);
        Assert.DoesNotContain(new AgentPaths("/srv/agent").HostProgramsDirectory, new AgentPaths("/srv/agent").OwnFolders);
    }

    [LinuxFact]
    public void TheGamesEnvironmentFileIsReadOnceAndOnlyIfNobodyElseCouldReadIt()
    {
        var file = GameExec.WriteEnvironment(new Dictionary<string, string> { ["A"] = "1", ["PASSWORD"] = "secret value" });
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        Assert.Equal(["A=1", "PASSWORD=secret value"], GameExec.ReadEnvironment(file));
        Assert.False(File.Exists(file));

        var open = GameExec.WriteEnvironment(new Dictionary<string, string> { ["A"] = "1" });
        File.SetUnixFileMode(open, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Assert.Throws<IOException>(() => GameExec.ReadEnvironment(open));
        Assert.False(File.Exists(open));
    }

    [LinuxFact]
    public void TheHostsUnitHidesOtherUsersProcesses()
    {
        Assert.Contains("ProtectProc=invisible", SystemdUnits.AgentUnitFile("/usr/bin/true", [], TimeSpan.FromSeconds(60)), StringComparison.Ordinal);
    }
}
