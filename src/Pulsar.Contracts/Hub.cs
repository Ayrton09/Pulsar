namespace Pulsar.Contracts;

/// <summary>Methods the agent calls on the panel's agent hub (<see cref="AgentProtocol.HubPath"/>).</summary>
public interface IAgentHub
{
    /// <summary>First message after connecting; the panel answers with its time and keys (or closes the connection).</summary>
    Task<HelloAccepted> Hello(AgentHello hello);

    /// <summary>Node-level metrics, every 15 s.</summary>
    Task NodeMetrics(NodeMetrics metrics);

    /// <summary>A server changed state. Not retried: the next Hello carries every server's state anyway.</summary>
    Task ServerStateChanged(ServerStateChanged change);

    Task OperationProgress(OperationProgress progress);

    /// <summary>The end of an operation; the agent keeps it and sends it again until this call succeeds.</summary>
    Task OperationCompleted(OperationCompleted completed);

    /// <summary>Console output of a server the panel subscribed to (<see cref="SubscribeConsoleCommand"/>).</summary>
    Task ConsoleOutput(ServerConsoleOutput output);

    /// <summary>Usage of the running servers, every 5 s.</summary>
    Task Metrics(MetricsBatch batch);

    /// <summary>The end of a transfer the agent received (<see cref="ReceiveFileCommand"/>).</summary>
    Task TransferCompleted(TransferResult result);
}

/// <summary>Methods the panel calls on a connected agent (SignalR client results).</summary>
public interface IAgentClient
{
    Task<CommandAck> Execute(SignedMessage command);
}

/// <param name="PinnedPanelKeys">
/// Key ids of the panel keys the agent trusts. The panel signs this connection's commands with one of them and, when they
/// differ from its own, sends <see cref="UpdatePanelKeysCommand"/>. Null from agents that predate panel key rotation.
/// </param>
/// <param name="Servers">The actual state of every server the agent knows (docs/architecture/05, "Reconciliation").</param>
/// <param name="OperationsInFlight">Operations the agent is still running; any other one the panel thinks is running was interrupted.</param>
public sealed record AgentHello(
    string AgentVersion,
    int ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    NodeInventory Inventory,
    IReadOnlyList<string>? PinnedPanelKeys = null,
    IReadOnlyList<ServerSnapshot>? Servers = null,
    IReadOnlyList<Guid>? OperationsInFlight = null);

/// <summary><see cref="ServerTime"/> lets the agent detect clock skew (command expiry is a secondary defense).</summary>
/// <param name="ConsoleSubscriptions">
/// Consoles browsers are watching: the agent sends their output from the given sequence on, as after a
/// <see cref="SubscribeConsoleCommand"/>. Subscriptions end with the connection.
/// </param>
public sealed record HelloAccepted(DateTimeOffset ServerTime, IReadOnlyList<PanelKey> PanelKeys,
    IReadOnlyList<ConsoleSubscription>? ConsoleSubscriptions = null);

public sealed record NodeMetrics(DateTimeOffset At, double CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, TimeSpan Uptime);
