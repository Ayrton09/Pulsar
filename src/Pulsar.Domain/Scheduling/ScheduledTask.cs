using System.Diagnostics.CodeAnalysis;
using Cronos;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Scheduling;

public enum ScheduledAction
{
    Start,
    Stop,
    Restart,
    Backup,
    Update,
    ConsoleCommand,
}

/// <summary>What some actions need: the console command to send, whether an update verifies every file.</summary>
public sealed record ScheduledActionPayload(string? Command = null, bool Validate = false)
{
    public static ScheduledActionPayload None { get; } = new();
}

/// <summary>
/// A typed action on a server at the times a cron expression gives in an IANA time zone (ADR-0010;
/// docs/architecture/02, "Scheduling"). The panel's scheduler takes it when <see cref="NextRunAt"/> comes: runs missed
/// while the panel was down are recorded as skipped, never done late in a burst.
/// </summary>
public sealed class ScheduledTask
{
    public const int MaxNameLength = 128;
    public const int MaxCronLength = 128;
    public const int MaxTimeZoneLength = 64;
    public const int MaxCommandLength = 512;
    public const int MaxTasksPerServer = 50;

    /// <summary>A run this late still runs (the panel was busy or restarting); an older one was missed.</summary>
    public static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(1);

    // Counting missed runs stops here (a task every minute and a panel down for months).
    private const int MaxCountedMisses = 100_000;

    private CronExpression? _expression;
    private TimeZoneInfo? _zone;

    private ScheduledTask()
    {
        // For EF Core materialization.
        Name = null!;
        CronExpression = null!;
        TimeZone = null!;
        Payload = null!;
    }

    private ScheduledTask(ServerId serverId, UserId? createdBy, DateTimeOffset now)
    {
        Id = ScheduledTaskId.New();
        ServerId = serverId;
        CreatedBy = createdBy;
        CreatedAt = now;
        Name = null!;
        CronExpression = null!;
        TimeZone = null!;
        Payload = ScheduledActionPayload.None;
    }

    public ScheduledTaskId Id { get; private set; }

    public ServerId ServerId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Five fields: minute, hour, day of month, month, day of week.</summary>
    public string CronExpression { get; private set; }

    /// <summary>IANA id; occurrences follow its daylight saving time.</summary>
    public string TimeZone { get; private set; }

    public ScheduledAction Action { get; private set; }

    public ScheduledActionPayload Payload { get; private set; }

