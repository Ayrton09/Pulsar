using Pulsar.Domain.Common;

namespace Pulsar.Domain.Scheduling;

public static class ScheduleErrors
{
    public static DomainError NotFound { get; } = new("schedules.not_found", "The scheduled task does not exist.");

    public static DomainError NameInvalid { get; } =
        new("schedules.name_invalid", $"A task name is one line of 1 to {ScheduledTask.MaxNameLength} characters.");

    public static DomainError CronInvalid { get; } =
        new("schedules.cron_invalid", "The schedule is not a cron expression of five fields (minute, hour, day of month, month, day of week).");

    public static DomainError NeverRuns { get; } = new("schedules.never_runs", "The schedule has no future occurrence.");

    public static DomainError TimeZoneInvalid { get; } = new("schedules.time_zone_invalid", "The time zone is not an IANA time zone (e.g. Europe/Madrid).");

    public static DomainError OnlyIfRunningUnsupported { get; } =
        new("schedules.only_if_running_unsupported", "Only restarts and backups can depend on the server running.");

    public static DomainError CommandRequired { get; } = new("schedules.command_required", "A console command task needs a valid command.");

    public static DomainError BackupTooOften { get; } = new("schedules.backup_too_often",
        $"Scheduled backups are at least {(int)ScheduledTask.MinBackupInterval.TotalMinutes} minutes apart.");

    public static DomainError CommandSensitive { get; } =
        new("schedules.command_sensitive", "A scheduled command cannot set a secret: it would be kept and shown in clear.");

    public static DomainError TooMany { get; } =
        new("schedules.too_many", $"A server has at most {ScheduledTask.MaxTasksPerServer} scheduled tasks.");

    public static DomainError Forbidden { get; } =
        new("schedules.forbidden", "Scheduling an action needs the permission to do it.");

    /// <summary>Its runs came while the panel was down: none of them ran, and none runs late.</summary>
    public static DomainError Missed { get; } = new("schedules.missed", "The panel was not running at the scheduled time.");

    /// <summary>The panel stopped between taking the run and doing it.</summary>
    public static DomainError Interrupted { get; } = new("schedules.interrupted", "The panel stopped before the run ended.");

    /// <summary>The task runs only while the server does, and it did not.</summary>
    public static DomainError NotRunning { get; } = new("schedules.not_running", "The server was not running.");

    public static DomainError StepsInvalid { get; } = new("schedules.steps_invalid",
        $"A task has 1 to {ServerSequence.MaxSteps} steps: commands and messages are one line, waits last 1 second to a day.");

    /// <summary>Another sequence of steps (a task's, an automatic update's) is running on the server.</summary>
    public static DomainError SequenceRunning { get; } =
        new("schedules.sequence_running", "Another task of several steps or an automatic update is running on the server.");

    public static DomainError SequenceOver { get; } = new("schedules.sequence_over", "It is no longer running.");

    public static DomainError Cancelled { get; } = new("schedules.cancelled", "Cancelled by a user.");

    /// <summary>A step waited longer than it may for the server (to stop, to be running).</summary>
    public static DomainError StepTimedOut { get; } = new("schedules.step_timed_out", "The server did not get there in time.");

    /// <summary>The server stopped (failed to start, crashed, was stopped) while a step waited for it to run.</summary>
    public static DomainError StartFailed { get; } = new("schedules.start_failed", "The server did not start.");

    /// <summary>The server was started again or stopped by someone else while a step waited for it.</summary>
    public static DomainError StepInterfered { get; } = new("schedules.step_interfered", "The server did not do what the step asked.");

    /// <summary>A schedule valid when saved that this panel cannot read (a later time zone database or cron library).</summary>
    public static DomainError Unreadable { get; } =
        new("schedules.unreadable", "The task's schedule (its cron expression or time zone) can no longer be read: edit it to run it again.");
}
