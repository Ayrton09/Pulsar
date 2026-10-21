using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Notifications;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Scheduling;

/// <param name="Taken">Tasks taken in this pass.</param>
/// <param name="More">More tasks were due than one pass takes: run again right away.</param>
public sealed record SchedulerPass(int Taken, bool More);

/// <summary>
/// Runs scheduled tasks (ADR-0010): takes the due ones under row locks (several panels never run one twice), records
/// their runs (missed ones as skipped) and does each action as the schedule, through the same services users go
/// through, so every rule and audit entry applies.
/// </summary>
public sealed partial class Scheduler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<Scheduler> logger)
{
    /// <summary>Tasks taken per pass.</summary>
    public const int BatchSize = 50;

    /// <summary>Runs kept per task (older ones are deleted).</summary>
    public const int KeptRuns = 100;

    /// <summary>A run still running after this was left behind by a panel that stopped.</summary>
    public static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(5);

    /// <summary>Takes the tasks due at <paramref name="now"/> and runs them.</summary>
    public async Task<SchedulerPass> RunDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<(ScheduledRunId Run, ScheduledTaskId Task)> toRun = [];
        List<ScheduledTask> taken;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            await FailInterruptedAsync(db, now, cancellationToken);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            taken = [.. await scope.ServiceProvider.GetRequiredService<IScheduledTaskClaims>().LockDueAsync(now, BatchSize, cancellationToken)];
            foreach (var task in taken)
            {
                foreach (var run in task.Due(now))
                {
                    db.ScheduledTaskRuns.Add(run);
                    switch (run.Status)
                    {
                        case ScheduledRunStatus.Running:
                            toRun.Add((run.Id, task.Id));
                            break;
                        case ScheduledRunStatus.Failed:
                            // A schedule this panel cannot read: nobody sees it stop otherwise.
                            LogUnreadable(task.Id, task.ServerId, task.CronExpression, task.TimeZone);
                            if (await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == task.ServerId, cancellationToken) is { } server)
                            {
                                await scope.ServiceProvider.GetRequiredService<Notifier>().ScheduledTaskFailedAsync(server, task.Name, run.Message,
                                    cancellationToken);
                            }

                            break;
                        default:
                            LogMissed(task.Id, task.ServerId, run.Occurrences, run.ScheduledFor);
                            break;
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await Task.WhenAll(toRun.Select(r => ExecuteAsync(r.Run, cancellationToken)));
        foreach (var task in taken)
        {
            await PruneAsync(task.Id, cancellationToken);
        }

        return new SchedulerPass(taken.Count, taken.Count == BatchSize);
    }

    /// <summary>When the next enabled task is due (UTC), if any.</summary>
    public async Task<DateTimeOffset?> NextRunAtAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ScheduledTasks.AsNoTracking()
            .Where(t => t.IsEnabled && t.NextRunAt != null)
            .MinAsync(t => t.NextRunAt, cancellationToken);
    }

    /// <summary>
    /// Does a taken run's action, in a scope of its own acting as the schedule. Never throws for the action's sake: a
    /// failure is the run's outcome.
    /// </summary>
    public async Task ExecuteAsync(ScheduledRunId runId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IPulsarDbContext>();
        // Runs are deleted with their task or server: either may be gone since the run was taken.
        var run = await db.ScheduledTaskRuns.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        var task = run is null ? null : await db.ScheduledTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == run.TaskId, cancellationToken);
        if (run is null || task is null)
        {
            return; // deleted meanwhile, with its runs
        }

