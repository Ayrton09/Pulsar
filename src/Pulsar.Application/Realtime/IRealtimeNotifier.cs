using Pulsar.Domain.Common;

namespace Pulsar.Application.Realtime;

/// <summary>
/// Tells the browsers watching the panel that something changed (docs/architecture/07, "Real time"), so they refresh it
/// instead of polling. Fire and forget: a browser that misses a notice refreshes everything when it reconnects.
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>Servers whose state, settings or operations changed (stored ones; the browser fetches them again).</summary>
    void ServersChanged(IReadOnlyCollection<ServerId> servers);

    /// <summary>Nodes that changed: stored data, or their connection.</summary>
    void NodesChanged(IReadOnlyCollection<NodeId> nodes);

    /// <summary>Live progress of an operation (stored only now and then, so it is pushed as is).</summary>
    void OperationProgress(ServerId server, Guid operationId, string stage, decimal? percent);
}

/// <summary>For hosts without browsers (tests of the application layer, the migration command).</summary>
internal sealed class NoRealtimeNotifier : IRealtimeNotifier
{
    public void ServersChanged(IReadOnlyCollection<ServerId> servers)
    {
    }

    public void NodesChanged(IReadOnlyCollection<NodeId> nodes)
    {
    }

    public void OperationProgress(ServerId server, Guid operationId, string stage, decimal? percent)
    {
    }
}
