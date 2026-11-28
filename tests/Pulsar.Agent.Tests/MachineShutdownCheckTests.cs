using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Tests;

/// <summary>A machine shutting down stops the game servers with the agent; a plain stop of the agent leaves them running.</summary>
public sealed class MachineShutdownCheckTests
{
    [Fact]
    public async Task AMachineShuttingDownStopsTheServersWithTheAgent()
    {
        var shutdown = new AgentShutdown();

        await new MachineShutdownCheck(shutdown, () => true).StopAsync(TestContext.Current.CancellationToken);

        Assert.True(shutdown.StopServers);
    }

    [Fact]
    public async Task APlainStopLeavesThemRunningInTheirHosts()
    {
        var shutdown = new AgentShutdown();

        await new MachineShutdownCheck(shutdown, () => false).StopAsync(TestContext.Current.CancellationToken);

        Assert.False(shutdown.StopServers);
    }

    [Fact]
    public async Task AStopAlreadyAskedForStaysAskedFor()
    {
        var shutdown = new AgentShutdown { StopServers = true };

        await new MachineShutdownCheck(shutdown, () => false).StopAsync(TestContext.Current.CancellationToken);

        Assert.True(shutdown.StopServers);
    }
}