        // Before anything resolves the request context: audit entries name the schedule.
        services.GetRequiredService<RequestContextOverride>().Context = new ScheduleRequestContext(task.Name, run.Id);
        try
        {
            await DoAsync(services, db, task, run, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Failed(ScheduleErrors.Interrupted.Code, ScheduleErrors.Interrupted.Message, clock.GetUtcNow());
        }
        catch (Exception ex)
        {
            LogRunFailed(ex, task.Id, task.ServerId, task.Action);
            db.ChangeTracker.Clear();
            if (await db.ScheduledTaskRuns.SingleOrDefaultAsync(r => r.Id == runId, CancellationToken.None) is not { } failedRun)
            {
                return; // deleted while the action ran
            }

            run = failedRun;
            if (ex is DomainErrorException { Error: var error })
            {
                run.Failed(error.Code, error.Message, clock.GetUtcNow()); // a template this panel refuses, say
            }
            else
            {
                run.Failed("schedules.error", "The panel could not run the task; its log has the details.", clock.GetUtcNow());
            }
        }

        if (run.Status == ScheduledRunStatus.Failed
            && await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == task.ServerId, CancellationToken.None) is { } server)
        {
            await services.GetRequiredService<Notifier>().ScheduledTaskFailedAsync(server, task.Name, run.Message ?? run.ErrorCode, CancellationToken.None);
        }

        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            return; // the run was deleted with its task or server while the action ran: nothing left to record
        }

        LogRun(task.Id, task.ServerId, task.Action, run.Status, run.ErrorCode);
    }

    private async Task DoAsync(IServiceProvider services, IPulsarDbContext db, ScheduledTask task, ScheduledTaskRun run, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == task.ServerId, cancellationToken);
        if (server is null)
        {
            run.Failed(ServerErrors.NotFound.Code, ServerErrors.NotFound.Message, clock.GetUtcNow());
            return;
        }

        var running = server.RuntimeState is ServerRuntimeState.Starting or ServerRuntimeState.Running;
        if ((task.OnlyIfRunning || task.Action == ScheduledAction.ConsoleCommand) && !running)
        {
            run.Skipped(ScheduleErrors.NotRunning, clock.GetUtcNow());
            return;
        }

        var servers = services.GetRequiredService<ServerManagement>();
        switch (task.Action)
        {
            case ScheduledAction.Start:
                Acknowledged(run, await servers.StartAsync(server.Id, cancellationToken));
                break;
            case ScheduledAction.Stop:
                Acknowledged(run, await servers.StopAsync(server.Id, cancellationToken));
                break;
            case ScheduledAction.Restart:
                Acknowledged(run, await servers.RestartAsync(server.Id, cancellationToken));
                break;
            case ScheduledAction.ConsoleCommand:
                Acknowledged(run, await services.GetRequiredService<ConsoleCommands>().SendAsync(server.Id, task.Payload.Command!, cancellationToken));
                break;
            case ScheduledAction.Backup:
                var backup = await services.GetRequiredService<ServerBackups>().CreateAsync(server.Id, null, OperationRequester.Schedule, cancellationToken);
                await DispatchedAsync(db, run, backup.IsSuccess ? backup.Value.OperationId : null, backup.Error, cancellationToken);
                break;
            case ScheduledAction.Update:
                var update = await servers.RequestUpdateAsync(server.Id, task.Payload.Validate, OperationRequester.Schedule, cancellationToken);
                await DispatchedAsync(db, run, update.IsSuccess ? update.Value.Operation.Id.Value : null, update.Error, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unknown scheduled action {task.Action}.");
        }
    }

    private void Acknowledged(ScheduledTaskRun run, Result<AgentCommandResult> result)
    {
        var now = clock.GetUtcNow();
        if (!result.IsSuccess)
        {
            run.Failed(result.Error!.Code, result.Error.Message, now);
        }
        else if (result.Value.Status is AckStatus.Accepted or AckStatus.AlreadyInState)
        {
            run.Succeeded(now);
        }
        else
        {
            run.Failed(result.Value.ErrorCode ?? ServerErrors.CommandRejected.Code, $"The node answered {result.Value.Status}.", now);
        }
    }

    /// <summary>
    /// The action started an operation: its outcome is the operation's. An operation the node refused has failed
    /// already: so does the run, which notifies like a refused start.
    /// </summary>
    private async Task DispatchedAsync(IPulsarDbContext db, ScheduledTaskRun run, Guid? operationId, DomainError? error, CancellationToken cancellationToken)
    {
        if (operationId is { } id)
        {
            var operation = await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == new OperationId(id), cancellationToken);
            var failed = operation is { Status: OperationStatus.Failed or OperationStatus.Cancelled };
            run.Dispatched(new OperationId(id), clock.GetUtcNow(), failed ? operation!.ErrorCode ?? ServerErrors.CommandRejected.Code : null,
                failed ? operation!.ErrorMessage : null);
        }
        else
        {
            var reason = error ?? ServerErrors.CommandRejected;
            run.Failed(reason.Code, reason.Message, clock.GetUtcNow());
        }
    }

    /// <summary>Runs a stopped panel left behind (taken, never ended) fail: nobody will end them.</summary>
    private static Task<int> FailInterruptedAsync(IPulsarDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var before = now - InterruptedAfter;
        return db.ScheduledTaskRuns
            .Where(r => r.Status == ScheduledRunStatus.Running && r.StartedAt < before)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ScheduledRunStatus.Failed)
                .SetProperty(r => r.ErrorCode, ScheduleErrors.Interrupted.Code)
                .SetProperty(r => r.Message, ScheduleErrors.Interrupted.Message)
                .SetProperty(r => r.FinishedAt, now), cancellationToken);
    }

    private async Task PruneAsync(ScheduledTaskId taskId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ScheduledTaskRuns;
        var oldestKept = await runs.Where(r => r.TaskId == taskId)
            .OrderByDescending(r => r.StartedAt)
            .Skip(KeptRuns - 1)
            .Select(r => (DateTimeOffset?)r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (oldestKept is { } cutoff)
        {
            await runs.Where(r => r.TaskId == taskId && r.StartedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled task {TaskId} ({Action}) on server {ServerId}: {Status} {Code}")]
    private partial void LogRun(ScheduledTaskId taskId, ServerId serverId, ScheduledAction action, ScheduledRunStatus status, string? code);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Scheduled task {TaskId} on server {ServerId} missed {Count} run(s) from {ScheduledFor} while the panel was not running")]
    private partial void LogMissed(ScheduledTaskId taskId, ServerId serverId, int count, DateTimeOffset scheduledFor);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Scheduled task {TaskId} on server {ServerId} stopped: its schedule \"{Cron}\" in {TimeZone} can no longer be read; edit it to run it again")]
    private partial void LogUnreadable(ScheduledTaskId taskId, ServerId serverId, string cron, string timeZone);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled task {TaskId} ({Action}) on server {ServerId} failed unexpectedly")]
    private partial void LogRunFailed(Exception exception, ScheduledTaskId taskId, ServerId serverId, ScheduledAction action);
}
