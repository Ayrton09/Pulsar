using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// Windows Firewall rules through its COM API (<c>HNetCfg.FwPolicy2</c>), never through <c>netsh</c>: inbound, allow,
/// one protocol and local port each, bound to the server's executable and, when the allocation is not every interface,
/// to its address; and one outbound block for the server's account toward the panel's TCP ports, updated in place so it
/// is never missing while it changes. Rule names are derived from the server and port, so they can be replaced and
/// removed exactly.
/// </summary>
public sealed partial class WindowsServerFirewall(bool enabled, ILogger<WindowsServerFirewall> logger) : IServerFirewall
{
    public const string Grouping = "Pulsar";

    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int DirectionInbound = 1;
    private const int DirectionOutbound = 2;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int AllProfiles = 0x7FFFFFFF;
    private const int GroupPolicyOverride = 1; // NET_FW_MODIFY_STATE_GP_OVERRIDE: local rules are not applied

    private static readonly (int Type, string Name)[] s_profiles = [(1, "domain"), (2, "private"), (4, "public")];
    private static readonly SecurityIdentifier? s_self = WindowsIdentity.GetCurrent().User;

    // The outbound blocks: one writer at a time, and none for a server once it is removed.
    private readonly Lock _blockLock = new();
    private readonly Dictionary<string, AppliedBlock> _applied = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private volatile PanelTargets? _targets;
    private string? _inert;

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
        Use(rules =>
        {
            RemoveRules(rules, shortId, keepPanelBlock: true);
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
        });
        LogRulesApplied(shortId, ports.Count);

