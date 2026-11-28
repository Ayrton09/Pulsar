using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent;

/// <summary>
/// The service's lifetime, which also tells why the service stops (docs/architecture/06, "Server hosts"): a stop or a
/// restart leaves the game servers running in their hosts, while Windows shutting down stops them gracefully first (they
/// would die with the machine otherwise) and they start again with the agent.
/// </summary>
internal sealed class AgentServiceLifetime(
    AgentShutdown shutdown,
    IHostEnvironment environment,
    IHostApplicationLifetime applicationLifetime,
    ILoggerFactory loggerFactory,
    IOptions<HostOptions> hostOptions,
    IOptions<WindowsServiceLifetimeOptions> serviceOptions)
    : WindowsServiceLifetime(environment, applicationLifetime, loggerFactory, hostOptions, serviceOptions)
{
    protected override void OnShutdown()
    {
        shutdown.StopServers = true;
        base.OnShutdown();
    }
}
