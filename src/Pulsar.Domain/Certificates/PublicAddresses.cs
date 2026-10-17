using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Pulsar.Domain.Certificates;

/// <summary>
/// What kind of IP address an address is, for the public IP certificate (docs/architecture/08, T40): the address a
/// certificate may be asked for must be one the internet reaches (a public, globally routable unicast address), and the
/// certificate is served only to connections from addresses that are not on a local network.
/// </summary>
public static class PublicAddresses
{
    // IANA's IPv4 special-purpose registry (RFC 6890 and its updates): none of these is a host on the internet.
    private static readonly (uint Network, int Prefix)[] s_ipv4NotGlobal =
    [
        (0x00000000, 8),    // 0.0.0.0/8, "this network"
        (0x0A000000, 8),    // 10.0.0.0/8, private
        (0x64400000, 10),   // 100.64.0.0/10, shared address space (carrier-grade NAT)
        (0x7F000000, 8),    // 127.0.0.0/8, loopback
        (0xA9FE0000, 16),   // 169.254.0.0/16, link-local
        (0xAC100000, 12),   // 172.16.0.0/12, private
        (0xC0000000, 24),   // 192.0.0.0/24, IETF protocol assignments
        (0xC0000200, 24),   // 192.0.2.0/24, documentation (TEST-NET-1)
        (0xC0586300, 24),   // 192.88.99.0/24, the former 6to4 relay anycast
        (0xC0A80000, 16),   // 192.168.0.0/16, private
        (0xC6120000, 15),   // 198.18.0.0/15, benchmarking
        (0xC6336400, 24),   // 198.51.100.0/24, documentation (TEST-NET-2)
        (0xCB007100, 24),   // 203.0.113.0/24, documentation (TEST-NET-3)
        (0xE0000000, 4),    // 224.0.0.0/4, multicast
        (0xF0000000, 4),    // 240.0.0.0/4, reserved, and 255.255.255.255
    ];

    // Of those, the ones that only ever mean a machine on this side of a router (a client the internet cannot be).
    private static readonly (uint Network, int Prefix)[] s_ipv4Local =
    [
        (0x00000000, 8),
        (0x0A000000, 8),
        (0x64400000, 10),
        (0x7F000000, 8),
        (0xA9FE0000, 16),
        (0xAC100000, 12),
        (0xC0A80000, 16),
    ];

    /// <summary>The address as it is compared: an IPv4 address mapped into IPv6 (a dual-mode socket's) as IPv4.</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>
    /// <paramref name="text"/> as an IP address literal written the usual way: IPv4 as four decimal numbers without
    /// leading zeros, IPv6 without brackets, zone or prefix. Anything else (a host name, <c>010.1.1.1</c>, <c>1.2.3</c>,
    /// an IPv4 address mapped into IPv6) is not one.
    /// </summary>
    public static bool TryParse(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrEmpty(text) || text.Length > 45 || text.Any(c => !(char.IsAsciiHexDigit(c) || c is '.' or ':')))
        {
            return false;
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(text, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6 || v6.ScopeId != 0 || v6.IsIPv4MappedToIPv6)
            {
                return false;
            }

            address = v6;
            return true;
        }

        var parts = text.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length is 0 or > 3 || (p.Length > 1 && p[0] == '0') || !p.All(char.IsAsciiDigit)
                || int.Parse(p, CultureInfo.InvariantCulture) > 255))
        {
            return false;
        }

        address = IPAddress.Parse(text);
        return true;
    }

    /// <summary>
    /// A public, globally routable unicast address: what a certificate authority can reach and validate, and what
    /// browsers on the internet connect to. Refused: private, loopback, link-local, carrier-grade NAT, documentation,
    /// benchmarking, multicast and reserved IPv4; for IPv6 anything outside 2000::/3 (unique local, link-local, multicast,
    /// mapped and translated IPv4) and, inside it, IETF protocol assignments (Teredo among them), documentation and 6to4.
    /// </summary>
    public static bool IsGloballyRoutable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !InAny(V4(address), s_ipv4NotGlobal);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        var first16 = (bytes[0] << 8) | bytes[1];
        var second16 = (bytes[2] << 8) | bytes[3];
        return (bytes[0] & 0xE0) == 0x20                                  // 2000::/3, global unicast
            && !(first16 == 0x2001 && second16 < 0x0200)                   // 2001::/23, IETF protocol assignments
            && !(first16 == 0x2001 && second16 == 0x0DB8)                  // 2001:db8::/32, documentation
            && first16 != 0x2002                                           // 2002::/16, 6to4
            && !(first16 == 0x3FFF && (second16 & 0xF000) == 0);           // 3fff::/20, documentation
    }

    /// <summary>
    /// An address only a machine on a local network has: loopback, private and carrier-grade NAT IPv4, link-local,
    /// unique local and site-local IPv6, the unspecified address. A connection from one is never from the internet.
    /// </summary>
    public static bool IsLocal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = Normalize(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return InAny(V4(address), s_ipv4Local);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)
            || (bytes[0] & 0xFE) == 0xFC                                   // fc00::/7, unique local
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) is 0x80 or 0xC0);    // fe80::/10 link-local, fec0::/10 site-local
    }

    private static uint V4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static bool InAny(uint value, (uint Network, int Prefix)[] ranges) =>
        ranges.Any(r => (value & (uint.MaxValue << (32 - r.Prefix))) == r.Network);
}
