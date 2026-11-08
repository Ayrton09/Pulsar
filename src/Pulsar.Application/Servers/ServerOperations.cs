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
public sealed partial class ServerOperations(IPulsarDbContext db, IAgentConnections connections, CommandTurns<ServerId> specTurns,
    TimeProvider clock, ILogger<ServerOperations> logger)
{
    /// <param name="beforeSave">Adds what belongs with the operation (its backup row) to the same first save.</param>
    /// <returns>The operation (failed already when the node did not accept it), or why the server could not take it.</returns>
    public async Task<Result<Operation>> RunAsync(Server server, Operation operation, AgentCommand command, Action? beforeSave,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(operation);
        // A provisioning and a settings sync share the turn: no spec read before a reinstall can arrive after it,
        // even when it uses the same manifest. Saves are committed before the agent is called, as for every operation.
        using var turn = command is ProvisionServerCommand ? await specTurns.AcquireAsync(server.Id, cancellationToken) : null;
        // While a transfer runs only its own operations (the panel's) change the server: a restore, a mod or a zip
        // otherwise lands on a copy the transfer is leaving or still filling.
        if (operation.RequestedVia != OperationRequester.System && await TransferGate.RunningAsync(db, server.Id, cancellationToken))
        {
            return ServerErrors.Transferring;
        }

        var taken = server.BeginOperation(operation, clock.GetUtcNow());
        if (!taken.IsSuccess)
        {
            return taken.Error!;
        }

        var before = db.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        db.Operations.Add(operation);
        beforeSave?.Invoke();
        if (await SaveBegunAsync(server, operation, before, cancellationToken) is { } notBegun)
        {
            return notBegun;
        }

        // Recorded: from here on the request going away changes nothing (the ack's own timeout still applies), so the
        // operation never stays queued for a command whose fate nobody waited for.
        CommandAck? ack;
        try
        {
            // To the operation's node: the server's own, or (a transfer's, 1.0.16) the node its old or new copy is on.
            ack = await connections.SendAsync(operation.NodeId, command, operation.RequestedBy, CancellationToken.None, operation.Id.Value);
        }
        catch (Exception ex)
        {
            LogOperationRefused(operation.Id, operation.Type, server.Id, ex.GetType().Name);
            try
            {
                await SaveOutcomeAsync(server, operation, () =>
                {
                    var failedAt = clock.GetUtcNow();
                    operation.Complete(OperationStatus.Failed, ServerErrors.CommandRejected.Code, "The panel could not send the command.", failedAt);
                    server.OperationCompleted(operation, failedAt);
                });
            }
            catch (Exception saving) when (saving is not OperationCanceledException)
            {
                // The send's own failure is what the caller needs to see, not the one of recording it.
                LogOutcomeNotSaved(operation.Id, saving);
            }

            throw;
        }

        var now = clock.GetUtcNow();
        if (ack?.Status is AckStatus.Accepted or AckStatus.AlreadyInState)
        {
            await StartedAsync(operation, now);
            return operation;
        }

        var refused = ack?.ErrorCode ?? (ack is null ? NodeErrors.NotConnected.Code : ServerErrors.CommandRejected.Code);
        LogOperationRefused(operation.Id, operation.Type, server.Id, refused);
        await SaveOutcomeAsync(server, operation, () =>
        {
            operation.Complete(OperationStatus.Failed, refused, ack?.Message, now);
            server.OperationCompleted(operation, now);
        });

        return operation;
    }

    /// <summary>
    /// The node accepted: the operation is running (<see cref="Operation.Started"/>), unless it is no longer queued (the
    /// agent reported its result already: a quick operation's result wins over "started"). One statement on its row, so
    /// the agent's reports on it meanwhile (each new stage of a backup changes the row) can neither make it fail nor be
    /// overwritten; the operation is then read back as it is.
    /// </summary>
    /// <summary>
    /// The first save of a begun operation. An agent's report that saved the same server row in between (its version
    /// moved on) is not the user's error: the operation is begun again on the fresh row, its checks run again, and what
    /// the caller added stays to be saved with it. When it still cannot be, nothing of it is left tracked for a later save
    /// of the same context (a scheduled run records its outcome after this) and the server is said to be busy.
    /// </summary>
    private async Task<DomainError?> SaveBegunAsync(Server server, Operation operation, HashSet<object> before, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return null;
            }
            catch (DbUpdateConcurrencyException ex) when (ex.Entries.All(e => e.Entity is Server))
            {
                await db.Entry(server).ReloadAsync(cancellationToken);
                var again = attempt < 3 ? server.BeginOperation(operation, clock.GetUtcNow()) : Result.Failure(ServerErrors.Busy);
                if (!again.IsSuccess)
                {
                    foreach (var entry in db.ChangeTracker.Entries().Where(e => !before.Contains(e.Entity)).ToList())
                    {
                        entry.State = EntityState.Detached;
                    }

                    foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified && e.Entity != server).ToList())
                    {
                        await entry.ReloadAsync(cancellationToken);
                    }

                    return again.Error;
                }
            }
        }
    }

    private async Task StartedAsync(Operation operation, DateTimeOffset now)
    {
        await db.Operations.Where(o => o.Id == operation.Id && o.Status == OperationStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OperationStatus.Running).SetProperty(o => o.StartedAt, now), CancellationToken.None);
        await db.Entry(operation).ReloadAsync(CancellationToken.None);
    }

    /// <summary>
    /// Applies a failure (the node did not take the command, or it could not be sent) and saves it. When the agent changed
    /// the server meanwhile (a state event) or the operation ended otherwise, both rows are read again and the outcome is
    /// applied on them, unless the operation has ended (a deleted row becomes detached).
    /// </summary>
    private async Task SaveOutcomeAsync(Server server, Operation operation, Action apply)
    {
        apply();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 4)
            {
                await db.Entry(operation).ReloadAsync(CancellationToken.None);
                await db.Entry(server).ReloadAsync(CancellationToken.None);
                if (!operation.IsActive || db.Entry(operation).State == EntityState.Detached)
                {
                    LogOperationAlreadyReported(operation.Id);
                    return;
                }

                apply();
            }
        }
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Operation {OperationId} could not be recorded as failed after its command could not be sent")]
    private partial void LogOutcomeNotSaved(OperationId operationId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} ({Type}) on server {ServerId} not accepted by the node: {Code}")]
    private partial void LogOperationRefused(OperationId operationId, OperationType type, ServerId serverId, string code);
}
