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
        var backup = ScheduledTask.Create(Server(), "Backup", cron, "UTC", ScheduledAction.Backup, ScheduledActionPayload.None, false, true, null, s_now);

        Assert.Equal(allowed ? null : ScheduleErrors.BackupTooOften, backup.Error);
        // Other actions run as often as the schedule says.
        Assert.True(ScheduledTask.Create(Server(), "Restart", cron, "UTC", ScheduledAction.Restart, ScheduledActionPayload.None, false, true, null, s_now)
            .IsSuccess);
    }

    [Fact]
    public void ATaskMadeABackupTaskIsCheckedLikeANewOne()
    {
        var task = Create("*/5 * * * *", "UTC").Value;

        Assert.Equal(ScheduleErrors.BackupTooOften,
            task.Change("Backup", "*/5 * * * *", "UTC", ScheduledAction.Backup, ScheduledActionPayload.None, false, true, s_now).Error);
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

        Assert.True(task.Change("Nightly restart", "0 4 * * *", BuenosAires, ScheduledAction.Restart, ScheduledActionPayload.None, false,
            enabled: false, s_now).IsSuccess);
        Assert.Null(task.NextRunAt);
        Assert.Empty(task.Due(s_now + TimeSpan.FromDays(5)));

        var later = s_now + TimeSpan.FromDays(5);
        Assert.True(task.Change("Nightly restart", "0 4 * * *", BuenosAires, ScheduledAction.Restart, ScheduledActionPayload.None, false,
            enabled: true, later).IsSuccess);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 7, 0, 0, TimeSpan.Zero), task.NextRunAt);
        Assert.Empty(task.Due(later));
    }

    [Fact]
    public void ChangingTheScheduleMovesTheNextRun()
    {
        var task = Daily("0 4 * * *", BuenosAires);

        Assert.True(task.Change("Nightly restart", "30 6 * * 1", "UTC", ScheduledAction.Restart, ScheduledActionPayload.None, false, true, s_now)
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
            ScheduledActionPayload.None, false, true, null, s_now).Error);

    [Theory]
    [InlineData(ScheduledAction.Start)]
    [InlineData(ScheduledAction.Stop)]
    [InlineData(ScheduledAction.Update)]
    [InlineData(ScheduledAction.ConsoleCommand)]
    public void OnlyRestartsAndBackupsCanDependOnTheServerRunning(ScheduledAction action)
    {
        var payload = action == ScheduledAction.ConsoleCommand ? new ScheduledActionPayload("say hi") : ScheduledActionPayload.None;
        Assert.Equal(ScheduleErrors.OnlyIfRunningUnsupported,
            ScheduledTask.Create(Server(), "Task", "0 4 * * *", "UTC", action, payload, onlyIfRunning: true, true, null, s_now).Error);
    }

    [Fact]
    public void ConsoleCommandsNeedACommandAndOtherActionsDropIt()
    {
        Assert.Equal(ScheduleErrors.CommandRequired, ScheduledTask.Create(Server(), "Say", "0 4 * * *", "UTC", ScheduledAction.ConsoleCommand,
            ScheduledActionPayload.None, false, true, null, s_now).Error);

        var restart = ScheduledTask.Create(Server(), "Restart", "0 4 * * *", "UTC", ScheduledAction.Restart, new ScheduledActionPayload("say hi", true),
            false, true, null, s_now).Value;
        Assert.Equal(ScheduledActionPayload.None, restart.Payload);

        var update = ScheduledTask.Create(Server(), "Update", "0 4 * * *", "UTC", ScheduledAction.Update, new ScheduledActionPayload("say hi", true),
            false, true, null, s_now).Value;
        Assert.Equal(new ScheduledActionPayload(null, true), update.Payload);
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
        ScheduledTask.Create(Server(), "Nightly restart", cron, zone, ScheduledAction.Restart, ScheduledActionPayload.None, false, true, null,
            now ?? s_now).Value;

    private static Result<ScheduledTask> Create(string cron, string zone) =>
        ScheduledTask.Create(Server(), "Task", cron, zone, ScheduledAction.Restart, ScheduledActionPayload.None, false, true, null, s_now);

    private static Server Server() =>
        Domain.Servers.Server.Create(NodeId.New(), TemplateId.New(), "Server", null, Domain.Servers.Server.NewShortId(), s_now).Value;
}
