using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;

namespace Pulsar.Domain.Tests.Scheduling;

public sealed class ServerSequenceTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StepsAreDoneInOrderAndTheSequenceSucceedsAfterTheLast()
    {
        var sequence = Start([new(SequenceStepKind.Stop), new(SequenceStepKind.Update), new(SequenceStepKind.Start)]);

        Assert.Equal(SequenceStepKind.Stop, sequence.Current?.Kind);
        Assert.Equal(s_now, sequence.WakeAt);
        sequence.Began(s_now, (4, s_now));
        Assert.True(sequence.StepBegun);
        Assert.False(sequence.ChangedSince(4, s_now));
        Assert.True(sequence.ChangedSince(5, s_now));
        sequence.StepDone(s_now.AddSeconds(5));
        Assert.Equal(SequenceStepKind.Update, sequence.Current?.Kind);
        Assert.False(sequence.StepBegun);
        Assert.False(sequence.ChangedSince(5, s_now)); // a step that has not begun saw nothing

        sequence.StepDone(s_now.AddSeconds(6));
        sequence.StepDone(s_now.AddSeconds(7));

        Assert.Equal(SequenceStatus.Succeeded, sequence.Status);
        Assert.Null(sequence.Current);
        Assert.Equal(s_now.AddSeconds(7), sequence.FinishedAt);
    }

    [Fact]
    public void AfterAFailureOnlyTheStepsThatAlwaysRunAreDone()
    {
        // An update that fails still puts the server back online, and the sequence still says it failed.
        var sequence = Start(
        [
            new(SequenceStepKind.Stop),
            new(SequenceStepKind.Update),
            new(SequenceStepKind.Broadcast, "skipped after a failure"),
            new(SequenceStepKind.Start, OnlyIfWasRunning: true, Always: true),
        ]);
        sequence.StepDone(s_now);

        sequence.StepFailed("steamcmd.failed", "SteamCMD exited with code 8.", s_now);

        Assert.Equal(SequenceStepKind.Start, sequence.Current?.Kind);
        Assert.Equal(SequenceStatus.Running, sequence.Status);
        sequence.StepFailed("schedules.start_failed", "a second failure is not the reason", s_now);
        Assert.Equal(SequenceStatus.Failed, sequence.Status);
        Assert.Equal("steamcmd.failed", sequence.ErrorCode); // the first one is
        Assert.Equal("SteamCMD exited with code 8.", sequence.Message);
    }

    [Fact]
    public void ACancelledSequenceStopsWhereItIs()
    {
        var sequence = Start([new(SequenceStepKind.Wait, Seconds: 60), new(SequenceStepKind.Stop)]);

        Assert.True(sequence.Cancel(s_now).IsSuccess);

        Assert.Equal(SequenceStatus.Cancelled, sequence.Status);
        Assert.Null(sequence.Current);
        Assert.Equal(ScheduleErrors.SequenceOver, sequence.Cancel(s_now).Error);
    }

    [Fact]
    public void ACancelStillPutsTheServerBackAsItWas()
    {
        // Cancelled while it updates: the update is not finished, the server is started again, and it ends cancelled.
        var sequence = Start([new(SequenceStepKind.Stop), new(SequenceStepKind.Update), new(SequenceStepKind.Start, OnlyIfWasRunning: true, Always: true)]);
        sequence.StepDone(s_now);
        sequence.Began(s_now, (1, s_now), OperationId.New());

        Assert.True(sequence.Cancel(s_now.AddSeconds(1)).IsSuccess);

        Assert.Equal(SequenceStepKind.Start, sequence.Current?.Kind);
        Assert.Null(sequence.OperationId);
        Assert.Equal(ScheduleErrors.SequenceOver, sequence.Cancel(s_now).Error); // once
        sequence.StepDone(s_now.AddSeconds(30));
        Assert.Equal(SequenceStatus.Cancelled, sequence.Status);
        Assert.Equal(ScheduleErrors.Cancelled.Code, sequence.ErrorCode);
    }

    [Fact]
    public void ACancelDuringAStepThatAlwaysRunsLetsItFinish()
    {
        var sequence = Start([new(SequenceStepKind.Update), new(SequenceStepKind.Start, Always: true)]);
        sequence.StepFailed("steamcmd.failed", null, s_now);

        Assert.True(sequence.Cancel(s_now).IsSuccess);

        Assert.Equal(SequenceStepKind.Start, sequence.Current?.Kind);
        Assert.Equal("steamcmd.failed", sequence.ErrorCode); // what went wrong first stays
    }

    [Theory]
    [InlineData(SequenceStepKind.Command, null, 0)]
    [InlineData(SequenceStepKind.Command, "say a\nquit", 0)]
    [InlineData(SequenceStepKind.Broadcast, "  ", 0)]
    [InlineData(SequenceStepKind.Wait, null, 0)]
    [InlineData(SequenceStepKind.Wait, null, 86_401)]
    [InlineData((SequenceStepKind)99, null, 0)]
    public void StepsMustBeComplete(SequenceStepKind kind, string? text, int seconds)
    {
        var started = ServerSequence.Start(ServerId.New(), SequenceOrigin.Schedule, "Nightly", [new(kind, text, seconds)], wasRunning: true, s_now);

        Assert.Equal(ScheduleErrors.StepsInvalid, started.Error);
    }

    [Fact]
    public void ASequenceHasAtMostItsLimitOfSteps()
    {
        var steps = Enumerable.Repeat(new SequenceStep(SequenceStepKind.Wait, Seconds: 1), ServerSequence.MaxSteps + 1).ToList();

        Assert.Equal(ScheduleErrors.StepsInvalid, ServerSequence.Start(ServerId.New(), SequenceOrigin.Schedule, "Nightly", steps, false, s_now).Error);
        Assert.Equal(ScheduleErrors.StepsInvalid, ServerSequence.Start(ServerId.New(), SequenceOrigin.Schedule, "Nightly", [], false, s_now).Error);
        Assert.Equal(ScheduleErrors.NameInvalid,
            ServerSequence.Start(ServerId.New(), SequenceOrigin.Schedule, " ", [new(SequenceStepKind.Stop)], false, s_now).Error);
    }

    private static ServerSequence Start(IReadOnlyList<SequenceStep> steps) =>
        ServerSequence.Start(ServerId.New(), SequenceOrigin.GameUpdate, "Game updates", steps, wasRunning: true, s_now).Value;
}
