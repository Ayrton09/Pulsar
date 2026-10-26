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

/// <summary>
/// What a user sets on a task. <paramref name="Command"/> is for console commands, <paramref name="Validate"/> for updates,
/// <paramref name="Steps"/> for tasks of several steps.
/// </summary>
public sealed record ScheduledTaskDefinition(
    string Name,
    string Cron,
    string TimeZone,
    ScheduledAction Action,
    string? Command,
    bool Validate,
    bool OnlyIfRunning,
    bool Enabled,
    IReadOnlyList<SequenceStep>? Steps = null,
    bool OnlyIfEmpty = false,
    bool OnlyIfUpdateAvailable = false);

/// <param name="Status">For an action that started an operation (backup, update), the operation's outcome.</param>
/// <param name="Occurrences">How many occurrences the run stands for (more than one for runs missed together).</param>
/// <param name="SequenceId">The steps of a task of several steps, which the run follows.</param>
/// <param name="Step">While those steps run: the one being done, from 0.</param>
/// <param name="Steps">While those steps run: how many there are.</param>
/// <param name="StepKind">While those steps run: what the one being done does.</param>
public sealed record ScheduledRunView(
    Guid Id,
    DateTimeOffset ScheduledFor,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    ScheduledRunStatus Status,
    int Occurrences,
    string? ErrorCode,
    string? Message,
    Guid? OperationId,
    Guid? SequenceId = null,
    int? Step = null,
    int? Steps = null,
    SequenceStepKind? StepKind = null);

/// <summary>What the server's game allows a task to do, for the editor.</summary>
/// <param name="CanBroadcast">The game has a command to message its players.</param>
/// <param name="KnowsPlayers">The game says who plays (its query): a task can wait for nobody playing.</param>
/// <param name="UpdatesChecked">The server's game updates are checked: a task can wait for one.</param>
public sealed record ScheduleAbilities(bool CanBroadcast, bool KnowsPlayers, bool UpdatesChecked);

public sealed record ScheduledTaskList(IReadOnlyList<ScheduledTaskView> Tasks, ScheduleAbilities Abilities);

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
    IReadOnlyList<SequenceStep>? Steps,
    bool OnlyIfRunning,
    bool OnlyIfEmpty,
    bool OnlyIfUpdateAvailable,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    ScheduledRunView? LastRun,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A server's scheduled tasks (ADR-0010; docs/architecture/07, "Schedules"). Scheduling an action needs the right to do
