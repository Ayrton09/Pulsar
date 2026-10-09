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