        // The block normally exists already (PanelBlockKeeper); one that went missing is made again before the game starts.
        if (_targets is { } targets)
        {
            try
            {
                Block([new ServerAccount(shortId, identity)], targets);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogPanelBlockFailed(shortId, ex.Message);
            }
        }
    }

    public void BlockPanel(IReadOnlyCollection<ServerAccount> servers, PanelTargets targets)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(targets);
        if (!enabled)
        {
            return;
        }

        _targets = targets;
        WarnIfInert();
        Block(servers, targets);
    }

    public void Remove(string shortId)
    {
        if (!enabled)
        {
            return;
        }

        ServerShortId.Validate(shortId);
        lock (_blockLock)
        {
            // Before the rules go: a refresh already under way must not make the block again for a server being deleted.
            _removed.Add(shortId);
            _applied.Remove(shortId);
            Use(rules => RemoveRules(rules, shortId, keepPanelBlock: false));
        }

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

    /// <summary>Whether <paramref name="identity"/> is the server's own account (<c>pulsar_&lt;shortId&gt;</c>), the only kind ever blocked.</summary>
    internal static bool IsServerAccount(string shortId, ProcessIdentity identity) =>
        ServerShortId.IsValid(shortId) && identity is ProcessIdentity.AccountIdentity account
        && string.Equals(account.Name, WindowsServerAccounts.AccountName(shortId), StringComparison.Ordinal);

    /// <summary>
    /// Whether an account may be blocked: a local account's, never the agent's own user, SYSTEM or a built-in group (a
    /// rule for them would cut the agent, or everyone, off from the panel).
    /// </summary>
    internal static bool MayBlock(SecurityIdentifier account, SecurityIdentifier? self) =>
        account.IsAccountSid() && (self is null || !account.Equals(self)) && !account.IsWellKnown(WellKnownSidType.LocalSystemSid)
        && !account.Value.StartsWith("S-1-5-32-", StringComparison.Ordinal);

    /// <summary>The firewall's local user condition: an SDDL whose entry allows the one account it applies to.</summary>
    internal static string LocalUsers(SecurityIdentifier account) => $"D:(A;;CC;;;{account.Value})";

    /// <summary>
    /// Why rules would have no effect now: the firewall off for a profile in use (a third-party firewall turns it off), or
    /// group policy not merging local rules. Null when they apply.
    /// </summary>
    internal static string? InertReason()
    {
        var policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        try
        {
            dynamic p = policy;
            int current = p.CurrentProfileTypes;
            var off = s_profiles.Where(profile => (current & profile.Type) != 0 && !(bool)p.FirewallEnabled[profile.Type]).Select(profile => profile.Name).ToList();
            if (off.Count > 0)
            {
                return $"the Windows Firewall is off for the {string.Join(", ", off)} profile in use";
            }

            return (int)p.LocalPolicyModifyState == GroupPolicyOverride ? "group policy does not apply this machine's own firewall rules" : null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
        }
    }

    private void WarnIfInert()
    {
        string? reason;
        try
        {
            reason = InertReason();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = $"its state could not be read ({ex.Message})";
        }

        if (reason != Interlocked.Exchange(ref _inert, reason) && reason is not null)
        {
            LogInert(reason);
        }
    }

    private void Block(IReadOnlyCollection<ServerAccount> servers, PanelTargets targets)
    {
        lock (_blockLock)
        {
            List<(string ShortId, string Users)> blocked = [];
            foreach (var server in servers)
            {
                if (!_removed.Contains(server.ShortId) && UsersOf(server) is { } users)
                {
                    blocked.Add((server.ShortId, users));
                }
            }

            if (blocked.Count == 0)
            {
                return;
            }

            Use(rules =>
            {
                foreach (var (shortId, users) in blocked)
                {
                    try
                    {
                        UpdatePanelBlock(rules, shortId, users, targets);
                    }
                    catch (Exception ex) when (ex is COMException or ArgumentException)
                    {
                        // The rule keeps what it had; the next refresh tries again.
                        LogPanelBlockFailed(shortId, ex.Message);
                    }
                }
            });
        }
    }

    /// <summary>The block's users for a server running as its own account; null (none) otherwise.</summary>
    private string? UsersOf(ServerAccount server)
    {
        if (server.Identity is not ProcessIdentity.AccountIdentity)
        {
            return null; // a development agent's server runs as the agent's user: the rule would cut the agent off
        }

        if (!IsServerAccount(server.ShortId, server.Identity))
        {
            LogNotServerAccount(server.ShortId);
            return null;
        }

        SecurityIdentifier sid;
        try
        {
            sid = WindowsServerAccounts.Sid(WindowsServerAccounts.AccountName(server.ShortId));
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            LogPanelBlockFailed(server.ShortId, ex.Message); // its account is not there (yet): the next refresh looks again
            return null;
        }

        if (!MayBlock(sid, s_self))
        {
            LogNotServerAccount(server.ShortId);
            return null;
        }

        return LocalUsers(sid);
    }

    /// <summary>
    /// Updates the existing rule in place (never removed and added again, so it is never missing), or adds it. Left alone
    /// when it is as last written.
    /// </summary>
    private void UpdatePanelBlock(dynamic rules, string shortId, string users, PanelTargets targets)
    {
        var name = PanelBlockName(shortId);
        var wanted = $"{users}|{targets.RemoteAddresses}|{targets.RemotePorts}";
        object? existing = Find(rules, name);
        var rule = existing ?? Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
        try
        {
            dynamic r = rule;
            if (existing is not null && _applied.TryGetValue(shortId, out var last) && last.Wanted == wanted && last.State == State(r))
            {
                return;
            }

            if (existing is null)
            {
                r.Name = name;
                r.Grouping = Grouping;
            }

            r.Description = $"Game server {shortId} may not reach the panel. Managed by the Pulsar agent: changes are replaced.";
            r.Direction = DirectionOutbound;
            r.Action = ActionBlock;
            r.Protocol = ProtocolTcp;
            r.RemoteAddresses = targets.RemoteAddresses;
            r.RemotePorts = targets.RemotePorts;
            r.LocalUserAuthorizedList = users;
            r.Profiles = AllProfiles;
            r.Enabled = true;
            if (existing is null)
            {
                rules.Add(rule);
            }

            _applied[shortId] = new AppliedBlock(wanted, State(r));
            LogPanelBlocked(shortId, targets.RemoteAddresses, targets.RemotePorts);
        }
        finally
        {
            Marshal.FinalReleaseComObject(rule);
        }
    }

    /// <summary>The rule of the agent's grouping with that name, if any.</summary>
    private static object? Find(dynamic rules, string name)
    {
        object rule;
        try
        {
            rule = rules.Item(name);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException)
        {
            return null;
        }

        if (((dynamic)rule).Grouping is string grouping && grouping == Grouping)
        {
            return rule;
        }

        Marshal.FinalReleaseComObject(rule);
        return null;
    }

    /// <summary>What the rule says now, as the firewall reports it (someone may have changed it since).</summary>
    private static string State(dynamic r) =>
        $"{r.Direction}|{r.Action}|{r.Protocol}|{r.Enabled}|{r.Profiles}|{r.LocalUserAuthorizedList}|{r.RemoteAddresses}|{r.RemotePorts}|{r.ApplicationName}";

    /// <summary>Every rule of the server, by prefix, whatever ports the manifest names now; its account's block too unless kept.</summary>
    private static void RemoveRules(dynamic rules, string shortId, bool keepPanelBlock)
    {
        var block = PanelBlockName(shortId);
        Func<string, bool> match = name => IsRuleOf(shortId, name) && !(keepPanelBlock && name == block); // (a lambda cannot be passed along a dynamic argument)
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The firewall rule that keeps server {ShortId} from the panel could not be made or updated: {Reason}")]
    private partial void LogPanelBlockFailed(string shortId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ShortId} does not run as its own account: no firewall rule keeps it from the panel")]
    private partial void LogNotServerAccount(string shortId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The firewall rules that keep game servers from the panel have no effect: {Reason}")]
    private partial void LogInert(string reason);

    private sealed record AppliedBlock(string Wanted, string State);
}
