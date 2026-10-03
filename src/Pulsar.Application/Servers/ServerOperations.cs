using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>
/// Starts operations on servers (docs/architecture/05): takes the server for the operation, records it and sends its
/// command. A refused or unanswered command fails the operation at once (and releases the server), so nothing waits
/// for a result that will never come; the agent reports the end of an accepted one (<see cref="AgentServerEvents"/>).
/// </summary>
public sealed partial class ServerOperations(IPulsarDbContext db, IAgentConnections connections, TimeProvider clock, ILogger<ServerOperations> logger)
{
    /// <param name="beforeSave">Adds what belongs with the operation (its backup row) to the same first save.</param>
    /// <returns>The operation (failed already when the node did not accept it), or why the server could not take it.</returns>
    public async Task<Result<Operation>> RunAsync(Server server, Operation operation, AgentCommand command, Action? beforeSave,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(operation);
        var taken = server.BeginOperation(operation, clock.GetUtcNow());
        if (!taken.IsSuccess)
        {
            return taken.Error!;
        }

        db.Operations.Add(operation);
        beforeSave?.Invoke();
        await db.SaveChangesAsync(cancellationToken);

        var ack = await connections.SendAsync(server.NodeId, command, operation.RequestedBy, cancellationToken, operation.Id.Value);
        var now = clock.GetUtcNow();
        if (ack?.Status is AckStatus.Accepted or AckStatus.AlreadyInState)
        {
            operation.Started(now);
        }
        else
        {
            var code = ack?.ErrorCode ?? (ack is null ? NodeErrors.NotConnected.Code : ServerErrors.CommandRejected.Code);
            operation.Complete(OperationStatus.Failed, code, ack?.Message, now);
            server.OperationCompleted(operation, now);
            LogOperationRefused(operation.Id, operation.Type, server.Id, code);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The agent already reported the result (a quick operation): it wins over "started". Both rows are read
            // again (a deleted server becomes detached).
            LogOperationAlreadyReported(operation.Id);
            await db.Entry(operation).ReloadAsync(cancellationToken);
            await db.Entry(server).ReloadAsync(cancellationToken);
        }

        return operation;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Operation {OperationId} finished before its start was saved")]
    private partial void LogOperationAlreadyReported(OperationId operationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} ({Type}) on server {ServerId} not accepted by the node: {Code}")]
    private partial void LogOperationRefused(OperationId operationId, OperationType type, ServerId serverId, string code);
}