/// it, besides <c>server.schedules.edit</c>: a task acts as the schedule later, so it cannot do what its author could not.
/// A task of several steps needs the right to do each of them.
/// </summary>
public sealed class ServerSchedules(
    IPulsarDbContext db,
    Scheduler scheduler,
    ScheduleSignal signal,
    SequenceSignal sequences,
    ServerManagement servers,
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
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
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
        var manifest = await ManifestAsync(server, cancellationToken);
        var abilities = new ScheduleAbilities(manifest.Commands?.Broadcast is not null, manifest.Query is not null,
            await UpdatesCheckedAsync(server, manifest, cancellationToken));
        return new ScheduledTaskList([.. tasks.Select(t => View(t, lastRun.GetValueOrDefault(t.Id)))], abilities);
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
            Conditions(definition), definition.Enabled, request.UserId, clock.GetUtcNow());
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
        if (await CheckAsync(server, definition, task, cancellationToken) is { IsSuccess: false } check)
        {
            return check.Error!;
        }

        var changed = task.Change(definition.Name, definition.Cron, definition.TimeZone, definition.Action, Payload(definition), Conditions(definition),
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
            task.Change(definition.Name, definition.Cron, definition.TimeZone, definition.Action, Payload(definition), Conditions(definition),
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

        if (!await MayDoAsync(server, task, cancellationToken))
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

    /// <summary>
    /// Cancels the steps the task's running run is doing: what is left of them is not done, an operation they started (a
    /// backup, an update) is cancelled, and the steps that always run still do (the start that puts the server back). Needs
    /// the rights to run the task.
    /// </summary>
    public async Task<Result<ScheduledRunView>> CancelAsync(ServerId id, ScheduledTaskId taskId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var task = server is null ? null : await db.ScheduledTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId && t.ServerId == id, cancellationToken);
        if (server is null || task is null)
        {
            return server is null ? ServerErrors.NotFound : ScheduleErrors.NotFound;
        }

        if (!await MayDoAsync(server, task, cancellationToken))
        {
            return ScheduleErrors.Forbidden;
        }

        // The runner saves the steps as it moves them on: a cancel that lost the race is made again on what is stored.
        for (var tries = 1; ; tries++)
        {
            db.ChangeTracker.Clear();
            var run = await db.ScheduledTaskRuns
                .Where(r => r.TaskId == taskId && r.Status == ScheduledRunStatus.Running && r.SequenceId != null)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);
            var sequence = run?.SequenceId is { } running
                ? await db.ServerSequences.SingleOrDefaultAsync(s => s.Id == running, cancellationToken)
                : null;
            var now = clock.GetUtcNow();
            var operation = sequence?.OperationId;
            if (run is null || sequence is null)
            {
                return ScheduleErrors.SequenceOver;
            }

            // The steps running are those of the task when it began: the task may have been changed since.
            if (!await MayDoAsync(server, ScheduledAction.Sequence, sequence.Steps, cancellationToken))
            {
                return ScheduleErrors.Forbidden;
            }

            if (!sequence.Cancel(now).IsSuccess)
            {
                return ScheduleErrors.SequenceOver;
            }

            if (sequence.Status != SequenceStatus.Running)
            {
                run.SequenceEnded(sequence.Status, sequence.ErrorCode, sequence.Message, now); // nothing left that always runs
            }

            audit.Record(AuditActions.Server.ScheduleRunCancelled, metadata: new { task = task.Id.Value, task.Name, run = run.Id.Value })
                .WithServer(server.Id, server.NodeId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (tries < 4)
            {
                continue;
            }

            if (operation is { } inFlight && await db.Operations.AsNoTracking()
                    .AnyAsync(o => o.Id == inFlight && (o.Status == OperationStatus.Queued || o.Status == OperationStatus.Running), cancellationToken))
            {
                // What the steps started: the steps that always run wait for it to end.
                await servers.CancelOperationAsync(server.Id, inFlight.Value, cancellationToken);
            }

            sequences.Notify();
            return (await ViewsAsync([run], cancellationToken))[0];
        }
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

    /// <summary>The permission the action needs when a user does it; a task of several steps needs those of its steps.</summary>
    public static string? PermissionFor(ScheduledAction action) => action switch
    {
        ScheduledAction.Start => Permissions.Server.Start,
        ScheduledAction.Stop => Permissions.Server.Stop,
        ScheduledAction.Restart => Permissions.Server.Restart,
        ScheduledAction.Backup => Permissions.Server.BackupsCreate,
        ScheduledAction.Update => Permissions.Server.Install,
        ScheduledAction.ConsoleCommand => Permissions.Server.ConsoleWrite,
        ScheduledAction.Sequence => null,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>The permission a step needs when a user does what it does; none for a wait.</summary>
    public static string? PermissionFor(SequenceStepKind kind) => kind switch
    {
        SequenceStepKind.Command or SequenceStepKind.Broadcast => Permissions.Server.ConsoleWrite,
        SequenceStepKind.Stop => Permissions.Server.Stop,
        SequenceStepKind.Start => Permissions.Server.Start,
        SequenceStepKind.Restart => Permissions.Server.Restart,
        SequenceStepKind.Backup => Permissions.Server.BackupsCreate,
        SequenceStepKind.Update or SequenceStepKind.UpgradeTemplate => Permissions.Server.Install,
        SequenceStepKind.Wait => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Every permission doing the action (each of its steps) needs.</summary>
    public static IReadOnlySet<string> PermissionsFor(ScheduledAction action, IReadOnlyList<SequenceStep>? steps) =>
        action == ScheduledAction.Sequence
            ? (steps ?? []).Where(s => s is not null).Select(s => PermissionFor(s.Kind)).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal) { PermissionFor(action)! };

    private async Task<Result> CheckAsync(Server server, ScheduledTaskDefinition definition, ScheduledTask? current, CancellationToken cancellationToken)
    {
        // Values the API reads as numbers too: refused before anything looks up what they need.
        if (!Enum.IsDefined(definition.Action))
        {
            return ScheduleErrors.ActionInvalid;
        }

        if (definition.Action == ScheduledAction.Sequence
            && definition.Steps?.Any(s => s is not null && (!Enum.IsDefined(s.Kind) || s.Kind == SequenceStepKind.UpgradeTemplate)) == true)
        {
            return ScheduleErrors.StepsInvalid;
        }

        if (!await MayDoAsync(server, definition.Action, definition.Steps, cancellationToken)
            || current is not null && !await MayDoAsync(server, current, cancellationToken))
        {
            return ScheduleErrors.Forbidden;
        }

        var sequence = definition.Action == ScheduledAction.Sequence;
        IReadOnlyList<string> commands = definition.Action == ScheduledAction.ConsoleCommand
            ? definition.Command is { } single ? [single] : []
            : sequence
                ? [.. (definition.Steps ?? []).Where(s => s is { Kind: SequenceStepKind.Command, Text: not null }).Select(s => s.Text!)]
                : [];
        var broadcasts = sequence && (definition.Steps ?? []).Any(s => s is { Kind: SequenceStepKind.Broadcast });
        var waitsForEmpty = definition.OnlyIfEmpty && definition.Action is not (ScheduledAction.Start or ScheduledAction.ConsoleCommand);
        var waitsForUpdate = definition.OnlyIfUpdateAvailable && definition.Action is ScheduledAction.Update or ScheduledAction.Sequence;
        if (commands.Count == 0 && !broadcasts && !waitsForEmpty && !waitsForUpdate)
        {
            return Result.Success();
        }

        var manifest = await ManifestAsync(server, cancellationToken);
        var prefixes = manifest.Runtime.GetValueOrDefault(ManifestValidator.WindowsX64)?.Console.SensitiveCommandPrefixes ?? [];
        foreach (var command in commands)
        {
            // A command that sets a secret would be kept in clear and shown to whoever sees the schedule.
            if (ConsoleCommands.Redact(command, prefixes) != command)
            {
                return ScheduleErrors.CommandSensitive;
            }

            if (!Contracts.ConsoleInput.IsValid(command.Trim()))
            {
                return sequence ? ScheduleErrors.StepsInvalid : ScheduleErrors.CommandRequired;
            }
        }

        if (broadcasts && manifest.Commands?.Broadcast is null)
        {
            return ScheduleErrors.BroadcastUnsupported;
        }

        if (waitsForEmpty && manifest.Query is null)
        {
            return ScheduleErrors.PlayersNotReported;
        }

        if (waitsForUpdate && !await UpdatesCheckedAsync(server, manifest, cancellationToken))
        {
            return ScheduleErrors.UpdateChecksUnavailable;
        }

        return Result.Success();
    }

    /// <summary>The server's game updates are checked: its game can be, and the checks are on (a server without settings has them on).</summary>
    private async Task<bool> UpdatesCheckedAsync(Server server, GameManifest manifest, CancellationToken cancellationToken) =>
        GameUpdates.IsCheckable(manifest)
        && !await db.GameUpdateWatches.AnyAsync(w => w.ServerId == server.Id && w.Policy == GameUpdatePolicy.Off, cancellationToken);

    private Task<bool> MayDoAsync(Server server, ScheduledTask task, CancellationToken cancellationToken) =>
        MayDoAsync(server, task.Action, task.Payload.Steps, cancellationToken);

    private async Task<bool> MayDoAsync(Server server, ScheduledAction action, IReadOnlyList<SequenceStep>? steps, CancellationToken cancellationToken)
    {
        if (request.UserId is not { } user)
        {
            return false;
        }

        var target = AccessTarget.ForServer(server.Id, server.NodeId);
        foreach (var permission in PermissionsFor(action, steps))
        {
            if (!await permissions.HasAsync(user, permission, target, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<GameManifest> ManifestAsync(Server server, CancellationToken cancellationToken) =>
        ServerSpecs.Parse(await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken));

    private async Task<DomainError> NotFoundAsync(ServerId id, CancellationToken cancellationToken) =>
        await db.Servers.AnyAsync(s => s.Id == id, cancellationToken) ? ScheduleErrors.NotFound : ServerErrors.NotFound;

    private async Task<IReadOnlyList<ScheduledRunView>> LastRunsAsync(ScheduledTaskId taskId, CancellationToken cancellationToken) =>
        await ViewsAsync(await db.ScheduledTaskRuns.AsNoTracking().Where(r => r.TaskId == taskId).OrderByDescending(r => r.StartedAt).Take(1)
            .ToListAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Runs as shown: an action that started an operation takes the operation's outcome; a run of several steps that
    /// goes on says which one is being done.
    /// </summary>
    private async Task<IReadOnlyList<ScheduledRunView>> ViewsAsync(IReadOnlyList<ScheduledTaskRun> runs, CancellationToken cancellationToken)
    {
        var operationIds = runs.Where(r => r.OperationId is not null).Select(r => r.OperationId!.Value).ToList();
        var operations = operationIds.Count == 0
            ? []
            : await db.Operations.AsNoTracking().Where(o => operationIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        var sequenceIds = runs.Where(r => r.SequenceId is not null && r.Status == ScheduledRunStatus.Running).Select(r => r.SequenceId!.Value).ToList();
        var sequences = sequenceIds.Count == 0
            ? []
            : await db.ServerSequences.AsNoTracking().Where(s => sequenceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        return [.. runs.Select(run => run.OperationId is { } op && operations.TryGetValue(op, out var operation)
            ? View(run, operation)
            : run.SequenceId is { } steps && sequences.TryGetValue(steps, out var sequence) ? View(run, sequence) : View(run))];
    }

    private static ScheduledRunView View(ScheduledTaskRun run) =>
        new(run.Id.Value, run.ScheduledFor, run.StartedAt, run.FinishedAt, run.Status, run.Occurrences, run.ErrorCode, run.Message, run.OperationId?.Value,
            run.SequenceId?.Value);

    private static ScheduledRunView View(ScheduledTaskRun run, ServerSequence sequence) => View(run) with
    {
        Step = sequence.Status == SequenceStatus.Running ? Math.Min(sequence.StepIndex, sequence.Steps.Count - 1) : null,
        Steps = sequence.Status == SequenceStatus.Running ? sequence.Steps.Count : null,
        StepKind = sequence.Current?.Kind,
    };

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
        new(task.Id.Value, task.Name, task.CronExpression, task.TimeZone, task.Action, task.Payload.Command, task.Payload.Validate, task.Payload.Steps,
            task.OnlyIfRunning, task.OnlyIfEmpty, task.OnlyIfUpdateAvailable, task.IsEnabled, task.NextRunAt, task.LastRunAt, lastRun, task.CreatedAt,
            task.UpdatedAt);

    private static ScheduledActionPayload Payload(ScheduledTaskDefinition definition) => new(definition.Command, definition.Validate, definition.Steps);

    private static ScheduledConditions Conditions(ScheduledTaskDefinition definition) =>
        new(definition.OnlyIfRunning, definition.OnlyIfEmpty, definition.OnlyIfUpdateAvailable);

    private static object Audited(ScheduledTask task) => new
    {
        task = task.Id.Value,
        task.Name,
        cron = task.CronExpression,
        timeZone = task.TimeZone,
        action = task.Action.ToString(),
        command = task.Payload.Command,
        // What each step does, and the commands they send (the messages are cleaned when sent).
        steps = task.Payload.Steps?.Select(s => s.Kind.ToString()).ToArray(),
        commands = task.Payload.Steps?.Where(s => s.Kind == SequenceStepKind.Command).Select(s => s.Text).ToArray(),
        onlyIfRunning = task.OnlyIfRunning,
        onlyIfEmpty = task.OnlyIfEmpty,
        onlyIfUpdateAvailable = task.OnlyIfUpdateAvailable,
        enabled = task.IsEnabled,
    };
}
