using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// Windows Firewall rules through its COM API (<c>HNetCfg.FwPolicy2</c>), never through <c>netsh</c>: inbound, allow,
/// one protocol and local port each, bound to the server's executable and, when the allocation is not every interface,
/// to its address; and one outbound block for the server's account toward the panel's TCP ports. Rule names are derived
/// from the server and port, so they can be replaced and removed exactly.
/// </summary>
/// <param name="panels">The addresses the agent reaches its panel at (its configuration's panel and fallback), read at each start.</param>
public sealed partial class WindowsServerFirewall(bool enabled, Func<IReadOnlyList<Uri>> panels, ILogger<WindowsServerFirewall> logger)
    : IServerFirewall
{
    public const string Grouping = "Pulsar";

    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int DirectionInbound = 1;
    private const int DirectionOutbound = 2;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int AllProfiles = 0x7FFFFFFF;

    /// <summary>
    /// Loopback, on every node: where the panel runs on the node, a game reaches it there too (and from there takes the
    /// local agent's share of the panel's budget for connections not signed in yet). The firewall refuses <c>::1</c>
    /// itself; <c>::/127</c> is it and the unspecified address, which nothing connects to.
    /// </summary>
    private static readonly string[] s_loopback = ["127.0.0.0/8", "::/127"];

    public bool Enabled => enabled;

    public static string RuleName(string shortId, string portName, PortProtocol protocol) =>
        $"Pulsar {shortId} {portName} {(protocol == PortProtocol.Udp ? "udp" : "tcp")}";

    /// <summary>The outbound rule of the server's account (in the server's prefix, so every removal takes it too).</summary>
    public static string PanelBlockName(string shortId) => $"Pulsar {shortId} panel block";

    public void Allow(string shortId, string executablePath, IReadOnlyList<BoundPort> ports, ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(identity);
        if (!enabled)
        {
            return;
        }

        ServerShortId.Validate(shortId);
        var block = PanelBlockOf(shortId, identity); // names resolved before the firewall is opened
        Use(rules =>
        {
            RemoveRules(rules, shortId);
            foreach (var port in ports)
            {
                foreach (var protocol in port.Protocols)
                {
                    var rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
                    try
                    {
                        dynamic r = rule;
                        r.Name = RuleName(shortId, port.Name, protocol);
                        r.Description = $"Game server {shortId}, port '{port.Name}'. Managed by the Pulsar agent: changes are replaced at the next start.";
                        r.Grouping = Grouping;
                        r.Direction = DirectionInbound;
                        r.Action = ActionAllow;
                        r.Protocol = protocol == PortProtocol.Udp ? ProtocolUdp : ProtocolTcp;
                        r.LocalPorts = port.Port.ToString(CultureInfo.InvariantCulture);
                        if (port.IpAddress != PortAssignment.AnyAddress)
                        {
                            r.LocalAddresses = port.IpAddress;
                        }

                        r.ApplicationName = executablePath;
                        r.Profiles = AllProfiles;
                        r.Enabled = true;
                        rules.Add(rule);
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(rule);
                    }
                }
            }

            if (block is not null)
            {
                AddPanelBlock(rules, shortId, block);
            }
        });
        LogRulesApplied(shortId, ports.Count);
    }

    public void Remove(string shortId)
    {
        if (!enabled)
        {
            return;
        }

        ServerShortId.Validate(shortId);
        Use(rules => RemoveRules(rules, shortId));
        LogRulesRemoved(shortId);
    }

    /// <summary>
    /// Removes every rule the agent made for these servers (by its grouping and their names), whatever ports they had and
    /// whether managing the firewall is on now or not: unenroll leaves none behind. Returns how many were removed.
    /// </summary>
    public static int RemoveAll(IReadOnlyCollection<string> shortIds)
    {
        ArgumentNullException.ThrowIfNull(shortIds);
        return shortIds.Count == 0 ? 0 : RemoveWhere(name => shortIds.Any(id => IsRuleOf(id, name)));
    }

    /// <summary>
    /// Whether a rule (of the agent's grouping, which the caller has checked) is one the agent made for the server: named
    /// <c>Pulsar &lt;shortId&gt; …</c> (see <see cref="RuleName"/> and <see cref="PanelBlockName"/>), for whatever port: a
    /// game update may drop a port from its manifest, and its rule must not outlive the manifest that named it.
    /// </summary>
    internal static bool IsRuleOf(string shortId, string name) =>
        name.StartsWith($"{Grouping} {shortId} ", StringComparison.Ordinal);

    /// <summary>Removes the rules of the agent's grouping whose name matches.</summary>
    internal static int RemoveWhere(Func<string, bool> match)
    {
        var removed = 0;
        Use(rules => removed = RemoveMatching(rules, match));
        return removed;
    }

    /// <summary>
    /// The outbound rule of a server's account: TCP to the panel's ports at every address of the panel's names, loopback
    /// and the node's own addresses (a panel on the node, also behind a public name that leads back to it through the
    /// router). Only those ports: a game may still reach the panel machine's other services (its game databases). Null
    /// without a panel to block.
    /// </summary>
    internal static PanelBlock? PanelBlockFor(SecurityIdentifier account, IReadOnlyCollection<Uri> panels,
        Func<string, IReadOnlyList<IPAddress>> resolve, IEnumerable<IPAddress> ownAddresses)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(panels);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(ownAddresses);
        if (panels.Count == 0)
        {
            return null;
        }

        List<string> addresses = [.. s_loopback];
        var named = panels.Select(panel => panel.IdnHost).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(host => AddressesOf(host, resolve));
        foreach (var address in named.Concat(ownAddresses))
        {
            if (Remote(address) is { } remote && !addresses.Contains(remote))
            {
                addresses.Add(remote);
            }
        }

        var ports = panels.Select(panel => panel.Port).Distinct().Order().Select(port => port.ToString(CultureInfo.InvariantCulture));
        // The firewall's local user condition: an SDDL whose entries allow the users it applies to.
        return new PanelBlock($"D:(A;;CC;;;{account.Value})", string.Join(',', addresses), string.Join(',', ports));
    }

    private static IReadOnlyList<IPAddress> AddressesOf(string host, Func<string, IReadOnlyList<IPAddress>> resolve) =>
        IPAddress.TryParse(host, out var literal) ? [literal] : resolve(host);

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

    /// <summary>
    /// The outbound rule of a server that runs as its own account, read now: each start refreshes the panel's and the
    /// node's addresses. None for one that runs as the agent's user (development): the rule would cut the agent off.
    /// </summary>
    private PanelBlock? PanelBlockOf(string shortId, ProcessIdentity identity)
    {
        if (identity is not ProcessIdentity.AccountIdentity account)
        {
            return null;
        }

        try
        {
            return PanelBlockFor(WindowsServerAccounts.Sid(account.Name), panels(), Resolve, OwnAddresses());
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or NetworkInformationException)
        {
            LogPanelBlockFailed(shortId, ex.Message);
            return null;
        }
    }

    private IReadOnlyList<IPAddress> Resolve(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host);
        }
        catch (SocketException ex)
        {
            // Loopback and the node's own addresses are blocked all the same; the next start tries the name again.
            LogPanelNotResolved(host, ex.Message);
            return [];
        }
    }

    private static List<IPAddress> OwnAddresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)];

    /// <summary>
    /// Adds the outbound rule. Logged, not thrown, when the firewall refuses it: it keeps a game from holding the panel's
    /// connections for its node, the game is no more exposed without it, and so it does not keep the game from starting.
    /// </summary>
    private void AddPanelBlock(dynamic rules, string shortId, PanelBlock block)
    {
        var rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
        try
        {
            dynamic r = rule;
            r.Name = PanelBlockName(shortId);
            r.Description = $"Game server {shortId} may not reach the panel. Managed by the Pulsar agent: changes are replaced at the next start.";
            r.Grouping = Grouping;
            r.Direction = DirectionOutbound;
            r.Action = ActionBlock;
            r.Protocol = ProtocolTcp;
            r.RemoteAddresses = block.RemoteAddresses;
            r.RemotePorts = block.RemotePorts;
            r.LocalUserAuthorizedList = block.LocalUsers;
            r.Profiles = AllProfiles;
            r.Enabled = true;
            rules.Add(rule);
            LogPanelBlocked(shortId, block.RemoteAddresses, block.RemotePorts);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            LogPanelBlockFailed(shortId, ex.Message);
        }
        finally
        {
            Marshal.FinalReleaseComObject(rule);
        }
    }

    /// <summary>Every rule of the server, by prefix, whatever ports the manifest names now.</summary>
    private static void RemoveRules(dynamic rules, string shortId)
    {
        Func<string, bool> match = name => IsRuleOf(shortId, name); // (a lambda cannot be passed along a dynamic argument)
        RemoveMatching(rules, match);
    }

    private static int RemoveMatching(dynamic rules, Func<string, bool> match)
    {
        List<string> names = [];
        foreach (var rule in rules)
        {
            try
            {
                if (rule.Grouping is string grouping && grouping == Grouping && rule.Name is string name && match(name))
                {
                    names.Add(name);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(rule);
            }
        }

        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            rules.Remove(name);
        }

        return names.Count;
    }

    private static void Use(Action<dynamic> change)
    {
        var policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        object? rules = null;
        try
        {
            rules = ((dynamic)policy).Rules;
            change(rules!);
        }
        catch (COMException ex)
        {
            throw new GameProcessException("firewall.failed", $"The Windows Firewall rules could not be changed: {ex.Message}", ex);
        }
        finally
        {
            if (rules is not null)
            {
                Marshal.FinalReleaseComObject(rules);
            }

            Marshal.FinalReleaseComObject(policy);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Firewall rules for server {ShortId}: {Count} ports allowed")]
    private partial void LogRulesApplied(string shortId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Firewall rules for server {ShortId} removed")]
    private partial void LogRulesRemoved(string shortId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Firewall rule for server {ShortId}: its account may not reach the panel at {Addresses} on TCP {Ports}")]
    private partial void LogPanelBlocked(string shortId, string addresses, string ports);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The firewall rule that keeps server {ShortId} from the panel could not be made: {Reason}")]
    private partial void LogPanelBlockFailed(string shortId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel's name {Host} could not be resolved for the game servers' firewall rules: {Reason}")]
    private partial void LogPanelNotResolved(string host, string reason);
}

/// <summary>What the outbound rule of a server's account says, in the forms the firewall's COM API takes.</summary>
/// <param name="LocalUsers">The SDDL of the users it applies to (<c>LocalUserAuthorizedList</c>): the server's account only.</param>
/// <param name="RemoteAddresses">The panel's addresses, loopback and the node's own, comma-separated.</param>
/// <param name="RemotePorts">The TCP ports of the panel's addresses, comma-separated.</param>
internal sealed record PanelBlock(string LocalUsers, string RemoteAddresses, string RemotePorts);
