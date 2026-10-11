using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Scheduling;

/// <summary>What a user sets on a task. <paramref name="Command"/> is for console commands, <paramref name="Validate"/> for updates.</summary>
public sealed record ScheduledTaskDefinition(
    string Name,
    string Cron,
    string TimeZone,
    ScheduledAction Action,
    string? Command,
    bool Validate,
    bool OnlyIfRunning,
    bool Enabled);

/// <param name="Status">For an action that started an operation (backup, update), the operation's outcome.</param>
/// <param name="Occurrences">How many occurrences the run stands for (more than one for runs missed together).</param>
public sealed record ScheduledRunView(
    Guid Id,
    DateTimeOffset ScheduledFor,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    ScheduledRunStatus Status,
    int Occurrences,
    string? ErrorCode,
    string? Message,
    Guid? OperationId);

public sealed record ScheduledTaskList(IReadOnlyList<ScheduledTaskView> Tasks);

public sealed record ScheduledRunList(IReadOnlyList<ScheduledRunView> Runs);

/// <param name="Occurrences">The next ones, UTC.</param>
public sealed record SchedulePreview(IReadOnlyList<DateTimeOffset> Occurrences);

public sealed record ScheduledTaskView(
    Guid Id,
    string Name,
    string Cron,
    string TimeZone,
    ScheduledAction Action,
    string? Command,
    bool Validate,
    bool OnlyIfRunning,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    ScheduledRunView? LastRun,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A server's scheduled tasks (ADR-0010; docs/architecture/07, "Schedules"). Scheduling an action needs the right to do
/// it, besides <c>server.schedules.edit</c>: a task acts as the schedule later, so it cannot do what its author could not.
/// </summary>
public sealed class ServerSchedules(
    IPulsarDbContext db,
    Scheduler scheduler,
    ScheduleSignal signal,
    IPermissionEvaluator permissions,
    IRowLocks locks,
    IAuditLog audit,
    IRequestContext request,
    TimeProvider clock)
{
    public const int PreviewCount = 5;
    public const int MaxRunsListed = Scheduler.KeptRuns;

    public async Task<Result<ScheduledTaskList>> ListAsync(ServerId id, CancellationToken cancellationToken)
    {
        if (!await db.Servers.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return ServerErrors.NotFound;
        }

        var tasks = await db.ScheduledTasks.AsNoTracking().Where(t => t.ServerId == id).OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var taskIds = tasks.Select(t => t.Id).ToList();
        var lastRuns = await db.ScheduledTaskRuns.AsNoTracking()
            .Where(r => taskIds.Contains(r.TaskId))
            .GroupBy(r => r.TaskId)
            .Select(g => g.OrderByDescending(r => r.StartedAt).First())
            .ToListAsync(cancellationToken);
        var views = await ViewsAsync(lastRuns, cancellationToken);
        var lastRun = lastRuns.Zip(views).ToDictionary(p => p.First.TaskId, p => p.Second);
        return new ScheduledTaskList([.. tasks.Select(t => View(t, lastRun.GetValueOrDefault(t.Id)))]);
    }

    public async Task<Result<ScheduledRunList>> RunsAsync(ServerId id, ScheduledTaskId taskId, int limit, CancellationToken cancellationToken)
    {
        if (!await db.ScheduledTasks.AnyAsync(t => t.Id == taskId && t.ServerId == id, cancellationToken))
        {
            return await NotFoundAsync(id, cancellationToken);
        }

        var runs = await db.ScheduledTaskRuns.AsNoTracking()
            .Where(r => r.TaskId == taskId)
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(limit, 1, MaxRunsListed))
            .ToListAsync(cancellationToken);
        return new ScheduledRunList(await ViewsAsync(runs, cancellationToken));
    }

    public async Task<Result<ScheduledTaskView>> CreateAsync(ServerId id, ScheduledTaskDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (await CheckAsync(server, definition, null, cancellationToken) is { IsSuccess: false } check)
        {
            return check.Error!;
        }

        // Counted with the server's row locked: requests made at the same moment cannot all take the last place.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockServerAsync(id, cancellationToken);
        if (await db.ScheduledTasks.CountAsync(t => t.ServerId == id, cancellationToken) >= ScheduledTask.MaxTasksPerServer)
        {
            return ScheduleErrors.TooMany;
        }

        var created = ScheduledTask.Create(server, definition.Name, definition.Cron, definition.TimeZone, definition.Action, Payload(definition),
            definition.OnlyIfRunning, definition.Enabled, request.UserId, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var task = created.Value;
        db.ScheduledTasks.Add(task);
        audit.Record(AuditActions.Server.ScheduleCreated, metadata: Audited(task)).WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        signal.Notify();
        return View(task, null);
    }

    public async Task<Result<ScheduledTaskView>> UpdateAsync(ServerId id, ScheduledTaskId taskId, ScheduledTaskDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var task = server is null ? null : await db.ScheduledTasks.SingleOrDefaultAsync(t => t.Id == taskId && t.ServerId == id, cancellationToken);
        if (server is null || task is null)
        {
            return server is null ? ServerErrors.NotFound : ScheduleErrors.NotFound;
        }

        // Changing a task needs the right to do what it did, and what it will do.
        if (await CheckAsync(server, definition, task.Action, cancellationToken) is { IsSuccess: false } check)
        {
            return check.Error!;
        }

        var changed = task.Change(definition.Name, definition.Cron, definition.TimeZone, definition.Action, Payload(definition), definition.OnlyIfRunning,
            definition.Enabled, clock.GetUtcNow());
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        audit.Record(AuditActions.Server.ScheduleChanged, metadata: Audited(task)).WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The scheduler took a run meanwhile: apply the change to the fresh task.
            await db.Entry(task).ReloadAsync(cancellationToken);
            task.Change(definition.Name, definition.Cron, definition.TimeZone, definition.Action, Payload(definition), definition.OnlyIfRunning,
                definition.Enabled, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        signal.Notify();
        var last = await LastRunsAsync(task.Id, cancellationToken);
        return View(task, last.Count > 0 ? last[0] : null);
    }

    public async Task<Result> DeleteAsync(ServerId id, ScheduledTaskId taskId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var task = server is null ? null : await db.ScheduledTasks.SingleOrDefaultAsync(t => t.Id == taskId && t.ServerId == id, cancellationToken);
        if (server is null || task is null)
        {
            return server is null ? ServerErrors.NotFound : ScheduleErrors.NotFound;
        }

        db.ScheduledTasks.Remove(task);
        audit.Record(AuditActions.Server.ScheduleDeleted, metadata: new { task = task.Id.Value, task.Name }).WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Taken by the scheduler meanwhile (or already deleted): delete what is there now.
            db.ChangeTracker.Clear();
            audit.Record(AuditActions.Server.ScheduleDeleted, metadata: new { task = task.Id.Value, task.Name }).WithServer(server.Id, server.NodeId);
            await db.ScheduledTasks.Where(t => t.Id == taskId).ExecuteDeleteAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        signal.Notify();
        return Result.Success();
    }

    /// <summary>Runs the task now, outside its schedule, and returns how it went (an operation it starts goes on).</summary>
    public async Task<Result<ScheduledRunView>> RunNowAsync(ServerId id, ScheduledTaskId taskId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var task = server is null ? null : await db.ScheduledTasks.SingleOrDefaultAsync(t => t.Id == taskId && t.ServerId == id, cancellationToken);
        if (server is null || task is null)
        {
            return server is null ? ServerErrors.NotFound : ScheduleErrors.NotFound;
        }

        if (!await MayDoAsync(server, task.Action, cancellationToken))
        {
            return ScheduleErrors.Forbidden;
        }

        var run = task.RunNow(clock.GetUtcNow());
        db.ScheduledTaskRuns.Add(run);
        audit.Record(AuditActions.Server.ScheduleRunRequested, metadata: new { task = task.Id.Value, task.Name, run = run.Id.Value })
            .WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The scheduler took the task meanwhile; the run does not change it.
            await db.Entry(task).ReloadAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await scheduler.ExecuteAsync(run.Id, cancellationToken);
        await db.Entry(run).ReloadAsync(cancellationToken);
        return (await ViewsAsync([run], cancellationToken))[0];
    }

    /// <summary>The next occurrences of a schedule (shown while editing it).</summary>
    public async Task<Result<SchedulePreview>> PreviewAsync(ServerId id, string cron, string timeZone, CancellationToken cancellationToken)
    {
        if (!await db.Servers.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return ServerErrors.NotFound;
        }

        var occurrences = ScheduledTask.Preview(cron, timeZone, clock.GetUtcNow(), PreviewCount);
        return occurrences.IsSuccess ? new SchedulePreview(occurrences.Value) : occurrences.Error!;
    }

    /// <summary>The permission the action needs when a user does it.</summary>
    public static string PermissionFor(ScheduledAction action) => action switch
    {
        ScheduledAction.Start => Permissions.Server.Start,
        ScheduledAction.Stop => Permissions.Server.Stop,
        ScheduledAction.Restart => Permissions.Server.Restart,
        ScheduledAction.Backup => Permissions.Server.BackupsCreate,
        ScheduledAction.Update => Permissions.Server.Install,
        ScheduledAction.ConsoleCommand => Permissions.Server.ConsoleWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private async Task<Result> CheckAsync(Server server, ScheduledTaskDefinition definition, ScheduledAction? current, CancellationToken cancellationToken)
    {
        if (!await MayDoAsync(server, definition.Action, cancellationToken) || current is { } was && !await MayDoAsync(server, was, cancellationToken))
        {
            return ScheduleErrors.Forbidden;
        }

        if (definition.Action == ScheduledAction.ConsoleCommand && definition.Command is { } command)
        {
            // A command that sets a secret would be kept in clear and shown to whoever sees the schedule.
            var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
            var prefixes = ServerSpecs.Parse(template).Runtime.GetValueOrDefault(ManifestValidator.WindowsX64)?.Console.SensitiveCommandPrefixes ?? [];
            if (ConsoleCommands.Redact(command, prefixes) != command)
            {
                return ScheduleErrors.CommandSensitive;
            }

            if (!Contracts.ConsoleInput.IsValid(command.Trim()))
            {
                return ScheduleErrors.CommandRequired;
            }
        }

        return Result.Success();
    }

    private async Task<bool> MayDoAsync(Server server, ScheduledAction action, CancellationToken cancellationToken) =>
        request.UserId is { } user
        && await permissions.HasAsync(user, PermissionFor(action), AccessTarget.ForServer(server.Id, server.NodeId), cancellationToken);

    private async Task<DomainError> NotFoundAsync(ServerId id, CancellationToken cancellationToken) =>
        await db.Servers.AnyAsync(s => s.Id == id, cancellationToken) ? ScheduleErrors.NotFound : ServerErrors.NotFound;

    private async Task<IReadOnlyList<ScheduledRunView>> LastRunsAsync(ScheduledTaskId taskId, CancellationToken cancellationToken) =>
        await ViewsAsync(await db.ScheduledTaskRuns.AsNoTracking().Where(r => r.TaskId == taskId).OrderByDescending(r => r.StartedAt).Take(1)
            .ToListAsync(cancellationToken), cancellationToken);

    /// <summary>Runs as shown: an action that started an operation takes the operation's outcome.</summary>
    private async Task<IReadOnlyList<ScheduledRunView>> ViewsAsync(IReadOnlyList<ScheduledTaskRun> runs, CancellationToken cancellationToken)
    {
        var operationIds = runs.Where(r => r.OperationId is not null).Select(r => r.OperationId!.Value).ToList();
        var operations = operationIds.Count == 0
            ? []
            : await db.Operations.AsNoTracking().Where(o => operationIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        return [.. runs.Select(run => run.OperationId is { } op && operations.TryGetValue(op, out var operation) ? View(run, operation) : View(run))];
    }

    private static ScheduledRunView View(ScheduledTaskRun run) =>
        new(run.Id.Value, run.ScheduledFor, run.StartedAt, run.FinishedAt, run.Status, run.Occurrences, run.ErrorCode, run.Message, run.OperationId?.Value);

    private static ScheduledRunView View(ScheduledTaskRun run, Operation operation) => operation.Status switch
    {
        OperationStatus.Queued or OperationStatus.Running =>
            new(run.Id.Value, run.ScheduledFor, run.StartedAt, null, ScheduledRunStatus.Running, run.Occurrences, null, null, operation.Id.Value),
        OperationStatus.Succeeded =>
            new(run.Id.Value, run.ScheduledFor, run.StartedAt, operation.CompletedAt, ScheduledRunStatus.Succeeded, run.Occurrences, null, null,
                operation.Id.Value),
        _ => new(run.Id.Value, run.ScheduledFor, run.StartedAt, operation.CompletedAt, ScheduledRunStatus.Failed, run.Occurrences,
            operation.ErrorCode ?? (operation.Status == OperationStatus.Cancelled ? "operation.cancelled" : "operation.failed"), operation.ErrorMessage,
            operation.Id.Value),
    };

    private static ScheduledTaskView View(ScheduledTask task, ScheduledRunView? lastRun) =>
        new(task.Id.Value, task.Name, task.CronExpression, task.TimeZone, task.Action, task.Payload.Command, task.Payload.Validate, task.OnlyIfRunning,
            task.IsEnabled, task.NextRunAt, task.LastRunAt, lastRun, task.CreatedAt, task.UpdatedAt);

    private static ScheduledActionPayload Payload(ScheduledTaskDefinition definition) => new(definition.Command, definition.Validate);

    private static object Audited(ScheduledTask task) => new
    {
        task = task.Id.Value,
        task.Name,
        cron = task.CronExpression,
        timeZone = task.TimeZone,
        action = task.Action.ToString(),
        command = task.Payload.Command,
        enabled = task.IsEnabled,
    };
}
