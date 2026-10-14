using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>Real Windows Firewall rules: changes the machine's firewall, so only on a disposable elevated machine (CI).</summary>
public sealed class ServerFirewallTests
{
    private const string ShortId = "fwtest01";

    private static readonly PanelTargets s_targets = new(["127.0.0.0/8", "::/127", "203.0.113.10", "2001:db8::5"], [8443, 8444]);

    [AccountFact]
    public void RulesOpenOnlyTheDeclaredPortsAndProtocolsForTheGameAndGoAway()
    {
        var firewall = Firewall(enabled: true);
        var executable = @"C:\PulsarServers\fwtest01\srcds.exe";
        try
        {
            firewall.BlockPanel([new ServerAccount(ShortId, ProcessIdentity.CurrentUser)], s_targets);
            firewall.Allow(ShortId, executable,
                [new BoundPort("game", "0.0.0.0", 27015, [PortProtocol.Udp]), new BoundPort("tv", "10.0.0.5", 27020, [PortProtocol.Udp])],
                ProcessIdentity.CurrentUser);

            var game = Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Udp))!;
            Assert.Equal("27015", (string)game.LocalPorts);
            Assert.Equal(17, (int)game.Protocol);
            Assert.Equal(1, (int)game.Direction);
            Assert.Equal(1, (int)game.Action);
            Assert.Equal(executable, (string)game.ApplicationName, ignoreCase: true);
            Assert.Equal(WindowsServerFirewall.Grouping, (string)game.Grouping);
            Assert.Equal("10.0.0.5/255.255.255.255", (string)Rule(WindowsServerFirewall.RuleName(ShortId, "tv", PortProtocol.Udp))!.LocalAddresses);
            Assert.Null(Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Tcp))); // RCON's side stays closed
            // A development agent's server runs as the agent's user: blocking it would block the agent.
            Assert.Null(Rule(WindowsServerFirewall.PanelBlockName(ShortId)));

            // A new start whose manifest no longer names CSTV (a game update dropped it): its rule goes all the same.
            firewall.Allow(ShortId, executable, [new BoundPort("game", "0.0.0.0", 27016, [PortProtocol.Udp])], ProcessIdentity.CurrentUser);
            Assert.Equal("27016", (string)Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Udp))!.LocalPorts);
            Assert.Null(Rule(WindowsServerFirewall.RuleName(ShortId, "tv", PortProtocol.Udp)));
        }
        finally
        {
            firewall.Remove(ShortId);
        }

        Assert.Null(Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Udp)));
    }

    [AccountFact]
    public void TheServersAccountIsBlockedFromThePanelUpdatedInPlaceAndUntilTheServerGoes()
    {
        var accounts = TestAccounts.Create();
        var shortId = "t" + Guid.NewGuid().ToString("N")[..9];
        var name = WindowsServerAccounts.AccountName(shortId);
        var sid = accounts.Ensure(name, "Pulsar firewall test");
        var server = new ServerAccount(shortId, ProcessIdentity.Account(name));
        var firewall = Firewall(enabled: true);
        try
        {
            firewall.BlockPanel([server], s_targets);

            var block = Rule(WindowsServerFirewall.PanelBlockName(shortId))!;
            Assert.Equal(2, (int)block.Direction);
            Assert.Equal(0, (int)block.Action);
            Assert.Equal(6, (int)block.Protocol);
            Assert.Equal(WindowsServerFirewall.Grouping, (string)block.Grouping);
            Assert.Equal("8443,8444", (string)block.RemotePorts);
            Assert.Equal($"D:(A;;CC;;;{sid.Value})", (string)block.LocalUserAuthorizedList);
            var addresses = ((string)block.RemoteAddresses).Split(',');
            Assert.Contains("203.0.113.10/255.255.255.255", addresses);
            Assert.Contains("2001:db8::5-2001:db8::5", addresses);
            Assert.Contains("127.0.0.0/255.0.0.0", addresses);

            // A start replaces the inbound rules only.
            firewall.Allow(shortId, @"C:\PulsarServers\x\srcds.exe", [new BoundPort("game", "0.0.0.0", 27015, [PortProtocol.Udp])], server.Identity);
            Assert.Equal("8443,8444", (string)Rule(WindowsServerFirewall.PanelBlockName(shortId))!.RemotePorts);

            // A refresh changes the rule in place; one changed by hand is put back.
            firewall.BlockPanel([server], s_targets with { Ports = [9443] });
            Assert.Equal("9443", (string)Rule(WindowsServerFirewall.PanelBlockName(shortId))!.RemotePorts);
            Rule(WindowsServerFirewall.PanelBlockName(shortId))!.Enabled = false;
            firewall.BlockPanel([server], s_targets with { Ports = [9443] });
            Assert.True((bool)Rule(WindowsServerFirewall.PanelBlockName(shortId))!.Enabled);
            Assert.Equal(1, Count(WindowsServerFirewall.PanelBlockName(shortId)));

            // One that went missing is made again at the next start.
            Rules().Remove(WindowsServerFirewall.PanelBlockName(shortId));
            firewall.Allow(shortId, @"C:\PulsarServers\x\srcds.exe", [], server.Identity);
            Assert.Equal("9443", (string)Rule(WindowsServerFirewall.PanelBlockName(shortId))!.RemotePorts);

            // Deleting the server removes it with the others, and a refresh under way does not make it again.
            firewall.Remove(shortId);
            Assert.Null(Rule(WindowsServerFirewall.PanelBlockName(shortId)));
            firewall.BlockPanel([server], s_targets);
            Assert.Null(Rule(WindowsServerFirewall.PanelBlockName(shortId)));

            // So does unenroll, whether managing the firewall is on or not.
            Firewall(enabled: true).BlockPanel([server], s_targets);
            Assert.Equal(1, WindowsServerFirewall.RemoveAll([shortId]));
            Assert.Null(Rule(WindowsServerFirewall.PanelBlockName(shortId)));
        }
        finally
        {
            WindowsServerFirewall.RemoveAll([shortId]);
            accounts.Delete(name);
        }
    }

    [Theory]
    [InlineData("Pulsar abcd2345 game udp", true)]
    [InlineData("Pulsar abcd2345 rcon_old tcp", true)] // a port a game update dropped from the manifest
    [InlineData("Pulsar abcd2345 game", true)]
    [InlineData("Pulsar abcd2345 panel block", true)] // the account's outbound rule: removed with the others
    [InlineData("Pulsar abcd23456 game udp", false)] // another server whose id starts with this one
    [InlineData("Pulsar abcd2 game udp", false)]
    [InlineData("Pulsar abcd2345", false)] // no port part: not one of ours
    [InlineData("pulsar abcd2345 game udp", false)]
    public void EveryRuleOfTheServersPrefixIsTheServersWhateverPortItWasFor(string name, bool owned)
    {
        Assert.Equal(owned, WindowsServerFirewall.IsRuleOf("abcd2345", name));
    }

    [Fact]
    public void ThePanelBlockIsNamedLikeTheServersOtherRulesAndListsTheAccountOnly()
    {
        Assert.Equal("Pulsar abcd2345 panel block", WindowsServerFirewall.PanelBlockName("abcd2345"));
        Assert.True(WindowsServerFirewall.IsRuleOf("abcd2345", WindowsServerFirewall.PanelBlockName("abcd2345")));
        Assert.Equal("D:(A;;CC;;;S-1-5-21-1-2-3-1001)", WindowsServerFirewall.LocalUsers(new SecurityIdentifier("S-1-5-21-1-2-3-1001")));
    }

    [Theory]
    [InlineData("abcd2345", "pulsar_abcd2345", true)]
    [InlineData("abcd2345", "pulsar_efgh6789", false)] // another server's account
    [InlineData("abcd2345", "Administrator", false)]
    [InlineData("abcd2345", "PULSAR_abcd2345", false)]
    [InlineData("ABCD2345", "pulsar_ABCD2345", false)] // not a short id
    [InlineData("abcd2345,x", "pulsar_abcd2345,x", false)]
    public void OnlyTheServersOwnAccountIsBlocked(string shortId, string account, bool blocked)
    {
        Assert.Equal(blocked, WindowsServerFirewall.IsServerAccount(shortId, ProcessIdentity.Account(account)));
        Assert.False(WindowsServerFirewall.IsServerAccount(shortId, ProcessIdentity.CurrentUser));
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-5-21-1-2-3-500", true)] // a local account all the same (the name check above keeps it out)
    [InlineData("S-1-5-18", false)] // SYSTEM: the agent itself
    [InlineData("S-1-5-32-544", false)] // Administrators
    [InlineData("S-1-5-32-545", false)] // Users
    [InlineData("S-1-1-0", false)] // Everyone
    [InlineData("S-1-5-11", false)] // Authenticated Users
    [InlineData("S-1-5-19", false)] // LocalService
    public void NoBlockIsMadeForTheAgentSystemOrAGroup(string sid, bool blocked)
    {
        Assert.Equal(blocked, WindowsServerFirewall.MayBlock(new SecurityIdentifier(sid), self: new SecurityIdentifier("S-1-5-21-9-9-9-1000")));
        Assert.False(WindowsServerFirewall.MayBlock(new SecurityIdentifier("S-1-5-21-9-9-9-1000"), self: new SecurityIdentifier("S-1-5-21-9-9-9-1000")));
    }

    [WindowsFact]
    public void WhetherRulesTakeEffectIsReadWithoutChangingAnything()
    {
        var reason = WindowsServerFirewall.InertReason(); // reads the policy only

        Assert.True(reason is null || reason.Length > 0);
    }

    [WindowsFact]
    public void ADisabledFirewallTouchesNothing()
    {
        var firewall = Firewall(enabled: false);
        var server = new ServerAccount(ShortId, ProcessIdentity.Account("pulsar_" + ShortId));

        firewall.BlockPanel([server], s_targets);
        firewall.Allow(ShortId, @"C:\x\game.exe", [new BoundPort("game", "0.0.0.0", 27015, [PortProtocol.Udp])], server.Identity);

        Assert.False(firewall.Enabled);
        Assert.Null(Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Udp)));
        Assert.Null(Rule(WindowsServerFirewall.PanelBlockName(ShortId)));
    }

    private static WindowsServerFirewall Firewall(bool enabled) => new(enabled, NullLogger<WindowsServerFirewall>.Instance);

    private static dynamic Rules()
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        return policy.Rules;
    }

    private static dynamic? Rule(string name)
    {
        try
        {
            return Rules().Item(name);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException)
        {
            return null;
        }
    }

    private static int Count(string name)
    {
        var count = 0;
        foreach (var rule in Rules())
        {
            if (rule.Name is string n && n == name)
            {
                count++;
            }
        }

        return count;
    }
}
