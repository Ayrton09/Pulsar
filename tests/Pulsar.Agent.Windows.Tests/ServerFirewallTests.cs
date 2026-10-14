using System.Net;
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

    private static readonly IReadOnlyList<Uri> s_panels = [new("https://203.0.113.10:8444/"), new("https://[2001:db8::5]:8443/")];

    [AccountFact]
    public void RulesOpenOnlyTheDeclaredPortsAndProtocolsForTheGameAndGoAway()
    {
        var firewall = Firewall(enabled: true);
        var executable = @"C:\PulsarServers\fwtest01\srcds.exe";
        try
        {
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
    public void TheServersAccountIsBlockedFromThePanelUntilTheServerGoes()
    {
        var accounts = TestAccounts.Create();
        var shortId = "t" + Guid.NewGuid().ToString("N")[..9];
        var name = WindowsServerAccounts.AccountName(shortId);
        var sid = accounts.Ensure(name, "Pulsar firewall test");
        var firewall = Firewall(enabled: true);
        try
        {
            firewall.Allow(shortId, @"C:\PulsarServers\x\srcds.exe", [new BoundPort("game", "0.0.0.0", 27015, [PortProtocol.Udp])],
                ProcessIdentity.Account(name));

            var block = Rule(WindowsServerFirewall.PanelBlockName(shortId))!;
            Assert.Equal(2, (int)block.Direction);
            Assert.Equal(0, (int)block.Action);
            Assert.Equal(6, (int)block.Protocol);
            Assert.Equal(WindowsServerFirewall.Grouping, (string)block.Grouping);
            Assert.Equal("8443,8444", (string)block.RemotePorts);
            Assert.Contains(sid.Value, (string)block.LocalUserAuthorizedList, StringComparison.Ordinal);
            var addresses = ((string)block.RemoteAddresses).Split(',');
            Assert.Contains("203.0.113.10/255.255.255.255", addresses);
            Assert.Contains("2001:db8::5-2001:db8::5", addresses);
            Assert.Contains("127.0.0.0/255.0.0.0", addresses);

            // Each start replaces it; deleting the server removes it with the others.
            firewall.Allow(shortId, @"C:\PulsarServers\x\srcds.exe", [], ProcessIdentity.Account(name));
            Assert.NotNull(Rule(WindowsServerFirewall.PanelBlockName(shortId)));
            firewall.Remove(shortId);
            Assert.Null(Rule(WindowsServerFirewall.PanelBlockName(shortId)));

            // So does unenroll, whether managing the firewall is on or not.
            firewall.Allow(shortId, @"C:\PulsarServers\x\srcds.exe", [], ProcessIdentity.Account(name));
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
    public void ThePanelBlockIsNamedLikeTheServersOtherRules()
    {
        Assert.Equal("Pulsar abcd2345 panel block", WindowsServerFirewall.PanelBlockName("abcd2345"));
        Assert.True(WindowsServerFirewall.IsRuleOf("abcd2345", WindowsServerFirewall.PanelBlockName("abcd2345")));
    }

    [Fact]
    public void ThePanelBlockListsEveryAddressOfThePanelAndOfTheNodeOnThePanelsPorts()
    {
        List<string> resolved = [];
        IReadOnlyList<IPAddress> Resolve(string host)
        {
            resolved.Add(host);
            return host == "panel.example.com"
                ? [IPAddress.Parse("203.0.113.10"), IPAddress.Parse("2001:db8::5"), IPAddress.Parse("::ffff:203.0.113.10")]
                : throw new InvalidOperationException($"{host} is not resolved");
        }

        var block = WindowsServerFirewall.PanelBlockFor(new SecurityIdentifier("S-1-5-21-1-2-3-1001"),
            [new("https://panel.example.com:8444/"), new("https://panel.example.com/"), new("https://198.51.100.7:8443/"), new("https://[2001:db8::7]:8443/")],
            Resolve,
            [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("fe80::1%12"), IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("203.0.113.10")])!;

        Assert.Equal("D:(A;;CC;;;S-1-5-21-1-2-3-1001)", block.LocalUsers);
        // Loopback always (the firewall refuses ::1 itself: ::/127 is it and the unspecified address), each address once,
        // IPv4-mapped ones as IPv4, without a scope: then the node's own.
        Assert.Equal("127.0.0.0/8,::/127,203.0.113.10,2001:db8::5,198.51.100.7,2001:db8::7,192.168.1.20,fe80::1", block.RemoteAddresses);
        Assert.Equal("443,8443,8444", block.RemotePorts);
        Assert.Equal(["panel.example.com"], resolved); // literals are not looked up, and a name only once
    }

    [Fact]
    public void APanelWhoseNameDoesNotResolveIsStillBlockedOnTheNodesOwnAddresses()
    {
        var block = WindowsServerFirewall.PanelBlockFor(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), [new("https://panel.example.com:8443/")],
            _ => [], [IPAddress.Parse("10.0.0.4"), IPAddress.Any, IPAddress.IPv6Any])!;

        Assert.Equal("127.0.0.0/8,::/127,10.0.0.4", block.RemoteAddresses);
        Assert.Equal("8443", block.RemotePorts);
    }

    [Fact]
    public void WithoutAPanelThereIsNothingToBlock()
    {
        Assert.Null(WindowsServerFirewall.PanelBlockFor(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), [], _ => [], [IPAddress.Parse("10.0.0.4")]));
    }

    [WindowsFact]
    public void ADisabledFirewallTouchesNothing()
    {
        var firewall = Firewall(enabled: false);

        firewall.Allow(ShortId, @"C:\x\game.exe", [new BoundPort("game", "0.0.0.0", 27015, [PortProtocol.Udp])], ProcessIdentity.Account("pulsar_" + ShortId));

        Assert.False(firewall.Enabled);
        Assert.Null(Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Udp)));
        Assert.Null(Rule(WindowsServerFirewall.PanelBlockName(ShortId)));
    }

    private static WindowsServerFirewall Firewall(bool enabled) =>
        new(enabled, () => s_panels, NullLogger<WindowsServerFirewall>.Instance);

    private static dynamic? Rule(string name)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        try
        {
            return policy.Rules.Item(name);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException)
        {
            return null;
        }
    }
}
