using System.Reflection;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests.Scheduling;

public sealed class ScheduledTaskTests
{
    private const string BuenosAires = "America/Argentina/Buenos_Aires";
    private const string NewYork = "America/New_York";

    // 09:00 in Buenos Aires (UTC-3 all year).
    private static readonly DateTimeOffset s_now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADailyTaskRunsAtItsLocalTime()
    {
        var task = Daily("0 4 * * *", BuenosAires);

        Assert.Equal(new DateTimeOffset(2026, 10, 4, 7, 0, 0, TimeSpan.Zero), task.NextRunAt);
        Assert.Equal(TimeSpan.Zero, task.NextRunAt!.Value.Offset); // stored as UTC
        Assert.Equal(ScheduledRunStatus.Running, Assert.Single(task.Due(task.NextRunAt!.Value)).Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero), task.NextRunAt);
    }

    [Fact]
    public void OccurrencesFollowDaylightSavingTime()
    {
        // New York: EDT (UTC-4) until 2026-11-01, EST (UTC-5) until 2027-03-14.
        var task = Daily("0 4 * * *", NewYork, now: new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), task.NextRunAt);
        task.Due(task.NextRunAt!.Value);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), task.NextRunAt);

        // 02:30 does not exist on 2027-03-14 (02:00 jumps to 03:00): it runs once, when the gap ends.
        var gap = Daily("30 2 * * *", NewYork, now: new DateTimeOffset(2027, 3, 13, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2027, 3, 14, 7, 0, 0, TimeSpan.Zero), gap.NextRunAt);

        // 01:30 happens twice on 2026-11-01: it runs once.
        var twice = Daily("30 1 * * *", NewYork, now: new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), twice.NextRunAt);
        twice.Due(twice.NextRunAt!.Value);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), twice.NextRunAt);
    }

    [Fact]
    public void ARunALittleLateStillRuns()
    {
        var task = Daily("0 4 * * *", BuenosAires);
        var due = task.NextRunAt!.Value;

        var run = Assert.Single(task.Due(due + TimeSpan.FromSeconds(50)));

        Assert.Equal(ScheduledRunStatus.Running, run.Status);
        Assert.Equal(due, run.ScheduledFor);
        Assert.Equal(due, task.LastRunAt);
    }

    [Fact]
    public void RunsMissedWhileThePanelWasDownAreSkippedTogether()
    {
        var task = Daily("0 4 * * *", BuenosAires);
        var first = task.NextRunAt!.Value;

        // Down for three days, back at 08:00 local: three runs were missed, none runs now.
        var runs = task.Due(first + TimeSpan.FromDays(2) + TimeSpan.FromHours(4));

        var missed = Assert.Single(runs);
        Assert.Equal(ScheduledRunStatus.Skipped, missed.Status);
        Assert.Equal(ScheduleErrors.Missed.Code, missed.ErrorCode);
        Assert.Equal(first, missed.ScheduledFor);
        Assert.Equal(3, missed.Occurrences);
        Assert.NotNull(missed.FinishedAt);
        Assert.Equal(first + TimeSpan.FromDays(3), task.NextRunAt);
    }

    [Fact]
    public void ARunDueNowStillRunsAfterTheMissedOnes()
    {
        var task = Daily("0 * * * *", BuenosAires);
        var first = task.NextRunAt!.Value;

        var runs = task.Due(first + TimeSpan.FromHours(5) + TimeSpan.FromSeconds(20));

        Assert.Collection(runs,
            missed =>
            {
                Assert.Equal(ScheduledRunStatus.Skipped, missed.Status);
                Assert.Equal(5, missed.Occurrences);
            },
            now =>
            {
                Assert.Equal(ScheduledRunStatus.Running, now.Status);
                Assert.Equal(first + TimeSpan.FromHours(5), now.ScheduledFor);
            });
        Assert.Equal(first + TimeSpan.FromHours(6), task.NextRunAt);
    }

    [Fact]
    public void AScheduleThatCanNoLongerBeReadEndsInAFailedRunNotAnError()
    {
        // Valid when saved; a later time zone database or cron library may not read it (as a row loaded from the database).
        foreach (var (property, value) in new[] { ("TimeZone", "Mars/Olympus_Mons"), ("CronExpression", "61 * * * *") })
        {
            var task = Daily("0 4 * * *", BuenosAires);
            typeof(ScheduledTask).GetProperty(property)!.SetValue(task, value);
            foreach (var parsed in new[] { "_expression", "_zone" })
            {
                typeof(ScheduledTask).GetField(parsed, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(task, null);
            }

            var due = task.NextRunAt!.Value;

            var run = Assert.Single(task.Due(due));

            Assert.Equal((ScheduledRunStatus.Failed, ScheduleErrors.Unreadable.Code), (run.Status, run.ErrorCode));
            // Due no more: every other task of the panel is taken and runs.
            Assert.Null(task.NextRunAt);
            Assert.Empty(task.Due(due + TimeSpan.FromDays(1)));
        }
    }

    [Theory]
    [InlineData("*/5 * * * *", false)]
    [InlineData("0,10 4 * * *", false)] // twice a night, ten minutes apart
    [InlineData("59 23 * * *", true)]
    [InlineData("0,59 0,23 1,2 * *", false)] // 23:59 on the 1st, then 00:00 on the 2nd
    [InlineData("50 23 31 * *", true)]
    [InlineData("*/15 * * * *", true)]
    [InlineData("0 */6 * * *", true)]
    public void ScheduledBackupsAreAtLeastFifteenMinutesApart(string cron, bool allowed)
    {
        var backup = ScheduledTask.Create(Server(), "Backup", cron, "UTC", ScheduledAction.Backup, ScheduledActionPayload.None, ScheduledConditions.None, true, null, s_now);

        Assert.Equal(allowed ? null : ScheduleErrors.BackupTooOften, backup.Error);
        // Other actions run as often as the schedule says.
        Assert.True(ScheduledTask.Create(Server(), "Restart", cron, "UTC", ScheduledAction.Restart, ScheduledActionPayload.None, ScheduledConditions.None, true, null, s_now)
            .IsSuccess);
    }

    [Fact]
    public void ATaskMadeABackupTaskIsCheckedLikeANewOne()
    {
        var task = Create("*/5 * * * *", "UTC").Value;

        Assert.Equal(ScheduleErrors.BackupTooOften,
            task.Change("Backup", "*/5 * * * *", "UTC", ScheduledAction.Backup, ScheduledActionPayload.None, ScheduledConditions.None, true, s_now).Error);
        Assert.Equal(ScheduledAction.Restart, task.Action);
    }

    [Fact]
    public void NothingIsDueBeforeItsTime()
    {
        var task = Daily("0 4 * * *", BuenosAires);
        var next = task.NextRunAt;

        Assert.Empty(task.Due(next!.Value - TimeSpan.FromSeconds(1)));
        Assert.Equal(next, task.NextRunAt);
    }

    [Fact]
    public void ADisabledTaskHasNoNextRunAndEnablingItStartsFromNow()
    {
        var task = Daily("0 4 * * *", BuenosAires);

        Assert.True(task.Change("Nightly restart", "0 4 * * *", BuenosAires, ScheduledAction.Restart, ScheduledActionPayload.None, ScheduledConditions.None,
            enabled: false, s_now).IsSuccess);
        Assert.Null(task.NextRunAt);
        Assert.Empty(task.Due(s_now + TimeSpan.FromDays(5)));

        var later = s_now + TimeSpan.FromDays(5);
        Assert.True(task.Change("Nightly restart", "0 4 * * *", BuenosAires, ScheduledAction.Restart, ScheduledActionPayload.None, ScheduledConditions.None,
            enabled: true, later).IsSuccess);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 7, 0, 0, TimeSpan.Zero), task.NextRunAt);
        Assert.Empty(task.Due(later));
    }

    [Fact]
    public void ChangingTheScheduleMovesTheNextRun()
    {
        var task = Daily("0 4 * * *", BuenosAires);

        Assert.True(task.Change("Nightly restart", "30 6 * * 1", "UTC", ScheduledAction.Restart, ScheduledActionPayload.None, ScheduledConditions.None, true, s_now)
            .IsSuccess);

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 6, 30, 0, TimeSpan.Zero), task.NextRunAt);
        Assert.Equal("UTC", task.TimeZone);
    }

    [Fact]
    public void RunningItNowLeavesTheNextRunAlone()
    {
        var task = Daily("0 4 * * *", BuenosAires);
        var next = task.NextRunAt;

        var run = task.RunNow(s_now);

        Assert.Equal(ScheduledRunStatus.Running, run.Status);
        Assert.Equal(s_now, run.ScheduledFor);
        Assert.Equal(next, task.NextRunAt);
    }

    [Theory]
    [InlineData("0 4 * *")] // four fields
    [InlineData("0 0 4 * * *")] // seconds are not offered
    [InlineData("61 4 * * *")]
    [InlineData("H 4 * * *")] // hashed fields need a seed
    [InlineData("")]
    public void InvalidCronExpressionsAreRefused(string cron) =>
        Assert.Equal(ScheduleErrors.CronInvalid, Create(cron, BuenosAires).Error);

    [Fact]
    public void AnExpressionThatNeverRunsIsRefused() =>
        Assert.Equal(ScheduleErrors.NeverRuns, Create("0 0 30 2 *", BuenosAires).Error);

    [Theory]
    [InlineData("Eastern Standard Time")] // Windows ids are not portable
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("")]
    public void TimeZonesAreIanaIds(string zone) => Assert.Equal(ScheduleErrors.TimeZoneInvalid, Create("0 4 * * *", zone).Error);

    [Theory]
    [InlineData("America/Buenos_Aires")] // browsers list the zone by its older name (CLDR)
    [InlineData("Asia/Calcutta")]
    [InlineData("UTC")]
    [InlineData("Etc/UTC")]
    public void ZoneNamesBrowsersOfferAreAccepted(string zone)
    {
        var task = Create("0 4 * * *", zone);

        Assert.True(task.IsSuccess, task.Error?.Code);
        Assert.Equal(zone, task.Value.TimeZone);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    public void NamesAreOneLine(string name) =>
        Assert.Equal(ScheduleErrors.NameInvalid, ScheduledTask.Create(Server(), name, "0 4 * * *", "UTC", ScheduledAction.Restart,
            ScheduledActionPayload.None, ScheduledConditions.None, true, null, s_now).Error);

    [Theory]
    [InlineData(ScheduledAction.Start)]
    [InlineData(ScheduledAction.Stop)]
    [InlineData(ScheduledAction.Update)]
    [InlineData(ScheduledAction.ConsoleCommand)]
    public void OnlyRestartsBackupsAndStepsCanDependOnTheServerRunning(ScheduledAction action)
    {
        var payload = action == ScheduledAction.ConsoleCommand ? new ScheduledActionPayload("say hi") : ScheduledActionPayload.None;
        Assert.Equal(ScheduleErrors.OnlyIfRunningUnsupported,
            ScheduledTask.Create(Server(), "Task", "0 4 * * *", "UTC", action, payload, new ScheduledConditions(OnlyIfRunning: true), true, null, s_now).Error);
    }

    [Fact]
    public void ConsoleCommandsNeedACommandAndOtherActionsDropIt()
    {
        Assert.Equal(ScheduleErrors.CommandRequired, ScheduledTask.Create(Server(), "Say", "0 4 * * *", "UTC", ScheduledAction.ConsoleCommand,
            ScheduledActionPayload.None, ScheduledConditions.None, true, null, s_now).Error);

        var restart = ScheduledTask.Create(Server(), "Restart", "0 4 * * *", "UTC", ScheduledAction.Restart, new ScheduledActionPayload("say hi", true),
            ScheduledConditions.None, true, null, s_now).Value;
        Assert.Equal(ScheduledActionPayload.None, restart.Payload);

        var update = ScheduledTask.Create(Server(), "Update", "0 4 * * *", "UTC", ScheduledAction.Update, new ScheduledActionPayload("say hi", true),
            ScheduledConditions.None, true, null, s_now).Value;
        Assert.Equal(new ScheduledActionPayload(null, true), update.Payload);
    }

    [Fact]
    public void ATaskOfSeveralStepsKeepsWhatEachStepUses()
    {
        var task = Steps([
            new SequenceStep(SequenceStepKind.Broadcast, "  Restarting in 5 min  ", Seconds: 9, Validate: true),
            new SequenceStep(SequenceStepKind.Wait, "ignored", Seconds: 240, OnlyIfWasRunning: true),
            new SequenceStep(SequenceStepKind.Command, "save-all", Always: true),
            new SequenceStep(SequenceStepKind.Stop, "ignored", Seconds: 5),
            new SequenceStep(SequenceStepKind.Update, Validate: true, OnlyIfWasRunning: true),
            new SequenceStep(SequenceStepKind.Start, "ignored", OnlyIfWasRunning: true, Always: true),
        ]).Value;

        Assert.Equal(ScheduledAction.Sequence, task.Action);
        Assert.Null(task.Payload.Command);
        Assert.Equal(
        [
            new SequenceStep(SequenceStepKind.Broadcast, "Restarting in 5 min"),
            new SequenceStep(SequenceStepKind.Wait, Seconds: 240),
            new SequenceStep(SequenceStepKind.Command, "save-all", Always: true),
            new SequenceStep(SequenceStepKind.Stop),
            new SequenceStep(SequenceStepKind.Update, Validate: true),
            new SequenceStep(SequenceStepKind.Start, OnlyIfWasRunning: true, Always: true),
        ], task.Payload.Steps!);

        // Other actions keep no steps.
        var restart = ScheduledTask.Create(Server(), "Restart", "0 4 * * *", "UTC", ScheduledAction.Restart,
            new ScheduledActionPayload(Steps: [new SequenceStep(SequenceStepKind.Stop)]), ScheduledConditions.None, true, null, s_now).Value;
        Assert.Null(restart.Payload.Steps);
    }

    public static TheoryData<SequenceStep[]> InvalidSteps => new()
    {
        Array.Empty<SequenceStep>(),
        Enumerable.Repeat(new SequenceStep(SequenceStepKind.Restart), ServerSequence.MaxSteps + 1).ToArray(),
        new[] { new SequenceStep(SequenceStepKind.Wait) },
        new[] { new SequenceStep(SequenceStepKind.Wait, Seconds: SequenceStep.MaxWaitSeconds + 1) },
        new[] { new SequenceStep(SequenceStepKind.Command, "  ") },
        new[] { new SequenceStep(SequenceStepKind.Command, "say a\nquit") },
        new[] { new SequenceStep(SequenceStepKind.Command, new string('a', SequenceStep.MaxTextLength + 1)) },
        new[] { new SequenceStep(SequenceStepKind.Broadcast, new string('a', SequenceStep.MaxMessageLength + 1)) },
        new[] { new SequenceStep((SequenceStepKind)42) },
        new SequenceStep[] { null! },
    };

    [Theory]
    [MemberData(nameof(InvalidSteps))]
    public void ATaskOfSeveralStepsNeedsValidOnes(SequenceStep[] steps)
    {
        Assert.Equal(ScheduleErrors.StepsInvalid, Steps(steps).Error);
        Assert.Equal(ScheduleErrors.StepsInvalid, Steps(null).Error);
    }

    [Fact]
    public void ATaskOfSeveralStepsHasAtMostTheMostStepsAndBroadcastsTheLongestMessage()
    {
        Assert.True(Steps([.. Enumerable.Repeat(new SequenceStep(SequenceStepKind.Restart), ServerSequence.MaxSteps)]).IsSuccess);
        Assert.True(Steps([new SequenceStep(SequenceStepKind.Broadcast, new string('a', SequenceStep.MaxMessageLength))]).IsSuccess);
        Assert.True(Steps([new SequenceStep(SequenceStepKind.Wait, Seconds: SequenceStep.MaxWaitSeconds)]).IsSuccess);
    }

    [Fact]
    public void ABackupStepIsAsFarApartAsScheduledBackups()
    {
        Assert.Equal(ScheduleErrors.BackupTooOften,
            Steps([new SequenceStep(SequenceStepKind.Broadcast, "Backing up"), new SequenceStep(SequenceStepKind.Backup)], "*/5 * * * *").Error);
        Assert.True(Steps([new SequenceStep(SequenceStepKind.Restart)], "*/5 * * * *").IsSuccess);
        Assert.True(Steps([new SequenceStep(SequenceStepKind.Backup)], "*/15 * * * *").IsSuccess);
        // One backup a run: not a burst of them seconds apart.
        Assert.Equal(ScheduleErrors.BackupTooOften, Steps([new SequenceStep(SequenceStepKind.Backup), new SequenceStep(SequenceStepKind.Wait, Seconds: 1),
            new SequenceStep(SequenceStepKind.Backup)], "0 4 * * *").Error);
    }

    [Fact]
    public void AnActionThePanelDoesNotKnowIsRefused()
    {
        Assert.Equal(ScheduleErrors.ActionInvalid, ScheduledTask.Create(Server(), "Task", "0 4 * * *", "UTC", (ScheduledAction)99,
            ScheduledActionPayload.None, ScheduledConditions.None, true, null, s_now).Error);
    }

    [Theory]
    [InlineData(ScheduledAction.Start, false, false)]
    [InlineData(ScheduledAction.ConsoleCommand, false, false)]
    [InlineData(ScheduledAction.Stop, true, false)]
    [InlineData(ScheduledAction.Restart, true, false)]
    [InlineData(ScheduledAction.Backup, true, false)]
    [InlineData(ScheduledAction.Update, true, true)]
    [InlineData(ScheduledAction.Sequence, true, true)]
    public void ConditionsDependOnTheAction(ScheduledAction action, bool mayWaitForEmpty, bool mayWaitForUpdate)
    {
        var payload = action switch
        {
            ScheduledAction.ConsoleCommand => new ScheduledActionPayload("say hi"),
            ScheduledAction.Sequence => new ScheduledActionPayload(Steps: [new SequenceStep(SequenceStepKind.Restart)]),
            _ => ScheduledActionPayload.None,
        };
        Result<ScheduledTask> With(ScheduledConditions conditions) =>
            ScheduledTask.Create(Server(), "Task", "0 4 * * *", "UTC", action, payload, conditions, true, null, s_now);

        var empty = With(new ScheduledConditions(OnlyIfEmpty: true));
        Assert.Equal(mayWaitForEmpty ? null : ScheduleErrors.OnlyIfEmptyUnsupported, empty.Error);
        Assert.Equal(mayWaitForEmpty, empty.IsSuccess && empty.Value.OnlyIfEmpty);

        var update = With(new ScheduledConditions(OnlyIfUpdateAvailable: true));
        Assert.Equal(mayWaitForUpdate ? null : ScheduleErrors.OnlyIfUpdateUnsupported, update.Error);
        Assert.Equal(mayWaitForUpdate, update.IsSuccess && update.Value.OnlyIfUpdateAvailable);

        if (action == ScheduledAction.Sequence)
        {
            var all = With(new ScheduledConditions(true, true, true)).Value;
            Assert.Equal(new ScheduledConditions(true, true, true), all.Conditions);
        }
    }

    [Theory]
    [InlineData(SequenceStatus.Succeeded, ScheduledRunStatus.Succeeded, null)]
    [InlineData(SequenceStatus.Failed, ScheduledRunStatus.Failed, "server.busy")]
    [InlineData(SequenceStatus.Cancelled, ScheduledRunStatus.Skipped, "schedules.cancelled")]
    public void ARunOfSeveralStepsTakesTheirOutcome(SequenceStatus ended, ScheduledRunStatus status, string? code)
    {
        var run = Steps([new SequenceStep(SequenceStepKind.Restart)]).Value.RunNow(s_now);
        var sequence = ServerSequenceId.New();
        run.Sequenced(sequence);

        run.SequenceEnded(SequenceStatus.Running, null, null, s_now.AddMinutes(1));
        Assert.Equal(ScheduledRunStatus.Running, run.Status); // still going

        run.SequenceEnded(ended, ended == SequenceStatus.Failed ? "server.busy" : null, "detail", s_now.AddMinutes(5));
        Assert.Equal(status, run.Status);
        Assert.Equal(code, run.ErrorCode);
        Assert.Equal(sequence, run.SequenceId);
        Assert.Equal(s_now.AddMinutes(5), run.FinishedAt);

        run.SequenceEnded(SequenceStatus.Failed, "late", null, s_now.AddMinutes(9));
        Assert.Equal(status, run.Status); // once
    }

    [Fact]
    public void ARunEndsOnce()
    {
        var run = Daily("0 4 * * *", BuenosAires).RunNow(s_now);
        var operation = OperationId.New();

        run.Dispatched(operation, s_now);
        run.Failed("server.busy", "busy", s_now);

        Assert.Equal(ScheduledRunStatus.Succeeded, run.Status);
        Assert.Equal(operation, run.OperationId);
        Assert.Null(run.ErrorCode);
    }

    private static ScheduledTask Daily(string cron, string zone, DateTimeOffset? now = null) =>
        ScheduledTask.Create(Server(), "Nightly restart", cron, zone, ScheduledAction.Restart, ScheduledActionPayload.None, ScheduledConditions.None, true, null,
            now ?? s_now).Value;

    private static Result<ScheduledTask> Steps(IReadOnlyList<SequenceStep>? steps, string cron = "0 4 * * *") =>
        ScheduledTask.Create(Server(), "Steps", cron, "UTC", ScheduledAction.Sequence, new ScheduledActionPayload(Steps: steps), ScheduledConditions.None,
            true, null, s_now);

    private static Result<ScheduledTask> Create(string cron, string zone) =>
        ScheduledTask.Create(Server(), "Task", cron, zone, ScheduledAction.Restart, ScheduledActionPayload.None, ScheduledConditions.None, true, null, s_now);

    private static Server Server() =>
        Domain.Servers.Server.Create(NodeId.New(), TemplateId.New(), "Server", null, Domain.Servers.Server.NewShortId(), s_now).Value;
}
