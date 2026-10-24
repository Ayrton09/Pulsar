using System.Net;
using Pulsar.Agent.Core.Commands;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The ports in use on the node's machine (1.0.7), which the panel's create dialog skips.</summary>
public sealed class PortsInUseTests
{
    [Fact]
    public void EachPortIsListedOnceTheLowestFirst()
    {
        var result = PortsInUse.From(
        [
            (new IPEndPoint(IPAddress.Any, 27015), "udp"),
            (new IPEndPoint(IPAddress.Any, 27015), "udp"), // two sockets on one port
            (new IPEndPoint(IPAddress.Parse("::ffff:192.168.0.10"), 25565), "tcp"),
            (new IPEndPoint(IPAddress.IPv6Any, 27015), "udp"),
            (new IPEndPoint(IPAddress.Loopback, 8443), "tcp"),
        ]);

        Assert.False(result.Truncated);
        Assert.Equal(
            [new PortInUse("127.0.0.1", 8443, "tcp"), new PortInUse("192.168.0.10", 25565, "tcp"), new PortInUse("0.0.0.0", 27015, "udp"),
                new PortInUse("::", 27015, "udp")],
            result.Ports);
    }

    [Fact]
    public void AMachineWithTooManyIsCutAtTheLimitAndSaysSo()
    {
        var result = PortsInUse.From(Enumerable.Range(1, PortsInUseResult.MaxPorts + 10).Select(p => (new IPEndPoint(IPAddress.Any, p), "udp")));

        Assert.True(result.Truncated);
        Assert.Equal(PortsInUseResult.MaxPorts, result.Ports.Count);
        Assert.Equal(1, result.Ports[0].Port);
    }

    [Fact]
    public void ThisMachinesListHoldsASocketJustOpened()
    {
        using var socket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

        Assert.Contains(new PortInUse("127.0.0.1", port, "udp"), PortsInUse.List().Ports);
    }
}
