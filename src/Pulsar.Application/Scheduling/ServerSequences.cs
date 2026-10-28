using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Scheduling;

/// <summary>Takes due sequences for one runner. Implemented with PostgreSQL row locks.</summary>
public interface IServerSequenceClaims
{
    /// <summary>
    /// Running sequences due at <paramref name="now"/>, oldest first, locked until the current transaction ends; those
    /// another panel holds are skipped. Must run inside a transaction.
    /// </summary>
    Task<IReadOnlyList<ServerSequence>> LockDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

/// <summary>A sequence acting: audited as its task (the schedule) or as the game updates; the entries of one share its id.</summary>
public sealed class SequenceRequestContext(ServerSequence sequence) : IRequestContext
{
    public UserId? UserId => null;

    public AuditActor Actor { get; } = sequence.Origin == SequenceOrigin.Schedule
        ? new(AuditActorType.Schedule, null, sequence.Name)
        : new(AuditActorType.System, null, sequence.Name);

    public AuditRequestInfo RequestInfo { get; } = new(null, null, sequence.Id.Value.ToString());
}

/// <param name="Taken">Sequences taken in this pass.</param>
/// <param name="More">More were due than one pass takes: run again right away.</param>
public sealed record SequencePass(int Taken, bool More);

/// <summary>
/// Runs sequences of steps on servers (Pulsar 1.0.9): an automatic game update (warn, stop, update, start again), a
/// scheduled task of several steps. A pass takes the due ones under row locks and moves them on: a step that only acts
/// (a command, a message) is followed by the next at once; one that waits for the server (to stop, to start) or for an
/// operation (a backup, an update) looks again every <see cref="PollInterval"/>. Each step goes through the same services
/// users go through, as the sequence, so every rule and audit entry applies. A step's state is stored after it acts: a
/// panel that stopped in the middle does it again, which every step bears (a stop of a stopped server is nothing).
/// </summary>
public sealed partial class ServerSequences(IServiceScopeFactory scopes, SequenceSignal signal, TimeProvider clock, ILogger<ServerSequences> logger)
{
    public const int BatchSize = 50;

    /// <summary>How often a step waiting for the server or an operation looks again.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>A taken sequence nobody moved on for this long was left by a panel that stopped: another pass takes it.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>How long a stop may take, the game's own stop timeout included.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(15);

    /// <summary>How long a start (or a restart) may take to report running: large games load for minutes.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Ended sequences kept per server (older ones are deleted).</summary>
    public const int KeptPerServer = 20;

    // Steps done in one go before the sequence goes back in line (commands and messages follow each other at once).
    private const int MaxStepsAtOnce = 16;

    /// <summary>Starts <paramref name="steps"/> on the server; refused while another sequence runs on it.</summary>
    public static async Task<Result<ServerSequence>> StartAsync(IPulsarDbContext db, IRowLocks locks, Server server, SequenceOrigin origin, string name,
        IReadOnlyList<SequenceStep> steps, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(server);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A sequence is started inside a transaction.");
        }

        // With the server's row locked: two sequences asked for at the same moment cannot both start.
        await locks.LockServerAsync(server.Id, cancellationToken);
        if (await db.ServerSequences.AnyAsync(s => s.ServerId == server.Id && s.Status == SequenceStatus.Running, cancellationToken))
        {
            return ScheduleErrors.SequenceRunning;
        }

        var running = server.RuntimeState is ServerRuntimeState.Starting or ServerRuntimeState.Running;
        var started = ServerSequence.Start(server.Id, origin, name, steps, running, now);
        if (started.IsSuccess)
        {
            db.ServerSequences.Add(started.Value);
        }

        return started;
    }

    /// <summary>Takes the sequences due at <paramref name="now"/> and moves each one on.</summary>
    public async Task<SequencePass> RunDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<ServerSequenceId> taken;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var due = await scope.ServiceProvider.GetRequiredService<IServerSequenceClaims>().LockDueAsync(now, BatchSize, cancellationToken);
            foreach (var sequence in due)
            {
                sequence.WaitUntil(now + Lease);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            taken = [.. due.Select(s => s.Id)];
        }

