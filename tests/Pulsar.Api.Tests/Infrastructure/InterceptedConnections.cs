using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// The panel's agent connections with <paramref name="send"/> in front of every command: it may hold the command
/// back, answer for the agent or fail, and calls <c>next</c> to send it for real (with the token it is given).
/// </summary>
public sealed class InterceptedConnections(
    IAgentConnections inner,
    Func<AgentCommand, Func<CancellationToken, Task<CommandAck?>>, CancellationToken, Task<CommandAck?>> send) : IAgentConnections
{
    public AgentConnectionInfo? Find(NodeId node) => inner.Find(node);

    public void Disconnect(NodeId node) => inner.Disconnect(node);

    public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);

    public Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null) =>
        send(command, token => inner.SendAsync(node, command, actor, token, operationId), cancellationToken);
}
