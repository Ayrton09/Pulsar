using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// Firewall rules for servers (docs/architecture/06, "Windows Firewall"), when the node's administrator lets the agent
/// manage them: one inbound rule per server port and declared protocol, limited to the server's executable, and one
/// outbound rule that keeps the server's account from the panel's ports. Nothing else is ever opened (RCON's TCP side
/// stays closed: game ports declare UDP only), and the firewall is never turned off.
/// </summary>
public interface IServerFirewall
{
    /// <summary>Whether the agent manages rules on this node (announced to the panel).</summary>
    bool Enabled { get; }

    /// <summary>
    /// Replaces the server's inbound rules (whatever ports it had) with ones for <paramref name="ports"/> (before every
    /// start). Its account's outbound block is kept, and made with the last targets when it is missing.
    /// </summary>
    void Allow(string shortId, string executablePath, IReadOnlyList<BoundPort> ports, ProcessIdentity identity);

    /// <summary>
    /// Creates, or updates in place, the outbound block of each server's own account toward <paramref name="targets"/>,
    /// which later starts use too (see <see cref="PanelBlockKeeper"/>).
    /// </summary>
    void BlockPanel(IReadOnlyCollection<ServerAccount> servers, PanelTargets targets);

    /// <summary>Removes the server's rules, its account's outbound block included (when it is deleted).</summary>
    void Remove(string shortId);

    /// <summary>
    /// The server is provisioned (again): its account is blocked from now on, even when a server of the same short id was
    /// removed earlier in this run.
    /// </summary>
    void Provisioned(string shortId);
}

/// <summary>The node's firewall is left to its administrator.</summary>
public sealed class UnmanagedFirewall : IServerFirewall
{
    public bool Enabled => false;

    public void Allow(string shortId, string executablePath, IReadOnlyList<BoundPort> ports, ProcessIdentity identity)
    {
    }

    public void BlockPanel(IReadOnlyCollection<ServerAccount> servers, PanelTargets targets)
    {
    }

    public void Remove(string shortId)
    {
    }

    public void Provisioned(string shortId)
    {
    }
}

/// <summary>A server and the identity its game runs as: only a server's own account is ever blocked.</summary>
public sealed record ServerAccount(string ShortId, ProcessIdentity Identity);

/// <summary>
/// Where the servers' accounts may not connect: the panel's TCP ports, at every address it is known by, loopback and the
/// node's own addresses, in the forms the Windows Firewall takes.
/// </summary>
public sealed record PanelTargets(IReadOnlyList<string> Addresses, IReadOnlyList<int> Ports)
{
    /// <summary>
    /// Loopback, on every node: where the panel runs on the node, a game reaches it there too (and from there takes the
    /// local agent's share of the panel's budget for connections not signed in yet). The firewall refuses <c>::1</c>
    /// itself; <c>::/127</c> is it and the unspecified address, which nothing connects to.
    /// </summary>
    private static readonly string[] s_loopback = ["127.0.0.0/8", "::/127"];

    public string RemoteAddresses => string.Join(',', Addresses);

    public string RemotePorts => string.Join(',', Ports.Select(port => port.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The ports of <paramref name="panels"/> at loopback, <paramref name="panelAddresses"/> and the node's own addresses
    /// (a panel on the node, also behind a public name that leads back to it through the router). Only those ports: a
    /// game may still reach the panel machine's other services (its game databases). Null without a panel.
    /// </summary>
    public static PanelTargets? For(IReadOnlyCollection<Uri> panels, IEnumerable<IPAddress> panelAddresses, IEnumerable<IPAddress> ownAddresses)
    {
        ArgumentNullException.ThrowIfNull(panels);
        ArgumentNullException.ThrowIfNull(panelAddresses);
        ArgumentNullException.ThrowIfNull(ownAddresses);
        if (panels.Count == 0)
        {
            return null;
        }

        List<string> addresses = [.. s_loopback];
        foreach (var address in panelAddresses.Concat(ownAddresses))
        {
            if (Remote(address) is { } remote && !addresses.Contains(remote))
            {
                addresses.Add(remote);
            }
        }

        return new PanelTargets(addresses, [.. panels.Select(panel => panel.Port).Distinct().Order()]);
    }

    /// <summary>
    /// An address as the firewall takes it: an IPv4-mapped one as IPv4, an IPv6 one without its scope; none for loopback
    /// (listed whole) or the unspecified addresses.
    /// </summary>
    private static string? Remote(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return null;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()).ToString() : address.ToString();
    }

    public bool Equals(PanelTargets? other) =>
        other is not null && Addresses.SequenceEqual(other.Addresses, StringComparer.Ordinal) && Ports.SequenceEqual(other.Ports);

    public override int GetHashCode() => HashCode.Combine(RemoteAddresses, RemotePorts);
}
