using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The server state machine (docs/architecture/05) against a fake process and a fake clock.</summary>
public sealed class ServerSupervisorTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeProcessManager _processes = new();
    private readonly RecordingEvents _events = new();
    private ServerSupervisor? _supervisor;
    private ServerDefinition? _definition;

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
        }
    }

    [Fact]
    public async Task AServerStartsAndIsRunningOnceItsReadinessLineAppears()
    {
        var supervisor = Supervisor();

        Assert.Equal(AckStatus.Accepted, await Started(supervisor));
        await _events.WaitForAsync(RuntimeState.Starting, StateChangeReason.Requested);
        _processes.Last.Print("\u001b[32mloading...\u001b[m\r\nTest server rea");
        _processes.Last.Print("dy\r\n");

        var running = await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
        Assert.Equal(RuntimeState.Starting, running.Previous);
        Assert.Equal([1, 2], _events.Changes.Select(c => c.StateSequence));
        Assert.True(_events.DesiredRunning);
        Assert.Equal(RuntimeState.Running, supervisor.Snapshot.State);
        Assert.Equal(["--ready-after", "0"], _processes.Specs[0].Arguments);
    }

    [Fact]
    public async Task AStartIsAcknowledgedBeforeTheProcessExists()
    {
        var supervisor = Supervisor();
        _processes.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The panel waits 10 s for an ack; an account's first logon alone can take longer than that.
        Assert.Equal(AckStatus.Accepted, await supervisor.StartAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Empty(_processes.Started);
        await _events.WaitForAsync(RuntimeState.Starting);

        _processes.Gate.SetResult();
        await supervisor.FlushAsync();
        _processes.Last.Print("Test server ready\r\n");
        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
    }

    [Fact]
    public async Task AServerThatNeverReportsReadyIsConsideredRunningAfterTheTimeout()
    {
        var supervisor = Supervisor();
        await Started(supervisor);

        _clock.Advance(TimeSpan.FromSeconds(60));

        var running = await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.ReadinessTimedOut);
        Assert.Equal("readiness.timed_out", running.Detail);
    }

    [Fact]
    public async Task ProcessAliveReadinessWaitsForItsGracePeriod()
    {
        var supervisor = Supervisor(plan => plan with { Readiness = new ProcessAliveReadiness(5) });
        await Started(supervisor);
        _clock.Advance(TimeSpan.FromSeconds(4));
        await supervisor.FlushAsync();
        Assert.Equal(RuntimeState.Starting, supervisor.Snapshot.State);

        _clock.Advance(TimeSpan.FromSeconds(1));

        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
    }

    [Fact]
    public async Task StartingARunningServerOrStoppingAStoppedOneChangesNothing()
    {
        var supervisor = await Running();

        Assert.Equal(AckStatus.AlreadyInState, await Started(supervisor));
        Assert.Single(_processes.Started);

        Assert.Equal(AckStatus.Accepted, await supervisor.StopAsync());
        await _events.WaitForAsync(RuntimeState.Offline);
        Assert.Equal(AckStatus.AlreadyInState, await supervisor.StopAsync());
        Assert.Equal(AckStatus.AlreadyInState, await supervisor.KillAsync());
    }

    [Fact]
    public async Task AGracefulStopRunsTheManifestStepsAndEndsOffline()
    {
        var supervisor = await Running();

        await supervisor.StopAsync();

        var offline = await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.Requested);
        Assert.Equal(0, offline.ExitCode);
        Assert.Equal(["quit\r\n"], _processes.Last.Input);
        Assert.False(_events.DesiredRunning);
        await supervisor.WaitUntilSettledAsync();
        Assert.True(_processes.Last.Disposed);
    }

    [Fact]
    public async Task StopStepsWaitBetweenThemselves()
    {
        var supervisor = await Running(plan => plan with
        {
            Stop = new StopDefinition([new ConsoleCommandStop("save"), new WaitStop(2), new CtrlCStop(), new ConsoleCommandStop("quit")], 30),
        });
        var process = _processes.Last;

        await supervisor.StopAsync();
        await supervisor.FlushAsync();
        await LogTailTests.Eventually(() => process.Input.Count == 1, () => string.Join(", ", process.Input)); // written beside the loop
        Assert.Equal(["save\r\n"], process.Input);

        _clock.Advance(TimeSpan.FromSeconds(2));

        await _events.WaitForAsync(RuntimeState.Offline);
        Assert.Equal(["save\r\n", "quit\r\n"], process.Input);
        Assert.Equal(1, process.Interrupts);
    }

    [Fact]
    public async Task AServerThatIgnoresTheStopIsKilledWhenTheTimeoutExpires()
    {
        var supervisor = await Running();
        _processes.Last.IgnoreQuit = true;

        await supervisor.StopAsync();
        _clock.Advance(TimeSpan.FromSeconds(15));

        var offline = await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.StopTimedOut);
        Assert.Equal(1, _processes.Last.TerminatedWith);
        Assert.Equal(1, offline.ExitCode);
    }

    [Fact]
    public async Task KillTerminatesTheTreeAtOnce()
    {
        var supervisor = await Running();
        _processes.Last.IgnoreQuit = true;

        Assert.Equal(AckStatus.Accepted, await supervisor.KillAsync());

        await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.Killed);
        Assert.Equal(1, _processes.Last.TerminatedWith);
        Assert.False(_events.DesiredRunning);
    }

    [Fact]
    public async Task KillWinsOverAGracefulStopInProgress()
    {
        var supervisor = await Running();
        _processes.Last.IgnoreQuit = true;
        await supervisor.StopAsync();

        await supervisor.KillAsync();

        await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.Killed);
    }

    [Fact]
    public async Task RestartStopsAndStartsAgainAsOneUnit()
    {
        var supervisor = await Running();

        Assert.Equal(AckStatus.Accepted, await supervisor.RestartAsync());

        await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.Requested);
        var starting = await _events.WaitForAsync(RuntimeState.Starting, afterIndex: 2);
        // "Starting" is reported before the process is created: let the supervisor finish handling the restart.
        await supervisor.FlushAsync();
        Assert.Equal(2, _processes.Started.Count);
        Assert.Equal([RuntimeState.Starting, RuntimeState.Running, RuntimeState.Stopping, RuntimeState.Offline, RuntimeState.Starting],
            _events.Changes.Select(c => c.State));
        Assert.Equal(StateChangeReason.Requested, starting.Reason);
        Assert.True(_events.DesiredRunning);
    }

    [Fact]
    public async Task ACrashIsRestartedWithBackoffUntilItLoops()
    {
        var supervisor = await Running();
        TimeSpan[] backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

        foreach (var delay in backoff)
        {
            var before = _events.Changes.Count;
            _processes.Last.Exit(3);
            var crashed = await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.Crashed, before - 1);
            Assert.Equal(3, crashed.ExitCode);
            await supervisor.FlushAsync();

            _clock.Advance(delay - TimeSpan.FromSeconds(1));
            await supervisor.FlushAsync();
            Assert.Equal(RuntimeState.Crashed, supervisor.Snapshot.State);
            _clock.Advance(TimeSpan.FromSeconds(1));
            await _events.WaitForAsync(RuntimeState.Starting, StateChangeReason.AutoRestart, before);
            await supervisor.FlushAsync();
        }

        _processes.Last.Exit(3);

        await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.CrashLoop);
        Assert.False(_events.DesiredRunning);
        Assert.Equal(4, _processes.Started.Count);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await supervisor.FlushAsync();
        Assert.Equal(4, _processes.Started.Count);
    }

    [Fact]
    public async Task CrashesOutsideTheWindowDoNotCountTowardsALoop()
    {
        var supervisor = await Running();

        for (var i = 0; i < 5; i++)
        {
            var before = _events.Changes.Count;
            _processes.Last.Exit(3);
            await _events.WaitForAsync(RuntimeState.Crashed, afterIndex: before - 1);
            await supervisor.FlushAsync();
            _clock.Advance(TimeSpan.FromSeconds(5));
            await _events.WaitForAsync(RuntimeState.Starting, StateChangeReason.AutoRestart, before);
            await supervisor.FlushAsync();
            _clock.Advance(TimeSpan.FromMinutes(11));
        }

        Assert.DoesNotContain(_events.Changes, c => c.Reason == StateChangeReason.CrashLoop);
    }

    [Fact]
    public async Task ACrashAfterTheMemoryLimitRefusedMemorySaysSoAndIsRestarted()
    {
        var supervisor = await Running(limits: new ProcessLimits(null, 512L * 1024 * 1024));

        _processes.Last.ReachedMemoryLimit = true;
        _processes.Last.Exit(unchecked((int)0xC0000017));

        var crashed = await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.MemoryLimit);
        Assert.Equal("restart in 5 s", crashed.Detail);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _events.WaitForAsync(RuntimeState.Starting, StateChangeReason.AutoRestart);
        // Starting is said before the process is started: what follows it in the mailbox is done first.
        await supervisor.FlushAsync();
        Assert.Equal(2, _processes.Started.Count);
        Assert.Equal(new ProcessLimits(null, 512L * 1024 * 1024), _processes.Specs[1].Limits);
    }

    [Fact]
    public async Task LimitsReachTheLaunchAndChangesReachTheRunningGame()
    {
        var supervisor = await Running(limits: new ProcessLimits(50, 1024L * 1024 * 1024));
        Assert.Equal(new ProcessLimits(50, 1024L * 1024 * 1024), _processes.Specs[0].Limits);

        await supervisor.UpdateAsync(_definition! with { Limits = new ProcessLimits(20, null) });
        await supervisor.UpdateAsync(_definition! with { Limits = new ProcessLimits(20, null) });
        await supervisor.UpdateAsync(_definition! with { Limits = null });

        Assert.Equal([new ProcessLimits(20, null), null], _processes.Last.AppliedLimits);
        Assert.Equal(AckStatus.Accepted, await supervisor.RestartAsync());
        await _events.WaitForAsync(RuntimeState.Starting, StateChangeReason.Requested, afterIndex: 1);
        await supervisor.FlushAsync();
        Assert.Null(_processes.Specs[^1].Limits);
    }

    [Fact]
    public async Task ACrashedServerIsNotRestartedWhenItsPolicySaysSo()
    {
        var supervisor = await Running(restartOnCrash: false);

        _processes.Last.Exit(3);

        await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.Crashed);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await supervisor.FlushAsync();
        Assert.Single(_processes.Started);
    }

    [Fact]
    public async Task StoppingACrashedServerCancelsItsPendingRestart()
    {
        var supervisor = await Running();
        _processes.Last.Exit(3);
        await _events.WaitForAsync(RuntimeState.Crashed);

        Assert.Equal(AckStatus.AlreadyInState, await supervisor.StopAsync());
        _clock.Advance(TimeSpan.FromMinutes(1));
        await supervisor.FlushAsync();

        Assert.Single(_processes.Started);
        Assert.False(_events.DesiredRunning);
    }

    [Fact]
    public async Task ExitCodeZeroWithoutAStopRequestIsAStopTheGameAskedFor()
    {
        var supervisor = await Running();

        _processes.Last.Exit(0);

        await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.ExitedByItself);
        Assert.False(_events.DesiredRunning);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await supervisor.FlushAsync();
        Assert.Single(_processes.Started);
    }

    [Fact]
    public async Task ASteamLoginLeftInTheServersFolderIsDeletedBeforeTheGameStarts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pulsar-supervisor-tests", Guid.NewGuid().ToString("N"));
        var lent = Path.Combine(directory, ".pulsar", "steamcmd", "config", "config.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(lent)!);
        File.WriteAllText(lent, "a cached login");
        try
        {
            var supervisor = Supervisor(directory: directory);

            await Started(supervisor);

            Assert.Single(_processes.Started);
            Assert.False(File.Exists(lent));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AStartThatFailsLeavesTheServerCrashedWithTheReason()
    {
        var supervisor = Supervisor();
        _processes.FailNext = new GameProcessException("process.executable_missing", "missing");

        Assert.Equal(AckStatus.Accepted, await Started(supervisor));

        var crashed = await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.StartFailed);
        Assert.Equal("process.executable_missing", crashed.Detail);
        Assert.False(_events.DesiredRunning);
    }

    [Fact]
    public async Task AStartThatFailsUnexpectedlyStillLeavesTheServerCrashed()
    {
        // Anything else thrown while starting used to leave the server Starting with no process: stops never finished.
        var supervisor = Supervisor();
        _processes.FailNext = new InvalidOperationException("the window station is gone");

        Assert.Equal(AckStatus.Accepted, await Started(supervisor));

        var crashed = await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.StartFailed);
        Assert.Equal("process.start_failed", crashed.Detail);
        Assert.False(_events.DesiredRunning);
        Assert.Equal(AckStatus.AlreadyInState, await supervisor.StopAsync());
        Assert.Equal(AckStatus.Accepted, await Started(supervisor)); // and it can be started again
        _processes.Last.Print("Test server ready\r\n");
        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
    }

    [Fact]
    public async Task ChildrenThatOutliveTheMainProcessGetAGracePeriodThenDie()
    {
        var supervisor = await Running();
        var process = _processes.Last;
        process.Children = 2;

        await supervisor.StopAsync();
        await _events.WaitForAsync(RuntimeState.Offline);
        await supervisor.FlushAsync();
        Assert.False(process.Disposed);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await supervisor.WaitUntilSettledAsync();

        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task StartingAgainKillsWhatThePreviousRunLeftBehind()
    {
        var supervisor = await Running();
        var first = _processes.Last;
        first.Children = 1;
        await supervisor.StopAsync();
        await _events.WaitForAsync(RuntimeState.Offline);

        await Started(supervisor);

        Assert.True(first.Disposed);
        Assert.Equal(2, _processes.Started.Count);
    }

    [Fact]
    public async Task ATimerFromAPreviousRunDoesNotAffectTheNextOne()
    {
        var supervisor = Supervisor();
        await Started(supervisor);
        _processes.Last.Exit(0);
        await _events.WaitForAsync(RuntimeState.Offline);

        await Started(supervisor);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await supervisor.FlushAsync();
        _clock.Advance(TimeSpan.FromSeconds(29));
        await supervisor.FlushAsync();

        // The first run's 60 s readiness timeout has expired, the second run's has not.
        Assert.Equal(RuntimeState.Starting, supervisor.Snapshot.State);
    }

    [Fact]
    public async Task AServerStoppedBecauseTheAgentStopsSaysSo()
    {
        var supervisor = await Running();

        Assert.True(await supervisor.ShutdownAsync(TimeSpan.FromSeconds(3)));

        var offline = await _events.WaitForAsync(RuntimeState.Offline);
        Assert.Equal(StateChangeReason.AgentStopping, offline.Reason);
    }

    [Fact]
    public async Task ShuttingDownTheAgentStopsTheServerWithinTheBudget()
    {
        var supervisor = await Running();
        _processes.Last.IgnoreQuit = true;

        var shutdown = supervisor.ShutdownAsync(TimeSpan.FromSeconds(3));
        await _events.WaitForAsync(RuntimeState.Stopping, StateChangeReason.AgentStopping);
        await supervisor.FlushAsync();
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.True(await shutdown.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(RuntimeState.Offline, supervisor.Snapshot.State);
        Assert.Equal(StateChangeReason.StopTimedOut, _events.Changes[^1].Reason);
        Assert.True(_events.DesiredRunning, "An agent stop does not change what the user wants.");
    }

    /// <summary>Starts and waits until the supervisor has handled it (the ack comes before the process exists).</summary>
    [Fact]
    public async Task TheGamesQueryAnswerGoesWithItsMetricsWhileItIsRecent()
    {
        var query = new FakeQuery { Answer = () => Answer("Pulsar") };
        var supervisor = await Running(plan => plan with { Query = new QueryEndpoint(QueryProtocol.A2s, "127.0.0.1", 27015) }, query: query);

        var sample = await supervisor.SampleAsync();

        Assert.Equal("Pulsar", sample?.Query?.Name);
        Assert.Equal(new QueryEndpoint(QueryProtocol.A2s, "127.0.0.1", 27015), query.Endpoints[0]);
        query.Answer = () => throw new QueryException("No answer.");
        _clock.Advance(TimeSpan.FromSeconds(31)); // three intervals without an answer
        Assert.Null((await supervisor.SampleAsync())?.Query);
    }

    [Fact]
    public async Task QueryReadinessWaitsForTheGamesFirstAnswer()
    {
        var query = new FakeQuery { Answer = () => throw new QueryException("Not listening yet.") };
        var supervisor = Supervisor(plan => plan with { Readiness = new QueryReadiness(60), Query = new QueryEndpoint(QueryProtocol.A2s, "127.0.0.1", 27015) },
            query: query);
        await Started(supervisor);
        await LogTailTests.Eventually(() => query.Endpoints.Count > 0, () => "never queried");
        Assert.Equal(RuntimeState.Starting, supervisor.Snapshot.State);

        query.Answer = () => Answer("Pulsar");
        while (supervisor.Snapshot.State == RuntimeState.Starting)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
    }

    [Fact]
    public async Task AnUnexpectedQueryErrorDoesNotEndTheQueries()
    {
        // A bug reading an odd answer must not leave a query-readiness server Starting until its timeout.
        var query = new FakeQuery { Answer = () => throw new FormatException("An odd answer.") };
        var supervisor = Supervisor(plan => plan with { Readiness = new QueryReadiness(60), Query = new QueryEndpoint(QueryProtocol.A2s, "127.0.0.1", 27015) },
            query: query);
        await Started(supervisor);
        await LogTailTests.Eventually(() => query.Endpoints.Count > 0, () => "never queried");

        query.Answer = () => Answer("Pulsar");
        for (var i = 0; i < 20 && supervisor.Snapshot.State == RuntimeState.Starting; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
    }

    [Fact]
    public async Task AGameTakenOverWhileItWasStoppingEndsThatStopWhenItExits()
    {
        // The previous run of the agent died in the middle of a stop.
        var supervisor = Supervisor();
        var game = new FakeGameProcess(5000);
        var host = new GameHostReference("pulsar-host-test", 4000, DateTimeOffset.UtcNow);

        Assert.True(await supervisor.AttachAsync(new FakeHostedProcess(game, host), new GameHostState(host, 0, 0, Ready: false, Stopping: true),
            desiredRunning: false));
        Assert.Equal(RuntimeState.Stopping, supervisor.Snapshot.State);
        Assert.True(supervisor.HostState!.Stopping);

        game.Exit(0);

        var offline = await _events.WaitForAsync(RuntimeState.Offline);
        Assert.Equal(StateChangeReason.Requested, offline.Reason); // the stop's end, not the game quitting by itself
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public async Task AStopTakenOverTimesOutAgainAndARestartStartsTheGameAfterIt()
    {
        var supervisor = Supervisor();
        var game = new FakeGameProcess(5000) { IgnoreQuit = true };
        var host = new GameHostReference("pulsar-host-test", 4000, DateTimeOffset.UtcNow);
        await supervisor.AttachAsync(new FakeHostedProcess(game, host), new GameHostState(host, 0, 0, Ready: true, Stopping: true),
            desiredRunning: true); // a restart: it should run again
        await supervisor.FlushAsync();

        _clock.Advance(TimeSpan.FromSeconds(15)); // the manifest's stop timeout

        await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.StopTimedOut);
        Assert.Equal(1, game.TerminatedWith);
        await _events.WaitForAsync(RuntimeState.Starting);
        Assert.Single(_processes.Started);
    }

    [Fact]
    public async Task AGameThatStopsReadingItsInputDoesNotHoldUpAKill()
    {
        // A frozen game: the write of a console command never ends. A kill must not wait behind it.
        var supervisor = await Running();
        var process = _processes.Last;
        process.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var input = supervisor.SendInputAsync("say hi", TestContext.Current.CancellationToken);
        await LogTailTests.Eventually(() => process.BlockedWrites == 1, () => "the input was never written");

        Assert.Equal(AckStatus.Accepted, await supervisor.KillAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.Killed);
        Assert.Equal(AckStatus.Rejected, await input.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnInputTheGameDoesNotTakeInTimeIsRefused()
    {
        var supervisor = await Running();
        var process = _processes.Last;
        process.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = supervisor.SendInputAsync("say hi", TestContext.Current.CancellationToken);
        await LogTailTests.Eventually(() => process.BlockedWrites == 1, () => "the input was never written");

        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(AckStatus.Busy, await input.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(RuntimeState.Running, supervisor.Snapshot.State);
        Assert.Empty(process.Input);
    }

    [Fact]
    public async Task WaitingForAConsoleCommandCanBeGivenUp()
    {
        // A backup asking a frozen game to save is cancelled: it must not keep waiting for the command.
        var supervisor = await Running();
        _processes.Last.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var input = supervisor.SendInputAsync("save-all", cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => input.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExclusiveOperationKeepsTheServerFromStartingAndAStartKeepsItFromBeginning()
    {
        // The operation's check and the start used to race: a start queued in the mailbox ran while a restore wrote files.
        var supervisor = Supervisor();
        Assert.True(supervisor.TryBeginExclusive(requireStopped: true));

        Assert.Equal(AckStatus.Busy, await Started(supervisor));
        Assert.Equal(AckStatus.Busy, await supervisor.RestartAsync());
        Assert.Empty(_processes.Started);
        Assert.Equal(RuntimeState.Offline, supervisor.Snapshot.State);
        Assert.Null(_events.DesiredRunning);

        supervisor.EndExclusive();
        _processes.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(AckStatus.Accepted, await supervisor.StartAsync()); // answered once it is starting, before the process exists

        Assert.False(supervisor.TryBeginExclusive(requireStopped: true));
        Assert.True(supervisor.TryBeginExclusive(requireStopped: false)); // a delete kills it itself
        supervisor.EndExclusive();
        _processes.Gate.SetResult();
    }

    [Fact]
    public async Task ACrashRestartWaitsForNoExclusiveOperation()
    {
        var supervisor = await Running();
        _processes.Last.Exit(3);
        await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.Crashed);
        await supervisor.FlushAsync();
        Assert.True(supervisor.TryBeginExclusive(requireStopped: true)); // an update of the crashed server

        _clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await supervisor.FlushAsync();

        Assert.Single(_processes.Started);
        Assert.Equal(RuntimeState.Crashed, supervisor.Snapshot.State);

        // Once the update ends, the restart it held back runs.
        supervisor.EndExclusive();
        await _events.WaitForAsync(RuntimeState.Starting, StateChangeReason.AutoRestart);
        await supervisor.FlushAsync();
        Assert.Equal(2, _processes.Started.Count);
    }

    [Fact]
    public async Task ACrashRestartHeldBackByAnOperationIsForgottenWhenTheServerIsStopped()
    {
        var supervisor = await Running();
        _processes.Last.Exit(3);
        await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.Crashed);
        await supervisor.FlushAsync();
        Assert.True(supervisor.TryBeginExclusive(requireStopped: true));
        _clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await supervisor.FlushAsync();

        Assert.Equal(AckStatus.AlreadyInState, await supervisor.StopAsync());
        supervisor.EndExclusive();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await supervisor.FlushAsync();

        Assert.Single(_processes.Started);
        Assert.Equal(RuntimeState.Crashed, supervisor.Snapshot.State);
    }

    [Fact]
    public async Task AStartRefusedForAnExclusiveOperationLeavesTheAccountsProcessesAlone()
    {
        // An update of a crashed server runs SteamCMD as the server's account: neither the crash restart it refuses, nor
        // the end of the crash handler's grace, may kill what that account runs.
        var supervisor = await Running();
        var crashed = _processes.Last;
        crashed.Children = 1; // a crash handler that outlives the game
        crashed.Exit(3);
        await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.Crashed);
        await supervisor.FlushAsync();
        var sweeps = _processes.StraySweeps;
        Assert.True(supervisor.TryBeginExclusive(requireStopped: true));

        _clock.Advance(TimeSpan.FromSeconds(5)); // the crash handler's grace and the crash restart
        await LogTailTests.Eventually(() => crashed.Disposed, () => "the crash handler's grace never ended");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await supervisor.FlushAsync();
        Assert.Equal(AckStatus.Busy, await Started(supervisor));
        Assert.Equal(AckStatus.Busy, await supervisor.RestartAsync());

        Assert.Equal(sweeps, _processes.StraySweeps);
        Assert.Single(_processes.Started);
    }

    [Fact]
    public async Task ACommandWaitingBehindOneTheGameDoesNotTakeIsRefusedWithinItsOwnTime()
    {
        // The panel waits 10 s for each command's ack: one queued behind a stuck one must not wait 5 s more once its turn comes.
        var supervisor = await Running();
        var process = _processes.Last;
        var gate = process.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var first = supervisor.SendInputAsync("say 1", TestContext.Current.CancellationToken);
            await LogTailTests.Eventually(() => process.BlockedWrites == 1, () => "the input was never written");
            var second = supervisor.SendInputAsync("say 2", TestContext.Current.CancellationToken);
            await supervisor.FlushAsync();

            _clock.Advance(TimeSpan.FromSeconds(5));

            Assert.Equal(AckStatus.Busy, await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(AckStatus.Busy, await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            gate.SetResult();
            await supervisor.FlushAsync();
            Assert.Equal(1, process.BlockedWrites); // the second, refused, never reached the game
            Assert.Empty(process.Input);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task CommandsPilingUpBehindOneTheGameDoesNotTakeAreRefusedAtOnce()
    {
        var supervisor = await Running();
        var process = _processes.Last;
        var gate = process.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var first = supervisor.SendInputAsync("say 0", TestContext.Current.CancellationToken);
            await LogTailTests.Eventually(() => process.BlockedWrites == 1, () => "the input was never written");
            var queued = Enumerable.Range(1, 16).Select(i => supervisor.SendInputAsync($"say {i}", TestContext.Current.CancellationToken)).ToList();

            // 16 wait behind the one being written; the next is refused without waiting.
            Assert.Equal(AckStatus.Busy, await supervisor.SendInputAsync("say 17", TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.DoesNotContain(queued, q => q.IsCompleted);

            gate.SetResult();
            Assert.All(await Task.WhenAll(queued.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                status => Assert.Equal(AckStatus.Accepted, status));
            Assert.Equal(Enumerable.Range(0, 17).Select(i => $"say {i}\r\n"), process.Input);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task AStopsCommandsGoAfterTheCommandsTypedBeforeIt()
    {
        var supervisor = await Running();
        var process = _processes.Last;
        var gate = process.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var input = supervisor.SendInputAsync("say bye", TestContext.Current.CancellationToken);
            await LogTailTests.Eventually(() => process.BlockedWrites == 1, () => "the input was never written");
            Assert.Equal(AckStatus.Accepted, await supervisor.StopAsync());
            await supervisor.FlushAsync();

            gate.SetResult();

            Assert.Equal(AckStatus.Accepted, await input.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.Requested);
            Assert.Equal(["say bye\r\n", "quit\r\n"], process.Input);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task TheAgentsStopDoesNotWaitForAWriteTheGameDoesNotTake()
    {
        var supervisor = await Running();
        var process = _processes.Last;
        process.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = supervisor.SendInputAsync("say hi", TestContext.Current.CancellationToken);
        await LogTailTests.Eventually(() => process.BlockedWrites == 1, () => "the input was never written");

        var shutdown = supervisor.ShutdownAsync(TimeSpan.FromSeconds(3));
        await _events.WaitForAsync(RuntimeState.Stopping, StateChangeReason.AgentStopping);
        await supervisor.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.True(await shutdown.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, process.TerminatedWith); // the stop's command never reached it: its time ran out
    }

    [Fact]
    public async Task AServerTheUserWasStoppingIsNotStartedAgainWithTheAgent()
    {
        // The user pressed Stop and the agent stopped (an update) while the game was still saving.
        var supervisor = await Running();
        _processes.Last.IgnoreQuit = true;
        await supervisor.StopAsync();
        await supervisor.FlushAsync();

        var shutdown = supervisor.ShutdownAsync(TimeSpan.FromSeconds(3));
        await supervisor.FlushAsync();
        _clock.Advance(TimeSpan.FromSeconds(15));

        Assert.False(await shutdown.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AServerBeingRestartedWhenTheAgentStopsIsStartedAgainWithIt()
    {
        var supervisor = await Running();
        _processes.Last.IgnoreQuit = true;
        await supervisor.RestartAsync();
        await supervisor.FlushAsync();

        var shutdown = supervisor.ShutdownAsync(TimeSpan.FromSeconds(3));
        await supervisor.FlushAsync();
        _clock.Advance(TimeSpan.FromSeconds(15));

        Assert.True(await shutdown.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Single(_processes.Started); // the agent starts it, not this stop
    }

    [Fact]
    public async Task AGameThatExitsWhileItsDesiredStateCannotBeSavedStillEndsOffline()
    {
        // A full disk: the server must not stay Running with no process (a start would then answer "already running").
        var supervisor = await Running();
        _events.DesiredError = new IOException("There is not enough space on the disk.");

        _processes.Last.Exit(0);

        await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.ExitedByItself);
        Assert.Equal(AckStatus.Accepted, await Started(supervisor));
        Assert.Equal(2, _processes.Started.Count);
    }

    [Fact]
    public async Task AGameThatDoesNotAnswerForItsUsageDoesNotHoldUpTheServer()
    {
        // A host that does not answer: one sample at a time, and a kill does not wait for it.
        var supervisor = await Running();
        var gate = _processes.Last.SampleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var first = supervisor.SampleAsync();
            Assert.Null(await supervisor.SampleAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(AckStatus.Accepted, await supervisor.KillAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.Killed);

            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task AStartThatNeverReturnsFailsAfterItsTime()
    {
        // Process creation that hangs (a profile that never loads): stop and kill must not wait forever behind it.
        var supervisor = Supervisor();
        var gate = _processes.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _processes.GateIgnoresCancellation = true;
        try
        {
            await supervisor.StartAsync();
            await _events.WaitForAsync(RuntimeState.Starting);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (supervisor.Snapshot.State == RuntimeState.Starting && DateTime.UtcNow < deadline)
            {
                _clock.Advance(TimeSpan.FromSeconds(10));
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            var failed = await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.StartFailed);
            Assert.Equal("process.start_failed", failed.Detail);
            Assert.Equal(AckStatus.AlreadyInState, await supervisor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

            // The process it started after all is ended at once.
            gate.SetResult();
            await LogTailTests.Eventually(() => _processes.Started.Count == 1 && _processes.Last.Disposed, () => "the late process was not ended");
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheAgentStoppingDoesNotWaitForAStartStillCreatingItsProcess(bool detachFirst)
    {
        // The service control manager gives the agent ~30 s; creating the process may take up to its start timeout.
        var supervisor = Supervisor();
        var gate = _processes.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _processes.GateIgnoresCancellation = true;
        try
        {
            await supervisor.StartAsync();
            await _events.WaitForAsync(RuntimeState.Starting);

            if (detachFirst)
            {
                Assert.False(await supervisor.DetachAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }

            // Wanted running: it starts again with the agent.
            Assert.True(await supervisor.ShutdownAsync(TimeSpan.FromSeconds(15)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(RuntimeState.Offline, supervisor.Snapshot.State);
            Assert.True(_events.DesiredRunning);

            // The process it created after all is ended at once.
            gate.SetResult();
            await LogTailTests.Eventually(() => _processes.Started.Count == 1 && _processes.Last.Disposed, () => "the late process was not ended");
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    private ServerQueryInfo Answer(string name) => new(_clock.GetUtcNow(), name, "de_dust2", 1, 16, 0, "1.0", [new QueryPlayer("Ana", 3, null)]);

    private sealed class FakeQuery : IServerQuery
    {
        public List<QueryEndpoint> Endpoints { get; } = [];

        public Func<ServerQueryInfo> Answer { get; set; } = () => throw new QueryException("No answer.");

        public Task<ServerQueryInfo> QueryAsync(QueryEndpoint endpoint, CancellationToken cancellationToken)
        {
            lock (Endpoints)
            {
                Endpoints.Add(endpoint);
            }

            return Task.FromResult(Answer());
        }
    }

    private static async Task<AckStatus> Started(ServerSupervisor supervisor)
    {
        var ack = await supervisor.StartAsync();
        await supervisor.FlushAsync();
        return ack;
    }

    private async Task<ServerSupervisor> Running(Func<LaunchPlan, LaunchPlan>? change = null, bool restartOnCrash = true, IServerQuery? query = null,
        ProcessLimits? limits = null)
    {
        var supervisor = Supervisor(change, restartOnCrash, query, limits);
        await Started(supervisor);
        _processes.Last.Print("Test server ready\r\n");
        await _events.WaitForAsync(RuntimeState.Running);
        return supervisor;
    }

    private ServerSupervisor Supervisor(Func<LaunchPlan, LaunchPlan>? change = null, bool restartOnCrash = true, IServerQuery? query = null,
        ProcessLimits? limits = null, string directory = @"C:\servers\abcd1234")
    {
        var plan = new LaunchPlan("bin/game.exe", "", ["--ready-after", "0"], new Dictionary<string, string>(),
            new ConsoleDefinition(ManifestConsoleMode.Pty, InputNewline: "\r\n"),
            new LogPatternReadiness("^Test server ready", 60),
            new StopDefinition([new ConsoleCommandStop("quit")], 15));
        var definition = new ServerDefinition(Guid.NewGuid(), "abcd1234", directory, ProcessIdentity.CurrentUser,
            change?.Invoke(plan) ?? plan, AutoStart: false, RestartOnCrash: restartOnCrash, limits);
        _definition = definition;
        _supervisor = new ServerSupervisor(definition, _processes, _events, _clock, new SupervisorOptions(), NullLogger.Instance, query: query);
        return _supervisor;
    }
}
