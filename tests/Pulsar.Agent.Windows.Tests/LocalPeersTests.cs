using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// Who is at the other end of a local connection (docs/architecture/08, T18), against a listener of this very process:
/// no elevation, no account of its own.
/// </summary>
public sealed class LocalPeersTests
{
    private readonly WindowsLocalPeers _peers = new();

    [WindowsFact]
    public Task TheProcessThatListensIsFoundOverIpv4() => FindsTheListenerAsync(IPAddress.Loopback);

    [WindowsFact]
    public Task TheProcessThatListensIsFoundOverIpv6() => FindsTheListenerAsync(IPAddress.IPv6Loopback);

    [WindowsFact]
    public async Task TheOtherEndIsTheProcessThatListensNotTheOneThatConnected()
    {
        // A listener in a process of its own: the answer names it, not this process (the end that connected).
        using var other = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command \"$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); " +
            "[Console]::Out.WriteLine($l.LocalEndpoint.Port); [Console]::Out.Flush(); [void][Console]::In.ReadLine()\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var port = int.Parse((await other.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken))!, CultureInfo.InvariantCulture);
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);

            var peer = _peers.PeerProcess((IPEndPoint)client.Client.LocalEndPoint!, (IPEndPoint)client.Client.RemoteEndPoint!);

            Assert.Equal(other.Id, peer);
            Assert.True(_peers.RunsAs(other.Id, ProcessIdentity.CurrentUser));
        }
        finally
        {
            other.Kill();
        }
    }

    [WindowsFact]
    public void TheProcessesBoundToAUdpPortAreFoundOnItsAddressAndOnEveryAddress()
    {
        // GoldSrc's RCON shares the game's UDP port: whoever is bound there, on the address or on every one, may get it.
        using var any = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)any.Client.LocalEndPoint!).Port;
        using var loopback = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var loopbackPort = ((IPEndPoint)loopback.Client.LocalEndPoint!).Port;

        Assert.Equal([Environment.ProcessId], _peers.UdpProcesses(IPAddress.Loopback, port));
        Assert.Equal([Environment.ProcessId], _peers.UdpProcesses(IPAddress.Parse("192.0.2.1"), port));
        Assert.Equal([Environment.ProcessId], _peers.UdpProcesses(IPAddress.Loopback, loopbackPort));
        Assert.Empty(_peers.UdpProcesses(IPAddress.Parse("192.0.2.1"), loopbackPort)); // bound elsewhere
        Assert.True(_peers.RunsAs(Environment.ProcessId, ProcessIdentity.CurrentUser));
    }

    [WindowsFact]
    public void AUdpPortNobodyHoldsHasNoProcess()
    {
        int port;
        using (var free = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)free.Client.LocalEndPoint!).Port;
        }

        Assert.Empty(_peers.UdpProcesses(IPAddress.Loopback, port));
    }

    [WindowsFact]
    public void AConnectionThatIsNotThereHasNoPeer() =>
        Assert.Null(_peers.PeerProcess(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2)));

    [WindowsFact]
    public void AProcessRunsAsItsOwnAccountOnly()
    {
        // Run as SYSTEM (the CI step), this process and the System one are SYSTEM's own.
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(_peers.RunsAs(Environment.ProcessId, ProcessIdentity.CurrentUser));
        Assert.False(_peers.RunsAs(Environment.ProcessId, ProcessIdentity.Account("pulsar_nosuch99")));
        Assert.Equal(identity.IsSystem, _peers.RunsAs(Environment.ProcessId, ProcessIdentity.Account("SYSTEM")));
        Assert.Equal(identity.IsSystem, _peers.RunsAs(4, ProcessIdentity.CurrentUser)); // the System process
    }

    private async Task FindsTheListenerAsync(IPAddress address)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);

        // Not accepted yet: the connection already belongs to the process that listens.
        var peer = _peers.PeerProcess((IPEndPoint)client.Client.LocalEndPoint!, (IPEndPoint)client.Client.RemoteEndPoint!);

        Assert.Equal(Environment.ProcessId, peer);
    }
}
