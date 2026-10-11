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
/// command. A refused or unanswered command, or one the panel could not send, fails the operation at once (and releases
/// the server), so nothing waits for a result that will never come; the agent reports the end of an accepted one
/// (<see cref="AgentServerEvents"/>).
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

        // Recorded: from here on the request going away changes nothing (the ack's own timeout still applies), so the
        // operation never stays queued for a command whose fate nobody waited for.
        CommandAck? ack;
        try
        {
            ack = await connections.SendAsync(server.NodeId, command, operation.RequestedBy, CancellationToken.None, operation.Id.Value);
        }
        catch (Exception ex)
        {
            var failedAt = clock.GetUtcNow();
            operation.Complete(OperationStatus.Failed, ServerErrors.CommandRejected.Code, "The panel could not send the command.", failedAt);
            server.OperationCompleted(operation, failedAt);
            LogOperationRefused(operation.Id, operation.Type, server.Id, ex.GetType().Name);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

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
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The agent already reported the result (a quick operation): it wins over "started". Both rows are read
            // again (a deleted server becomes detached).
            LogOperationAlreadyReported(operation.Id);
            await db.Entry(operation).ReloadAsync(CancellationToken.None);
            await db.Entry(server).ReloadAsync(CancellationToken.None);
        }

        return operation;
    }

    /// <summary>
    /// An operation the panel could not even send (what it needs is missing): recorded as failed with why, so the server's
    /// page says so. Nothing is recorded when the server is busy with another one.
    /// </summary>
    public async Task FailAsync(Server server, Operation operation, DomainError error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(error);
        var now = clock.GetUtcNow();
        if (!server.BeginOperation(operation, now).IsSuccess)
        {
            return;
        }

        db.Operations.Add(operation);
        operation.Complete(OperationStatus.Failed, error.Code, error.Message, now);
        server.OperationCompleted(operation, now);
        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Operation {OperationId} finished before its start was saved")]
    private partial void LogOperationAlreadyReported(OperationId operationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} ({Type}) on server {ServerId} not accepted by the node: {Code}")]
    private partial void LogOperationRefused(OperationId operationId, OperationType type, ServerId serverId, string code);
}
