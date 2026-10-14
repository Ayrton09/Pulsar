using System.Buffers.Binary;
using System.Net;

namespace Pulsar.Api.Http;

/// <summary>
/// Where a client's connections and requests are counted together (the agents' endpoint's budgets, the rate limiters):
/// an IPv4 address, also when mapped into IPv6, or the /64 of an IPv6 address, the least one host or one home network
/// has. Every loopback address (127.0.0.0/8, ::1) is one: a process on the panel's machine can use any of them.
/// </summary>
internal readonly record struct ClientAddress(bool IPv6, ulong Bits)
{
    /// <summary>The panel's own machine.</summary>
    public static readonly ClientAddress Loopback = new(false, 0x7F000000);

    /// <summary>The wider network the address is part of: its IPv4 /24 or IPv6 /48, what one site or one customer often has.</summary>
    public ClientAddress Network => IPv6 ? this with { Bits = Bits & 0xFFFF_FFFF_FFFF_0000 } : this with { Bits = Bits & 0xFFFF_FF00 };

    public static ClientAddress? Of(EndPoint? endpoint) => endpoint is IPEndPoint { Address: var address } ? Of(address) : null;

    public static ClientAddress? Of(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return Loopback;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
        {
            return null;
        }

        return written == 4
            ? new ClientAddress(false, BinaryPrimitives.ReadUInt32BigEndian(bytes))
            : new ClientAddress(true, BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }

    /// <summary>A rate limiter's partition for requests from <paramref name="address"/>.</summary>
    public static string PartitionOf(IPAddress? address) => Of(address)?.ToString() ?? "unknown";

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        if (!IPv6)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)Bits);
            return new IPAddress(bytes[..4]).ToString();
        }

        BinaryPrimitives.WriteUInt64BigEndian(bytes, Bits);
        return new IPAddress(bytes).ToString() + "/64";
    }
}
