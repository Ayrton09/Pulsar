namespace Pulsar.Contracts;

/// <summary>Methods the agent calls on the panel's agent hub (<see cref="AgentProtocol.HubPath"/>).</summary>
public interface IAgentHub
{
    /// <summary>First message after connecting; the panel answers with its time and keys (or closes the connection).</summary>
    Task<HelloAccepted> Hello(AgentHello hello);

    /// <summary>Node-level metrics, every 15 s.</summary>
    Task NodeMetrics(NodeMetrics metrics);
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
public sealed record AgentHello(
    string AgentVersion,
    int ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    NodeInventory Inventory,
    IReadOnlyList<string>? PinnedPanelKeys = null);

/// <summary><see cref="ServerTime"/> lets the agent detect clock skew (command expiry is a secondary defense).</summary>
public sealed record HelloAccepted(DateTimeOffset ServerTime, IReadOnlyList<PanelKey> PanelKeys);

public sealed record NodeMetrics(DateTimeOffset At, double CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, TimeSpan Uptime);
