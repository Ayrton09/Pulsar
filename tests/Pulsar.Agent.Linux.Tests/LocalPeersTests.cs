using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>Who holds the other end of RCON: read from the kernel's socket tables.</summary>
public sealed class LocalPeersTests
{
    private readonly LinuxLocalPeers _peers = new(new LinuxServerAccounts(NullLogger<LinuxServerAccounts>.Instance));

    [LinuxTheory]
    [InlineData("0100007F:1F90", "127.0.0.1", 8080)]
    [InlineData("00000000:6987", "0.0.0.0", 27015)]
    [InlineData("00000000000000000000000001000000:0050", "::1", 80)]
    [InlineData("0000000000000000FFFF00000100007F:6987", "::ffff:127.0.0.1", 27015)]
    public void TheTablesAddressesAreRead(string text, string address, int port)
    {
        Assert.Equal(new IPEndPoint(IPAddress.Parse(address), port), LinuxLocalPeers.Endpoint(text));
    }

    [LinuxFact]
    public async Task TheProcessThatAcceptedAConnectionIsFound()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.Current.CancellationToken);
        using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

        var peer = _peers.PeerProcess((IPEndPoint)client.Client.LocalEndPoint!, (IPEndPoint)client.Client.RemoteEndPoint!);

        Assert.Equal(Environment.ProcessId, peer);
        Assert.True(_peers.RunsAs(peer!.Value, ProcessIdentity.CurrentUser));
        Assert.False(_peers.RunsAs(peer.Value, ProcessIdentity.Account("pulsar_nosuchsrv")));
    }

    [LinuxFact]
    public async Task AConnectionNotAcceptedYetIsItsListenersProcess()
    {
        // A Source server accepts RCON only on its next frame: until then the connection waits in its listener's queue.
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.Current.CancellationToken);

        var peer = _peers.PeerProcess((IPEndPoint)client.Client.LocalEndPoint!, (IPEndPoint)client.Client.RemoteEndPoint!);

        Assert.Equal(Environment.ProcessId, peer);
    }

    [LinuxFact]
    public void TheProcessesOnAUdpPortAreFound()
    {
        using var game = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)game.Client.LocalEndPoint!).Port;

        Assert.Equal([Environment.ProcessId], _peers.UdpProcesses(IPAddress.Loopback, port));
        Assert.Empty(_peers.UdpProcesses(IPAddress.Loopback, port == 65535 ? 65534 : port + 1));
    }

    [LinuxFact]
    public void NobodyIsAtTheOtherEndOfAConnectionThatIsNotThere()
    {
        Assert.Null(_peers.PeerProcess(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2)));
    }
}
