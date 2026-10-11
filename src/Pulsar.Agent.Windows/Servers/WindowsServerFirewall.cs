using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// Windows Firewall rules through its COM API (<c>HNetCfg.FwPolicy2</c>), never through <c>netsh</c>: inbound, allow,
/// one protocol and local port each, bound to the server's executable and, when the allocation is not every interface,
/// to its address. Rule names are derived from the server and port, so they can be replaced and removed exactly.
/// </summary>
public sealed partial class WindowsServerFirewall(bool enabled, ILogger<WindowsServerFirewall> logger) : IServerFirewall
{
    public const string Grouping = "Pulsar";

    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int DirectionInbound = 1;
    private const int ActionAllow = 1;
    private const int AllProfiles = 0x7FFFFFFF;

    public bool Enabled => enabled;

    public static string RuleName(string shortId, string portName, PortProtocol protocol) =>
        $"Pulsar {shortId} {portName} {(protocol == PortProtocol.Udp ? "udp" : "tcp")}";

    public void Allow(string shortId, string executablePath, IReadOnlyList<BoundPort> ports, IReadOnlyCollection<string> portNames)
    {
        ArgumentNullException.ThrowIfNull(ports);
        if (!enabled)
        {
            return;
        }

        ServerShortId.Validate(shortId);
        Use(rules =>
        {
            RemoveRules(rules, shortId, portNames);
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
                        r.LocalPorts = port.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
        });
        LogRulesApplied(shortId, ports.Count);
    }

    public void Remove(string shortId, IReadOnlyCollection<string> portNames)
    {
        if (!enabled)
        {
            return;
        }

        ServerShortId.Validate(shortId);
        Use(rules => RemoveRules(rules, shortId, portNames));
        LogRulesRemoved(shortId);
    }

    /// <summary>
    /// Removes every rule the agent made for these servers (by its grouping and their names), whatever ports they had and
    /// whether managing the firewall is on now or not: unenroll leaves none behind. Returns how many were removed.
    /// </summary>
    public static int RemoveAll(IReadOnlyCollection<string> shortIds)
    {
        ArgumentNullException.ThrowIfNull(shortIds);
        return shortIds.Count == 0 ? 0 : RemoveWhere(name => shortIds.Any(id => name.StartsWith($"{Grouping} {id} ", StringComparison.Ordinal)));
    }

    /// <summary>Removes the rules of the agent's grouping whose name matches.</summary>
    internal static int RemoveWhere(Func<string, bool> match)
    {
        List<string> names = [];
        Use(rules =>
        {
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
        });
        return names.Count;
    }

    private static void RemoveRules(dynamic rules, string shortId, IReadOnlyCollection<string> portNames)
    {
        foreach (var name in portNames.SelectMany(p => new[] { RuleName(shortId, p, PortProtocol.Udp), RuleName(shortId, p, PortProtocol.Tcp) }))
        {
            // Nothing happens when there is no rule with that name. Rules are always removed before they are added, so a
            // name is never used twice.
            rules.Remove(name);
        }
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
}
