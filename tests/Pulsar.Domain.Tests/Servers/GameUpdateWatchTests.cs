using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests.Servers;

public sealed class GameUpdateWatchTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AServerIsCheckedAndNotifiedByDefault()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);

        Assert.Equal(GameUpdatePolicy.Notify, watch.Policy);
        Assert.True(watch.CheckDue(s_now));
        watch.Asked(s_now);
        Assert.True(watch.AwaitsAnswer);
        Assert.False(watch.CheckDue(s_now.AddMinutes(14)));
        Assert.True(watch.CheckDue(s_now + GameUpdateWatch.CheckInterval));
    }

    [Fact]
    public void AnInstallOrUpdateLeavesTheUpdateUnknownUntilTheNextCheck()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);
        watch.Change(GameUpdatePolicy.Install, false, 0, null, s_now);
        watch.Asked(s_now);
        watch.Checked("100", "200", true, null, null, s_now);
        Assert.True(watch.AutoUpdateDue);

        // Updated by hand (or by a scheduled task): no automatic update starts for what was just installed.
        watch.FilesChanged(s_now.AddMinutes(3));

        Assert.Null(watch.Available);
        Assert.False(watch.AutoUpdateDue);
        Assert.True(watch.CheckDue(s_now.AddMinutes(3)));
        Assert.Equal("100", watch.Installed); // until the check says otherwise
    }

    [Fact]
    public void ANodeThatDoesNotAnswerIsAskedAgainLater()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);
        watch.Asked(s_now);
        watch.CheckSoon(s_now.AddMinutes(1)); // a user asks again meanwhile

        Assert.True(watch.CheckDue(s_now.AddMinutes(1)));
        watch.Asked(s_now.AddMinutes(1));
        watch.CheckSoon(s_now.AddMinutes(2));
        Assert.True(watch.CheckDue(s_now.AddMinutes(2)));
    }

    [Fact]
    public void ANewBuildIsNotifiedOnceAndAtMostEveryHour()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);

        Assert.False(watch.Checked("100", "100", false, null, null, s_now));
        Assert.True(watch.Checked("100", "101", true, null, null, s_now));
        Assert.False(watch.Checked("100", "101", true, null, null, s_now.AddMinutes(15))); // already told
        Assert.False(watch.Checked("100", "102", true, null, null, s_now.AddMinutes(30))); // within the hour
        Assert.True(watch.Checked("100", "102", true, null, null, s_now.AddHours(1)));
        Assert.Equal("102", watch.NotifiedLatest);
        Assert.False(watch.AwaitsAnswer);
    }

    [Fact]
    public void AFailedCheckKeepsWhatTheLastOneFound()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);
        watch.Checked("100", "101", true, null, null, s_now);

        watch.Checked(null, null, null, "steamcmd.rate_limited", "FAILED (Rate Limit Exceeded)", s_now.AddMinutes(15));

        Assert.Equal(("100", "101", true), (watch.Installed, watch.Latest, watch.Available));
        Assert.Equal("steamcmd.rate_limited", watch.CheckErrorCode);
        watch.Checked("101", "101", false, null, null, s_now.AddMinutes(30));
        Assert.Null(watch.CheckErrorCode);
        Assert.Null(watch.CheckError);
    }

    [Fact]
    public void AnAutomaticUpdateIsTriedOncePerBuild()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);
        Assert.True(watch.Change(GameUpdatePolicy.Install, whenEmpty: true, warningMinutes: 5, null, s_now).IsSuccess);
        watch.Checked("100", "101", true, null, null, s_now);
        Assert.True(watch.AutoUpdateDue);

        var sequence = ServerSequenceId.New();
        watch.AutoUpdateStarted(sequence, s_now);
        Assert.False(watch.AutoUpdateDue);
        watch.AutoUpdateEnded(s_now.AddMinutes(10)); // it failed, say: the same build is not tried again
        Assert.False(watch.AutoUpdateDue);
        Assert.True(watch.CheckDue(s_now.AddMinutes(10))); // what is installed now is checked

        watch.Checked("100", "102", true, null, null, s_now.AddMinutes(11));
        Assert.True(watch.AutoUpdateDue);
        watch.Skip(s_now.AddMinutes(12)); // a user did not want this one
        Assert.False(watch.AutoUpdateDue);
    }

    [Fact]
    public void AutomaticUpdatesStartAtMostOnceAnHour()
    {
        // A server's account can make every check report a new build: the panel does not restart it every 15 minutes.
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);
        watch.Change(GameUpdatePolicy.Install, false, 0, null, s_now);
        watch.Checked("1", "2", true, null, null, s_now);
        Assert.True(watch.AutoUpdateMayStart(s_now));
        watch.AutoUpdateStarted(ServerSequenceId.New(), s_now);
        watch.AutoUpdateEnded(s_now.AddMinutes(5));

        watch.Checked("2", "3", true, null, null, s_now.AddMinutes(15));
        Assert.True(watch.AutoUpdateDue);
        Assert.False(watch.AutoUpdateMayStart(s_now.AddMinutes(15)));
        Assert.Equal(s_now.AddHours(1), watch.AutoUpdateNotBefore);
        Assert.True(watch.AutoUpdateMayStart(s_now.AddHours(1)));

        // One a user asks for is not counted.
        watch.AutoUpdateStarted(ServerSequenceId.New(), s_now.AddHours(2), byItself: false);
        watch.AutoUpdateEnded(s_now.AddHours(2));
        watch.Checked("3", "4", true, null, null, s_now.AddHours(2));
        Assert.True(watch.AutoUpdateMayStart(s_now.AddHours(2)));
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(61, null)]
    [InlineData(5, "a‮b")]
    public void SettingsAreChecked(int minutes, string? message)
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);

        Assert.Equal(ServerErrors.GameUpdatesInvalid, watch.Change(GameUpdatePolicy.Install, false, minutes, message, s_now).Error);
        Assert.Equal(ServerErrors.GameUpdatesInvalid,
            watch.Change(GameUpdatePolicy.Install, false, 5, new string('x', GameUpdateWatch.MaxWarningMessageLength + 1), s_now).Error);
        Assert.Equal(ServerErrors.GameUpdatesInvalid, watch.Change((GameUpdatePolicy)9, false, 5, null, s_now).Error);
    }

    [Fact]
    public void TheDefaultWarningIsNotStored()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);

        watch.Change(GameUpdatePolicy.Install, false, 5, GameUpdateWatch.DefaultWarningMessage, s_now);
        Assert.Null(watch.WarningMessage);
        watch.Change(GameUpdatePolicy.Install, false, 5, " Reinicio en {time} por una actualización ", s_now);
        Assert.Equal("Reinicio en {time} por una actualización", watch.WarningMessage);
    }

    [Fact]
    public void TurningChecksBackOnChecksNow()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);
        watch.Asked(s_now);
        watch.Checked("1", "1", false, null, null, s_now);
        watch.Change(GameUpdatePolicy.Off, false, 0, null, s_now);
        Assert.False(watch.CheckDue(s_now.AddHours(1)));

        watch.Change(GameUpdatePolicy.Notify, false, 0, null, s_now.AddHours(2));

        Assert.True(watch.CheckDue(s_now.AddHours(2)));
    }

    [Fact]
    public void ANewerTemplateIsNotifiedOnceAndMovedToOncePerVersion()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);
        Assert.Equal(GameUpdatePolicy.Notify, watch.TemplatePolicy);
        Assert.False(watch.TemplateUpgradeDue("1.4.0")); // notified only

        Assert.True(watch.TemplateNoticed("1.4.0", s_now));
        Assert.False(watch.TemplateNoticed("1.4.0", s_now.AddHours(5)));
        Assert.True(watch.TemplateNoticed("1.5.0", s_now.AddHours(6)));

        // The game's own policy is left as it is.
        Assert.True(watch.Change(GameUpdatePolicy.Off, GameUpdatePolicy.Install, false, 0, null, s_now).IsSuccess);
        Assert.Equal((GameUpdatePolicy.Off, GameUpdatePolicy.Install), (watch.Policy, watch.TemplatePolicy));
        Assert.True(watch.TemplateUpgradeMayStart("1.5.0", s_now));
        watch.TemplateUpgradeStarted(ServerSequenceId.New(), "1.5.0", s_now);
        Assert.False(watch.TemplateUpgradeDue("1.5.0")); // one runs
        Assert.False(watch.AutoUpdateDue);

        // It ended (failed, say): not tried again for that version, a newer one is, an hour after the last at most.
        watch.AutoUpdateEnded(s_now.AddMinutes(10));
        Assert.False(watch.TemplateUpgradeDue("1.5.0"));
        Assert.True(watch.TemplateUpgradeDue("1.6.0"));
        Assert.False(watch.TemplateUpgradeMayStart("1.6.0", s_now.AddMinutes(30)));
        Assert.True(watch.TemplateUpgradeMayStart("1.6.0", s_now.AddHours(1)));

        // Skipped by a user before it began: that version is not moved to by itself.
        watch.SkipTemplate("1.6.0", s_now.AddHours(1));
        Assert.False(watch.TemplateUpgradeDue("1.6.0"));

        // Off: nothing is notified or moved to.
        watch.Change(GameUpdatePolicy.Off, GameUpdatePolicy.Off, false, 0, null, s_now.AddHours(2));
        Assert.False(watch.TemplateNoticed("1.7.0", s_now.AddHours(2)));
        Assert.False(watch.TemplateUpgradeDue("1.7.0"));
    }

    [Fact]
    public void OnlyTheTemplateUpdatesMoveAServerToAnotherTemplate()
    {
        SequenceStep[] steps = [new(SequenceStepKind.Stop), new(SequenceStepKind.UpgradeTemplate, Guid.NewGuid().ToString())];

        Assert.True(ServerSequence.Start(ServerId.New(), SequenceOrigin.TemplateUpdate, "Game updates", steps, true, s_now).IsSuccess);
        Assert.Equal(ScheduleErrors.StepsInvalid, ServerSequence.Start(ServerId.New(), SequenceOrigin.GameUpdate, "Game updates", steps, true, s_now).Error);
        Assert.Equal(ScheduleErrors.StepsInvalid, ServerSequence.Start(ServerId.New(), SequenceOrigin.Schedule, "A task", steps, true, s_now).Error);
        Assert.Equal(ScheduleErrors.StepsInvalid, ServerSequence.Start(ServerId.New(), SequenceOrigin.TemplateUpdate, "Game updates",
            [new SequenceStep(SequenceStepKind.UpgradeTemplate, "not a template")], true, s_now).Error);
    }

    [Fact]
    public void ATemplatePolicyThatIsNoneOfThemIsRefused()
    {
        var watch = GameUpdateWatch.Create(ServerId.New(), s_now);

        Assert.Equal(ServerErrors.GameUpdatesInvalid, watch.Change(GameUpdatePolicy.Notify, (GameUpdatePolicy)7, false, 0, null, s_now).Error);
        Assert.Equal(GameUpdatePolicy.Notify, watch.TemplatePolicy);
    }
}
