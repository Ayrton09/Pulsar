using System.Net;
using System.Net.Sockets;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Nodes;

/// <summary>
/// An <c>ip:port</c> pair of a node that servers can be given (docs/architecture/02), for both UDP and TCP. Assigned to
/// at most one port of one server; <see cref="AnyAddress"/> means every interface of the node.
/// </summary>
public sealed class NodeAllocation
{
    public const string AnyAddress = "0.0.0.0";
    public const int MaxNotesLength = 128;
    public const int MaxPortNameLength = 32;

    private NodeAllocation()
    {
        // For EF Core materialization.
        IpAddress = null!;
    }

    private NodeAllocation(AllocationId id, NodeId nodeId, string ipAddress, int port, string? notes, DateTimeOffset now)
    {
        Id = id;
        NodeId = nodeId;
        IpAddress = ipAddress;
        Port = port;
        Notes = notes;
        CreatedAt = now;
    }

    public AllocationId Id { get; private set; }

    public NodeId NodeId { get; private set; }

    /// <summary>IPv4 address in canonical form (<c>0.0.0.0</c> for every interface).</summary>
    public string IpAddress { get; private set; }

    public int Port { get; private set; }

    public ServerId? ServerId { get; private set; }

    /// <summary>The manifest port it serves (<c>game</c>, <c>tv</c>…) while assigned.</summary>
    public string? PortName { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsAssigned => ServerId is not null;

    /// <summary>The address in canonical form, or null when it is not an IPv4 address.</summary>
    public static string? NormalizeAddress(string? address) =>
        IPAddress.TryParse(address?.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : null;

    public static Result<NodeAllocation> Create(NodeId nodeId, string ipAddress, int port, string? notes, DateTimeOffset now)
    {
        if (NormalizeAddress(ipAddress) is not { } address)
        {
            return NodeErrors.AllocationAddressInvalid;
        }

        if (port is < 1 or > 65535)
        {
            return NodeErrors.AllocationPortInvalid;
        }

        var cleanNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (cleanNotes?.Length > MaxNotesLength)
        {
            return NodeErrors.AllocationNotesTooLong;
        }

        return new NodeAllocation(AllocationId.New(), nodeId, address, port, cleanNotes, now);
    }

    /// <summary>
    /// Whether this and <paramref name="other"/> (same node) would listen on the same socket: the same port on the same
    /// address, or on any address when one of them is every interface.
    /// </summary>
    public bool Overlaps(string ipAddress, int port) =>
        Port == port && (IpAddress == ipAddress || IpAddress == AnyAddress || ipAddress == AnyAddress);

    public void Assign(ServerId serverId, string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (ServerId is { } current && current != serverId)
        {
            throw new InvalidOperationException("The allocation belongs to another server.");
        }

        ServerId = serverId;
        PortName = portName;
    }

    public void Release()
    {
        ServerId = null;
        PortName = null;
    }
}
