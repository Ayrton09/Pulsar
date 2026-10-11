using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Agents;

/// <summary>What the panel keeps of the inventory an agent reports.</summary>
public sealed class NodeInventoryTests
{
    // Format characters outside the BMP (tag characters, surrogate pairs) hide in a name as well as the others.
    [Theory]
    [InlineData("GAME​-01")]
    [InlineData("GAME‮-01")]
    [InlineData("GAME\U000E0001-01")]
    [InlineData("GAME\U000E0041\U000E007F-01")]
    public void TheHostnameKeepsOnlyWhatIsShown(string hostname)
    {
        var inventory = new NodeInventory(hostname, "Windows", "26200", null, 8, 16L << 30, null);

        Assert.Equal("GAME-01", AgentAccess.ToSnapshot(inventory, "1.0.0", AgentProtocol.CurrentVersion).Hostname);
        Assert.Equal("GAME \U0001F680 01", AgentAccess.ToSnapshot(inventory with { Hostname = "GAME \U0001F680 01" }, "1.0.0",
            AgentProtocol.CurrentVersion).Hostname);
    }
}