    /// <summary>Restarts and backups can be skipped while the server is not running.</summary>
    public bool OnlyIfRunning { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>The next occurrence (UTC); none while disabled.</summary>
    public DateTimeOffset? NextRunAt { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    public UserId? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<ScheduledTask> Create(Server server, string name, string cron, string timeZone, ScheduledAction action,
        ScheduledActionPayload payload, bool onlyIfRunning, bool enabled, UserId? createdBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(server);
        var task = new ScheduledTask(server.Id, createdBy, now);
        var set = task.Change(name, cron, timeZone, action, payload, onlyIfRunning, enabled, now);
        return set.IsSuccess ? task : set.Error!;
    }

    /// <summary>Replaces the task's definition; the next run is computed again from <paramref name="now"/>.</summary>
    public Result Change(string name, string cron, string timeZone, ScheduledAction action, ScheduledActionPayload payload, bool onlyIfRunning,
        bool enabled, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || !DisplayNames.IsDisplayable(trimmed))
        {
            return ScheduleErrors.NameInvalid;
        }

        var expression = Parse(cron);
        if (expression is null)
        {
            return ScheduleErrors.CronInvalid;
        }

        var zone = FindZone(timeZone);
        if (zone is null)
        {
            return ScheduleErrors.TimeZoneInvalid;
        }

        if (Next(expression, now, zone) is null)
        {
            return ScheduleErrors.NeverRuns;
        }

        if (onlyIfRunning && action is not (ScheduledAction.Restart or ScheduledAction.Backup))
        {
            return ScheduleErrors.OnlyIfRunningUnsupported;
        }

        var command = payload.Command?.Trim();
        if (action == ScheduledAction.ConsoleCommand
            && (string.IsNullOrEmpty(command) || command.Length > MaxCommandLength || command.Any(char.IsControl)))
        {
            return ScheduleErrors.CommandRequired;
        }

        Name = trimmed;
        CronExpression = cron.Trim();
        TimeZone = zone.Id;
        Action = action;
        Payload = action switch
        {
            ScheduledAction.ConsoleCommand => new ScheduledActionPayload(command),
            ScheduledAction.Update => new ScheduledActionPayload(Validate: payload.Validate),
            _ => ScheduledActionPayload.None,
        };
        OnlyIfRunning = onlyIfRunning;
        IsEnabled = enabled;
        _expression = expression;
        _zone = zone;
        NextRunAt = enabled ? Next(expression, now, zone) : null;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// What is due at <paramref name="now"/>: one skipped run standing for every occurrence older than
    /// <see cref="MissedAfter"/>, then a run for an occurrence that is still on time. The next run moves past now.
    /// A schedule that can no longer be read gives one failed run and is due no more until it is edited.
    /// </summary>
    public IReadOnlyList<ScheduledTaskRun> Due(DateTimeOffset now)
    {
        if (!IsEnabled || NextRunAt is not { } next || next > now)
        {
            return [];
        }

        if (!TrySchedule(out var expression, out var zone))
        {
            // Taken with the panel's other due tasks: they still run, and this one waits for an edit (Change parses again).
            NextRunAt = null;
            var unreadable = ScheduledTaskRun.Start(this, next, now);
            unreadable.Failed(ScheduleErrors.Unreadable.Code, ScheduleErrors.Unreadable.Message, now);
            return [unreadable];
        }

        var runs = new List<ScheduledTaskRun>(2);
        var missedBefore = now - MissedAfter;
        DateTimeOffset? current = next;
        if (next < missedBefore)
        {
            var missed = 0;
            while (current is { } at && at < missedBefore && missed < MaxCountedMisses)
            {
                missed++;
                current = Next(expression, at, zone);
            }

            runs.Add(ScheduledTaskRun.Missed(this, next, missed, now));
            if (current is { } still && still < missedBefore)
            {
                current = Next(expression, missedBefore, zone, inclusive: true);
            }
        }

        if (current is { } onTime && onTime <= now)
        {
            runs.Add(ScheduledTaskRun.Start(this, onTime, now));
            LastRunAt = onTime;
        }

        NextRunAt = Next(expression, now, zone);
        return runs;
    }

    /// <summary>A run asked for by a user, outside the schedule (which stays as it is).</summary>
    public ScheduledTaskRun RunNow(DateTimeOffset now)
    {
        LastRunAt = now;
        return ScheduledTaskRun.Start(this, now, now);
    }

    /// <summary>The next <paramref name="count"/> occurrences after <paramref name="from"/> (a preview while editing).</summary>
    public static Result<DateTimeOffset[]> Preview(string cron, string timeZone, DateTimeOffset from, int count)
    {
        var expression = Parse(cron);
        if (expression is null)
        {
            return ScheduleErrors.CronInvalid;
        }

        var zone = FindZone(timeZone);
        if (zone is null)
        {
            return ScheduleErrors.TimeZoneInvalid;
        }

        var occurrences = new List<DateTimeOffset>(count);
        for (DateTimeOffset? at = Next(expression, from, zone); at is { } next && occurrences.Count < count;
             at = Next(expression, next, zone))
        {
            occurrences.Add(next);
        }

        return occurrences.Count == 0 ? ScheduleErrors.NeverRuns : occurrences.ToArray();
    }

    private bool TrySchedule([NotNullWhen(true)] out CronExpression? expression, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        // Parsed once per loaded task: the stored values were valid when they were saved, but this panel may not read them.
        _expression ??= Parse(CronExpression);
        _zone ??= FindZone(TimeZone);
        (expression, zone) = (_expression, _zone);
        return expression is not null && zone is not null;
    }

    /// <summary>The next occurrence in UTC (Cronos gives it in the zone's offset; the database stores UTC only).</summary>
    private static DateTimeOffset? Next(CronExpression expression, DateTimeOffset from, TimeZoneInfo zone, bool inclusive = false) =>
        expression.GetNextOccurrence(from, zone, inclusive)?.ToUniversalTime();

    private static CronExpression? Parse(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron) || cron.Length > MaxCronLength)
        {
            return null;
        }

        try
        {
            // Standard format: five fields, no seconds.
            return Cronos.CronExpression.TryParse(cron.Trim(), CronFormat.Standard, out var expression) ? expression : null;
        }
        catch (MissingSeedException)
        {
            return null; // hashed fields (H) need a seed
        }
    }

    /// <summary>IANA ids only: Windows ids exist only on Windows (the panel may run on Linux).</summary>
    private static TimeZoneInfo? FindZone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaxTimeZoneLength && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) && zone.HasIanaId
            ? zone
            : null;
}