        await Task.WhenAll(taken.Select(id => AdvanceAsync(id, cancellationToken)));
        return new SequencePass(taken.Count, taken.Count == BatchSize);
    }

    /// <summary>When the next running sequence is due (UTC), if any.</summary>
    public async Task<DateTimeOffset?> NextWakeAtAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ServerSequences.AsNoTracking()
            .Where(s => s.Status == SequenceStatus.Running)
            .MinAsync(s => (DateTimeOffset?)s.WakeAt, cancellationToken);
    }

    /// <summary>Moves a taken sequence on, in a scope of its own acting as the sequence. A step's failure is the sequence's, never thrown.</summary>
    public async Task AdvanceAsync(ServerSequenceId id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IPulsarDbContext>();
        var sequence = await db.ServerSequences.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (sequence is null)
        {
            return; // deleted with its server
        }

        services.GetRequiredService<RequestContextOverride>().Context = new SequenceRequestContext(sequence);
        try
        {
            for (var done = 1; sequence.Current is { } step; done++)
            {
                // Held while the step acts: the services it goes through save with this same context, the sequence with
                // them, and a sequence saved as due would be taken by another pass in the middle of its step.
                var now = clock.GetUtcNow();
                sequence.WaitUntil(now + Lease);
                await StepAsync(services, db, sequence, step, now, cancellationToken);
                var next = sequence.Status == SequenceStatus.Running && sequence.WakeAt <= clock.GetUtcNow();
                if (next && done < MaxStepsAtOnce)
                {
                    sequence.WaitUntil(clock.GetUtcNow() + Lease); // the next step follows at once, still held
                    await db.SaveChangesAsync(CancellationToken.None);
                    continue;
                }

                await db.SaveChangesAsync(CancellationToken.None); // a step that waits, the end, or another pass's turn
                break;
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return; // cancelled by a user meanwhile (or its server deleted): it stays as they left it
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // the panel stops: its lease runs out and a pass takes it again
        }

        if (sequence.Status != SequenceStatus.Running)
        {
            LogEnded(sequence.Id, sequence.ServerId, sequence.Origin, sequence.Status, sequence.ErrorCode);
            await services.GetRequiredService<GameUpdates>().SequenceEndedAsync(sequence, CancellationToken.None);
            await services.GetRequiredService<Scheduler>().SequenceEndedAsync(sequence, CancellationToken.None);
            await PruneAsync(db, sequence.ServerId, CancellationToken.None);
        }

        signal.Notify();
    }

    private async Task StepAsync(IServiceProvider services, IPulsarDbContext db, ServerSequence sequence, SequenceStep step, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sequence.ServerId, cancellationToken);
        if (server is null)
        {
            sequence.StepFailed(ServerErrors.NotFound.Code, ServerErrors.NotFound.Message, now);
            return;
        }

        try
        {
            switch (step.Kind)
            {
                case SequenceStepKind.Command:
                    await CommandAsync(services, sequence, server, step.Text!.Trim(), now, cancellationToken);
                    break;
                case SequenceStepKind.Broadcast:
                    await BroadcastAsync(services, db, sequence, server, step.Text!, now, cancellationToken);
                    break;
                case SequenceStepKind.Wait when !sequence.StepBegun:
                    sequence.Began(now, Observed(server));
                    sequence.WaitUntil(now + TimeSpan.FromSeconds(step.Seconds));
                    break;
                case SequenceStepKind.Wait:
                    sequence.StepDone(now);
                    break;
                case SequenceStepKind.Stop:
                    await StopAsync(services, sequence, server, now, cancellationToken);
                    break;
                case SequenceStepKind.Start or SequenceStepKind.Restart:
                    await StartAsync(services, sequence, server, step, now, cancellationToken);
                    break;
                case SequenceStepKind.Backup or SequenceStepKind.Update:
                    await OperationAsync(services, db, sequence, server, step, now, cancellationToken);
                    break;
                case SequenceStepKind.UpgradeTemplate:
                    await TemplateAsync(services, db, sequence, server, step, now, cancellationToken);
                    break;
                case SequenceStepKind.CopyFrom:
                    await CopyFromAsync(services, db, sequence, server, new ServerId(Guid.Parse(step.Text!)), now, cancellationToken);
                    break;
                case SequenceStepKind.MoveTo when ServerSequence.TryParseMove(step.Text, out var from, out var to):
                    await MoveToAsync(services, db, sequence, server, new NodeId(from), new NodeId(to), now, cancellationToken);
                    break;
                default:
                    sequence.StepFailed("schedules.step_unknown", $"Unknown step {step.Kind}.", now);
                    break;
            }
        }
        catch (DomainErrorException ex)
        {
            sequence.StepFailed(ex.Error.Code, ex.Error.Message, now); // a template this panel refuses, say
        }
    }

    private static async Task CommandAsync(IServiceProvider services, ServerSequence sequence, Server server, string line, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!IsRunning(server))
        {
            sequence.StepDone(now); // a command for a game that does not run is moot
            return;
        }

        var sent = await services.GetRequiredService<ConsoleCommands>().SendAsync(server.Id, line, cancellationToken);
        if (Refusal(sent) is { } refusal)
        {
            sequence.StepFailed(refusal.Code, refusal.Message, now);
            return;
        }

        sequence.StepDone(now);
    }

    /// <summary>A message to the players: the game's broadcast command, when it has one and runs. A message that does not get through fails nothing.</summary>
    private async Task BroadcastAsync(IServiceProvider services, IPulsarDbContext db, ServerSequence sequence, Server server, string message, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (IsRunning(server))
        {
            var manifest = ServerSpecs.Parse(await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken));
            if (GameCommands.Broadcast(manifest, message) is { } command
                && Refusal(await services.GetRequiredService<ConsoleCommands>().SendAsync(server.Id, command, cancellationToken)) is { } refusal)
            {
                LogBroadcastFailed(server.Id, refusal.Code);
            }
        }

        sequence.StepDone(now);
    }

    private static async Task StopAsync(IServiceProvider services, ServerSequence sequence, Server server, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stopped = server.RuntimeState is ServerRuntimeState.Offline or ServerRuntimeState.Crashed;
        if (!sequence.StepBegun)
        {
            if (stopped)
            {
                sequence.StepDone(now);
                return;
            }

            var observed = Observed(server);
            if (Refusal(await services.GetRequiredService<ServerManagement>().StopAsync(server.Id, cancellationToken)) is { } refusal)
            {
                sequence.StepFailed(refusal.Code, refusal.Message, now);
                return;
            }

            sequence.Began(now, observed);
            sequence.WaitUntil(now + PollInterval);
        }
        else if (stopped)
        {
            sequence.StepDone(now);
        }
        else if (server.DesiredState == DesiredState.Running && sequence.ChangedSince(server.StateSequence, server.StateObservedAt)
                 && server.RuntimeState == ServerRuntimeState.Running)
        {
            sequence.StepFailed(ScheduleErrors.StepInterfered.Code, "The server was started again while the step waited for it to stop.", now);
        }
        else if (now - sequence.StepStartedAt >= StopTimeout)
        {
            sequence.StepFailed(ScheduleErrors.StepTimedOut.Code, "The server did not stop in time.", now);
        }
        else
        {
            sequence.WaitUntil(now + PollInterval);
        }
    }

    /// <summary>A start or a restart, over once the server reports running after it was asked.</summary>
    private static async Task StartAsync(IServiceProvider services, ServerSequence sequence, Server server, SequenceStep step, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!sequence.StepBegun)
        {
            if (step.OnlyIfWasRunning && !sequence.WasRunning)
            {
                sequence.StepDone(now); // the server is left as it was: stopped
                return;
            }

            if (step.Kind == SequenceStepKind.Start && server.RuntimeState == ServerRuntimeState.Running)
            {
                sequence.StepDone(now);
                return;
            }

            if (server.ActiveOperationId is not null && now - (sequence.StepStartedAt ?? now) < StartTimeout)
            {
                // An update being cancelled is still ending: the start waits for it (a server busy with one cannot start).
                sequence.Began(now, Observed(server));
                sequence.Unbegin();
                sequence.WaitUntil(now + PollInterval);
                return;
            }

            var observed = Observed(server);
            var servers = services.GetRequiredService<ServerManagement>();
            var asked = step.Kind == SequenceStepKind.Restart && IsRunning(server)
                ? await servers.RestartAsync(server.Id, cancellationToken)
                : await servers.StartAsync(server.Id, cancellationToken);
            if (Refusal(asked) is { } refusal)
            {
                sequence.StepFailed(refusal.Code, refusal.Message, now);
                return;
            }

            if (step.Kind == SequenceStepKind.Start && asked.Value.Status == AckStatus.AlreadyInState)
            {
                sequence.StepDone(now); // the node runs it already (it started since it was read): no change will come
                return;
            }

            sequence.Began(now, observed);
            sequence.WaitUntil(now + PollInterval);
            return;
        }

        var changed = sequence.ChangedSince(server.StateSequence, server.StateObservedAt);
        if (changed && server.RuntimeState == ServerRuntimeState.Running)
        {
            sequence.StepDone(now);
        }
        else if (changed && server.RuntimeState is ServerRuntimeState.Offline or ServerRuntimeState.Crashed
                 && (server.DesiredState == DesiredState.Stopped || server.LastStateReason is { } reason && Server.StopsIntent(reason)))
        {
            sequence.StepFailed(ScheduleErrors.StartFailed.Code, server.DesiredState == DesiredState.Stopped
                ? "The server was stopped while the step waited for it to start."
                : $"The server did not start ({server.LastStateReason}).", now);
        }
        else if (now - sequence.StepStartedAt >= StartTimeout)
        {
            sequence.StepFailed(ScheduleErrors.StepTimedOut.Code, "The server did not report running in time.", now);
        }
        else
        {
            sequence.WaitUntil(now + PollInterval);
        }
    }

    /// <summary>A backup or an update: started, then followed until its operation ends.</summary>
    private static async Task OperationAsync(IServiceProvider services, IPulsarDbContext db, ServerSequence sequence, Server server, SequenceStep step,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var via = sequence.Origin == SequenceOrigin.Schedule ? OperationRequester.Schedule : OperationRequester.System;
        if (!sequence.StepBegun)
        {
            // Begun with the operation's id in the save that records the operation, before its command goes out: a panel
            // stopped in between finds the step following that operation, never asks for a second one.
            void Recorded(OperationId started) => sequence.Began(now, Observed(server), started);
            DomainError? error;
            if (step.Kind == SequenceStepKind.Backup)
            {
                var backup = await services.GetRequiredService<ServerBackups>().CreateAsync(server.Id, null, via, cancellationToken, Recorded);
                error = backup.IsSuccess ? null : backup.Error;
            }
            else
            {
                var update = await services.GetRequiredService<ServerManagement>()
                    .RequestUpdateAsync(server.Id, step.Validate, via, cancellationToken, recorded: Recorded);
                error = update.IsSuccess ? null : update.Error;
            }

            if (error is not null || !sequence.StepBegun)
            {
                var reason = error ?? ServerErrors.CommandRejected;
                sequence.StepFailed(reason.Code, reason.Message, now);
                return;
            }

            sequence.WaitUntil(now + PollInterval);
            return;
        }

        var ended = await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == sequence.OperationId, cancellationToken);
        switch (ended?.Status)
        {
            case OperationStatus.Succeeded:
                sequence.StepDone(now);
                break;
            case OperationStatus.Failed or OperationStatus.Cancelled or OperationStatus.Interrupted or null:
                sequence.StepFailed(ended?.ErrorCode ?? (ended is null ? "operation.not_found" : "operation.cancelled"),
                    ended?.ErrorMessage ?? "The operation did not complete.", now);
                break;
            default:
                sequence.WaitUntil(now + PollInterval);
                break;
        }
    }

    /// <summary>
    /// Another server's files copied into this one (a clone, 1.0.16), in two operations the step follows in turn: a backup
    /// of the other server, then, once this one is installed, that backup restored here, relayed by the panel.
    /// </summary>
    private static async Task CopyFromAsync(IServiceProvider services, IPulsarDbContext db, ServerSequence sequence, Server server, ServerId source,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var backups = services.GetRequiredService<ServerBackups>();
        if (!sequence.StepBegun)
        {
            // Begun with the backup's operation in the save that records it, as any operation step.
            var made = await backups.CreateAsync(source, null, OperationRequester.System, cancellationToken,
                started => sequence.Began(now, Observed(server), started));
            if (!made.IsSuccess || !sequence.StepBegun)
            {
                var reason = made.Error ?? ServerErrors.CommandRejected;
                sequence.StepFailed(reason.Code, reason.Message, now);
                return;
            }

            sequence.WaitUntil(now + PollInterval);
            return;
        }

        var operation = await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == sequence.OperationId, cancellationToken);
        if (operation is null || operation.IsActive)
        {
            if (operation is null)
            {
                sequence.StepFailed("operation.not_found", "The operation did not complete.", now);
            }
            else
            {
                sequence.WaitUntil(now + PollInterval);
            }

            return;
        }

        if (operation.Status != OperationStatus.Succeeded)
        {
            sequence.StepFailed(operation.ErrorCode ?? "operation.cancelled", operation.ErrorMessage ?? "The operation did not complete.", now);
            return;
        }

        if (operation.Type == OperationType.BackupRestore)
        {
            sequence.StepDone(now);
            return;
        }

        // The backup is made: restored here once this server is installed (a new server installs first).
        if (server.InstallState == InstallState.Installing || server.ActiveOperationId is not null)
        {
            sequence.WaitUntil(now + PollInterval);
            return;
        }

        if (server.InstallState != InstallState.Installed)
        {
            sequence.StepFailed(ServerErrors.NotInstalled.Code, ServerErrors.NotInstalled.Message, now);
            return;
        }

        var backup = await db.Backups.AsNoTracking().SingleOrDefaultAsync(b => b.OperationId == operation.Id && b.ServerId == source, cancellationToken);
        if (backup is null)
        {
            sequence.StepFailed(ServerErrors.BackupNotFound.Code, ServerErrors.BackupNotFound.Message, now);
            return;
        }

        var restore = await backups.RestoreFromAsync(server.Id, source, backup.Id, OperationRequester.System, cancellationToken,
            started => sequence.Began(now, Observed(server), started));
        if (!restore.IsSuccess || sequence.OperationId != restore.Value.Id)
        {
            var reason = restore.Error ?? ServerErrors.CommandRejected;
            sequence.StepFailed(reason.Code, reason.Message, now);
            return;
        }

        sequence.WaitUntil(now + PollInterval);
    }

    /// <summary>
    /// A move to another version of the server's template (1.0.10): the server is provisioned again with it, and installed
    /// again (the install follows the provisioning by itself); over once it is installed.
    /// </summary>
    private static async Task TemplateAsync(IServiceProvider services, IPulsarDbContext db, ServerSequence sequence, Server server, SequenceStep step,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!sequence.StepBegun)
        {
            var via = sequence.Origin == SequenceOrigin.Schedule ? OperationRequester.Schedule : OperationRequester.System;
            var moved = await services.GetRequiredService<ServerManagement>()
                .RequestTemplateUpgradeAsync(server.Id, Guid.Parse(step.Text!), via, cancellationToken);
            if (!moved.IsSuccess)
            {
                sequence.StepFailed(moved.Error!.Code, moved.Error.Message, now);
                return;
            }

            sequence.Began(now, Observed(server), moved.Value.Operation.Id);
            sequence.WaitUntil(now + PollInterval);
            return;
        }

        var provision = await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == sequence.OperationId, cancellationToken);
        switch (provision?.Status)
        {
            case OperationStatus.Failed or OperationStatus.Cancelled or OperationStatus.Interrupted or null:
                sequence.StepFailed(provision?.ErrorCode ?? (provision is null ? "operation.not_found" : "operation.cancelled"),
                    provision?.ErrorMessage ?? "The server could not be provisioned with the new template.", now);
                return;
            case not OperationStatus.Succeeded:
                sequence.WaitUntil(now + PollInterval);
                return;
        }

        // Provisioned (the server stays installing until then): the install that follows decides.
        if (server.InstallState == InstallState.Installed && server.ActiveOperationId is null)
        {
            sequence.StepDone(now);
        }
        else if (server.InstallState is InstallState.Failed or InstallState.NotInstalled)
        {
            var install = await db.Operations.AsNoTracking()
                .Where(o => o.ServerId == server.Id && o.Type == OperationType.ServerInstall && o.CreatedAt >= provision.CreatedAt)
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            sequence.StepFailed(install?.ErrorCode ?? ScheduleErrors.TemplateInstallFailed.Code,
                install?.ErrorMessage ?? ScheduleErrors.TemplateInstallFailed.Message, now);
        }
        else
        {
            sequence.WaitUntil(now + PollInterval);
        }
    }

    private static async Task PruneAsync(IPulsarDbContext db, ServerId server, CancellationToken cancellationToken)
    {
        var oldestKept = await db.ServerSequences
            .Where(s => s.ServerId == server && s.Status != SequenceStatus.Running)
            .OrderByDescending(s => s.CreatedAt)
            .Skip(KeptPerServer - 1)
            .Select(s => (DateTimeOffset?)s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (oldestKept is { } cutoff)
        {
            await db.ServerSequences.Where(s => s.ServerId == server && s.Status != SequenceStatus.Running && s.CreatedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    private static bool IsRunning(Server server) => server.RuntimeState is ServerRuntimeState.Starting or ServerRuntimeState.Running;

    private static (long, DateTimeOffset?) Observed(Server server) => (server.StateSequence, server.StateObservedAt);

    /// <summary>Why a command was not taken; null when the node took it.</summary>
    private static DomainError? Refusal(Result<AgentCommandResult> result) =>
        !result.IsSuccess ? result.Error
        : result.Value.Status is AckStatus.Accepted or AckStatus.AlreadyInState ? null
        : new DomainError(result.Value.ErrorCode ?? ServerErrors.CommandRejected.Code, $"The node answered {result.Value.Status}.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Sequence {SequenceId} ({Origin}) on server {ServerId} ended: {Status} {Code}")]
    private partial void LogEnded(ServerSequenceId sequenceId, ServerId serverId, SequenceOrigin origin, SequenceStatus status, string? code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: a message to the players was not sent ({Code})")]
    private partial void LogBroadcastFailed(ServerId serverId, string code);
}
