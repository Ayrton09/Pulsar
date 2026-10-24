using System.Net;
using System.Net.NetworkInformation;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Commands;

/// <summary>The ports in use on this machine (<see cref="ListPortsInUseCommand"/>): TCP ports listened on, UDP ports bound.</summary>
public static class PortsInUse
{
    public static PortsInUseResult List() => From(
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => (e, "tcp"))
            .Concat(IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(e => (e, "udp"))));

    /// <summary>Each address, port and protocol once, the lowest ports first, at most <see cref="PortsInUseResult.MaxPorts"/>.</summary>
    public static PortsInUseResult From(IEnumerable<(IPEndPoint EndPoint, string Protocol)> endPoints)
    {
        var ports = endPoints
            .Select(e => new PortInUse((e.EndPoint.Address.IsIPv4MappedToIPv6 ? e.EndPoint.Address.MapToIPv4() : e.EndPoint.Address).ToString(), e.EndPoint.Port, e.Protocol))
            .Distinct()
            .OrderBy(p => p.Port).ThenBy(p => p.Protocol, StringComparer.Ordinal).ThenBy(p => p.Address, StringComparer.Ordinal)
            .ToList();
        return ports.Count > PortsInUseResult.MaxPorts
            ? new PortsInUseResult(ports[..PortsInUseResult.MaxPorts], Truncated: true)
            : new PortsInUseResult(ports);
    }
}
