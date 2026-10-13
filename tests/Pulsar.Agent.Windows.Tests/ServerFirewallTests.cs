using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>Real Windows Firewall rules: changes the machine's firewall, so only on a disposable elevated machine (CI).</summary>
public sealed class ServerFirewallTests
{
    private const string ShortId = "fwtest01";

    [AccountFact]
    public void RulesOpenOnlyTheDeclaredPortsAndProtocolsForTheGameAndGoAway()
    {
        var firewall = new WindowsServerFirewall(enabled: true, NullLogger<WindowsServerFirewall>.Instance);
        var executable = @"C:\PulsarServers\fwtest01\srcds.exe";
        try
        {
            firewall.Allow(ShortId, executable,
                [new BoundPort("game", "0.0.0.0", 27015, [PortProtocol.Udp]), new BoundPort("tv", "10.0.0.5", 27020, [PortProtocol.Udp])]);

            var game = Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Udp))!;
            Assert.Equal("27015", (string)game.LocalPorts);
            Assert.Equal(17, (int)game.Protocol);
            Assert.Equal(1, (int)game.Direction);
            Assert.Equal(1, (int)game.Action);
            Assert.Equal(executable, (string)game.ApplicationName, ignoreCase: true);
            Assert.Equal(WindowsServerFirewall.Grouping, (string)game.Grouping);
            Assert.Equal("10.0.0.5/255.255.255.255", (string)Rule(WindowsServerFirewall.RuleName(ShortId, "tv", PortProtocol.Udp))!.LocalAddresses);
            Assert.Null(Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Tcp))); // RCON's side stays closed

            // A new start whose manifest no longer names CSTV (a game update dropped it): its rule goes all the same.
            firewall.Allow(ShortId, executable, [new BoundPort("game", "0.0.0.0", 27016, [PortProtocol.Udp])]);
            Assert.Equal("27016", (string)Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Udp))!.LocalPorts);
            Assert.Null(Rule(WindowsServerFirewall.RuleName(ShortId, "tv", PortProtocol.Udp)));
        }
        finally
        {
            firewall.Remove(ShortId);
        }

        Assert.Null(Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Udp)));
    }

    [Theory]
    [InlineData("Pulsar abcd2345 game udp", true)]
    [InlineData("Pulsar abcd2345 rcon_old tcp", true)] // a port a game update dropped from the manifest
    [InlineData("Pulsar abcd2345 game", true)]
    [InlineData("Pulsar abcd23456 game udp", false)] // another server whose id starts with this one
    [InlineData("Pulsar abcd2 game udp", false)]
    [InlineData("Pulsar abcd2345", false)] // no port part: not one of ours
    [InlineData("pulsar abcd2345 game udp", false)]
    public void EveryRuleOfTheServersPrefixIsTheServersWhateverPortItWasFor(string name, bool owned)
    {
        Assert.Equal(owned, WindowsServerFirewall.IsRuleOf("abcd2345", name));
    }

    [WindowsFact]
    public void ADisabledFirewallTouchesNothing()
    {
        var firewall = new WindowsServerFirewall(enabled: false, NullLogger<WindowsServerFirewall>.Instance);

        firewall.Allow(ShortId, @"C:\x\game.exe", [new BoundPort("game", "0.0.0.0", 27015, [PortProtocol.Udp])]);

        Assert.False(firewall.Enabled);
        Assert.Null(Rule(WindowsServerFirewall.RuleName(ShortId, "game", PortProtocol.Udp)));
    }

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
