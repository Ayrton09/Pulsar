using Microsoft.Extensions.Hosting;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent;

/// <summary>
/// Stopped first of the agent's services (registered last): when the machine is shutting down, the game servers stop
/// with the agent instead of being left in their hosts, which would die with the machine (docs/architecture/06, "Server
/// hosts"). Windows says so to the service itself; this asks the platform for the others.
/// </summary>
internal sealed class MachineShutdownCheck(AgentShutdown shutdown, Func<bool> machineShuttingDown) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (machineShuttingDown())
        {
            shutdown.StopServers = true;
        }

        return Task.CompletedTask;
    }
}
