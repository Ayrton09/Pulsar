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

    public static DomainError ActionInvalid { get; } = new("schedules.action_invalid", "The task's action is not one the panel knows.");

    public static DomainError OnlyIfRunningUnsupported { get; } =
        new("schedules.only_if_running_unsupported", "Only restarts, backups and tasks of several steps can depend on the server running.");

    public static DomainError OnlyIfEmptyUnsupported { get; } =
        new("schedules.only_if_empty_unsupported", "Starts and console commands cannot depend on nobody playing.");

    public static DomainError OnlyIfUpdateUnsupported { get; } =
        new("schedules.only_if_update_unsupported", "Only updates and tasks of several steps can depend on a game update.");

    /// <summary>The game has no query: the panel cannot tell whether anyone plays.</summary>
    public static DomainError PlayersNotReported { get; } =
        new("schedules.players_not_reported", "The game does not say who plays (it has no query): a task cannot wait for nobody playing.");

    /// <summary>The server's game updates are not checked (its game cannot be, or the checks are off).</summary>
    public static DomainError UpdateChecksUnavailable { get; } = new("schedules.update_checks_unavailable",
        "The server's game updates are not checked (its game cannot be, or the checks are off in its settings).");

    public static DomainError BroadcastUnsupported { get; } =
        new("schedules.broadcast_unsupported", "The game has no command to message its players.");

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

    /// <summary>The task runs only while nobody plays, and someone did.</summary>
    public static DomainError NotEmpty { get; } = new("schedules.not_empty", "Someone was playing.");

    /// <summary>The task runs only while nobody plays, and the game's query had not answered.</summary>
    public static DomainError PlayersUnknown { get; } =
        new("schedules.players_unknown", "Nobody could tell whether someone was playing: the game's query had not answered.");

    /// <summary>The task runs only when there is a game update, and the last check found none.</summary>
    public static DomainError NoUpdate { get; } = new("schedules.no_update", "No game update was available.");

    /// <summary>The task runs only when there is a game update, and the checks were turned off since.</summary>
    public static DomainError UpdateChecksOff { get; } =
        new("schedules.update_checks_off", "The server's game updates are no longer checked: turn the checks on in its settings.");

    public static DomainError StepsInvalid { get; } = new("schedules.steps_invalid",
        $"A task has 1 to {ServerSequence.MaxSteps} steps: commands and messages are one line (messages up to {SequenceStep.MaxMessageLength} characters), waits last 1 second to a day.");

    /// <summary>Another sequence of steps (a task's, an automatic update's) is running on the server.</summary>
    public static DomainError SequenceRunning { get; } =
        new("schedules.sequence_running", "Another task of several steps or an automatic update is running on the server.");

    public static DomainError SequenceOver { get; } = new("schedules.sequence_over", "It is no longer running.");

    public static DomainError Cancelled { get; } = new("schedules.cancelled", "Cancelled by a user.");

    /// <summary>The server became another game (1.0.17): what was left of a sequence was the old game's, and none of it was done.</summary>
    public static DomainError GameChanged { get; } = new("schedules.game_changed", "The server became another game: what was left was not done.");

    /// <summary>A step waited longer than it may for the server (to stop, to be running).</summary>
    public static DomainError StepTimedOut { get; } = new("schedules.step_timed_out", "The server did not get there in time.");

    /// <summary>The server stopped (failed to start, crashed, was stopped) while a step waited for it to run.</summary>
    public static DomainError StartFailed { get; } = new("schedules.start_failed", "The server did not start.");

    /// <summary>The server could not be installed again with the newer template a template update moved it to.</summary>
    public static DomainError TemplateInstallFailed { get; } = new("schedules.template_install_failed", "The server could not be installed again with the new template.");

    /// <summary>The server was started again or stopped by someone else while a step waited for it.</summary>
    public static DomainError StepInterfered { get; } = new("schedules.step_interfered", "The server did not do what the step asked.");

    /// <summary>A schedule valid when saved that this panel cannot read (a later time zone database or cron library).</summary>
    public static DomainError Unreadable { get; } =
        new("schedules.unreadable", "The task's schedule (its cron expression or time zone) can no longer be read: edit it to run it again.");
}
