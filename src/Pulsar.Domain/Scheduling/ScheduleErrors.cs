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
}
