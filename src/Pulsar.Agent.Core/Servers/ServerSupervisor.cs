using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>Everything the agent needs to run one server, rebuilt from its stored spec.</summary>
public sealed record ServerDefinition(
    Guid ServerId,
    string ShortId,
    string Directory,
    ProcessIdentity Identity,
    LaunchPlan Plan,
    bool AutoStart,
    bool RestartOnCrash,
    ProcessLimits? Limits = null);

/// <summary>Where a supervisor reports what happens to its server.</summary>
public interface IServerEventSink
{
    void StateChanged(ServerStateChanged change);

    /// <summary>The server should (not) be running: persisted by the agent and mirrored by the panel.</summary>
    void DesiredChanged(Guid serverId, bool running);

    /// <summary>
    /// The server's game started or stopped running in a host process, or became ready there: the agent keeps
    /// <see cref="ServerSupervisor.HostState"/> so its next run finds the game again.
    /// </summary>
    void HostChanged(Guid serverId)
    {
    }
}

/// <summary>A game running in a host process, and how far the agent read its console (docs/architecture/06, "Server hosts").</summary>
/// <param name="ConsoleOffset">The host's output offset after the last text written to the console.</param>
/// <param name="LogPosition">The position in the game's log file read so far (<c>logfile+rcon</c>).</param>
/// <param name="Ready">The game passed its readiness check.</param>
/// <param name="Stopping">The game was being stopped.</param>
public sealed record GameHostState(GameHostReference Host, long ConsoleOffset, long LogPosition, bool Ready, bool Stopping = false);

public sealed class SupervisorOptions
{
    /// <summary>Waits before each automatic restart after a crash (docs/architecture/05: 5 s, 15 s, 60 s).</summary>
    public IReadOnlyList<TimeSpan> CrashBackoff { get; set; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    /// <summary>At most this many automatic restarts within <see cref="CrashWindow"/>; then the server stays crashed.</summary>
    public int MaxRestarts { get; set; } = 3;

    public TimeSpan CrashWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long children may outlive the main process before the tree is killed.</summary>
    public TimeSpan TreeGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a game's log file is checked for new text (<c>logfile+rcon</c>).</summary>
    public TimeSpan LogPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Wait between attempts to reach a starting game over RCON (<c>rcon</c> readiness).</summary>
    public TimeSpan RconRetryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long an RCON answer waits to show up in the log of a game that logs RCON commands (CS2) before the console
    /// shows it itself: such a game logs some answers (<c>status</c>) but not others (cvar queries).
    /// </summary>
    public TimeSpan LoggedAnswerWait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a running game with a query is asked for its name, map and players.</summary>
    public TimeSpan QueryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a console command waits for the game to take it (a frozen game stops reading its input); then it is
    /// refused. Within the panel's wait for the command's ack.
    /// </summary>
    public TimeSpan InputTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long creating the game's process may take (an account's first logon creates its profile); then the start
    /// fails, and a process that shows up later is ended.
    /// </summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a usage sample may take (a game in a host process is asked over its pipe); then it is skipped.</summary>
    public TimeSpan SampleTimeout { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Runs one server (docs/architecture/05): a single-reader mailbox processes commands and process events one at a
/// time, so nothing races ("stop arrives while start is creating the process"). Nothing in the loop waits for long:
/// readiness, stop steps, timeouts and crash backoff are timers that post messages back, and what the game itself must
/// answer (console input, a usage sample, a kill of a game in its host) runs beside the loop with a deadline, so a kill
/// is always handled right away. Only creating the process is awaited in the loop, within
/// <see cref="SupervisorOptions.StartTimeout"/> or until the agent stops. Messages from an earlier process (a late timer,
/// an old exit) carry its generation and are ignored.
/// </summary>
public sealed partial class ServerSupervisor : IAsyncDisposable
{
    // Console commands waiting for a game that is slow to take them; more are refused at once.
    private const int MaxQueuedWrites = 16;

    private readonly IGameProcessManager _processes;
    private readonly IServerFiles _files;
    private readonly IRconConnector _rcon;
    private readonly IServerQuery _query;
    private readonly IServerFirewall _firewall;
    private readonly IServerEventSink _events;
    private readonly ServerConsole _console;
    private readonly RecentLog _recentLog = new();
    private readonly TimeProvider _clock;
    private readonly SupervisorOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _rconLock = new(1, 1); // console commands share one RCON session, one at a time
    private readonly Channel<Message> _mailbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _agentStopping = new(); // a process still being created is not waited for
    private readonly Queue<DateTimeOffset> _restarts = new();
    private readonly List<TaskCompletionSource> _settledWaiters = [];
    private readonly Lock _gate = new(); // a start and an exclusive operation: whichever comes first keeps the other out
    private readonly Task _loop;

    private ServerDefinition _definition;
    private RuntimeState _state = RuntimeState.Offline;
    private IGameProcess? _process;
    private IGameProcess? _lingering;
    private LaunchPlan? _plan; // what the running process was started with
    private CancellationTokenSource? _watchers; // log tail, RCON probe and console input of the running process
    private Channel<ConsoleWrite>? _writes; // console input of the running process, written in order beside the loop
    private bool _exclusive; // guarded by _gate: an operation that must not see the game start is running
    private int _sampling; // 1 while a usage sample is being taken
    private int _generation;
    private long _sequence;
    private int? _exitCode;
    private bool _desiredRunning;
    private bool _restartAfterStop;
    private bool _startInterrupted; // the agent stopped while the game's process was being created
    private StateChangeReason? _stopReason;
    private int _restartTimer;
    private int? _deferredRestart; // the timer of a crash restart an exclusive operation refused: run when it ends
    private volatile ServerSnapshot _snapshot;
    private volatile ServerStateChanged? _lastChange;
    private (int Generation, IRconSession Session)? _consoleRcon; // guarded by _rconLock
    private (int Generation, ProcessUsage Usage, DateTimeOffset At)? _lastUsage;
    private volatile QueryAnswer? _lastQuery;
    private volatile IHostedGameProcess? _hosted; // the game's host, until the process is released
    private long _consoleEnd; // host output offset written to the console so far
    private long _logPosition; // game log file position read so far
    private HashSet<string>? _logsBefore; // a log named by a pattern: the files it matched before this start (null: attached to a running game)

    // A log named by a pattern is looked for in a folder the server's account fills as it likes: one with more entries is not
    // listed, and after the first seconds it is looked for every 2 s.
    private const int MaximumLogFolderEntries = 10_000;
    private static readonly TimeSpan s_maximumLogSearchInterval = TimeSpan.FromSeconds(2);
    private Task _pumping = Task.CompletedTask;
    private Task _tailing = Task.CompletedTask;

    public ServerSupervisor(ServerDefinition definition, IGameProcessManager processes, IServerEventSink events, TimeProvider clock,
        SupervisorOptions options, ILogger logger, IServerFiles? files = null, IRconConnector? rcon = null, IServerFirewall? firewall = null,
        ServerConsole? console = null, IServerQuery? query = null)
    {
        _definition = definition;
        _processes = processes;
        _files = files ?? new DirectServerFiles();
        _rcon = rcon ?? new RconConnectors();
        _query = query ?? new ServerQuery(clock);
        _firewall = firewall ?? new UnmanagedFirewall();
        _console = console ?? new ServerConsole(new ConsoleBuffer(clock));
        _events = events;
        _clock = clock;
        _options = options;
        _logger = logger;
        _snapshot = new ServerSnapshot(definition.ServerId, RuntimeState.Offline, 0, null, null);
        _loop = Task.Run(RunAsync);
    }

    public Guid ServerId => _definition.ServerId;

    /// <summary>The latest state, readable from any thread (for the Hello).</summary>
    public ServerSnapshot Snapshot => _snapshot;

    /// <summary>The last change reported, readable from any thread (delivered again when the agent stops).</summary>
    public ServerStateChanged? LastChange => _lastChange;

    /// <summary>The server's console output, across restarts of its game.</summary>
    public ServerConsole Console => _console;

    /// <summary>The host process of the running game and how far its console was read; null when none runs in a host.</summary>
    public GameHostState? HostState => _hosted is { } hosted
        ? new GameHostState(hosted.Host, Interlocked.Read(ref _consoleEnd), Interlocked.Read(ref _logPosition), _snapshot.State == RuntimeState.Running,
            _snapshot.State == RuntimeState.Stopping)
        : null;

    /// <summary>
    /// Takes over a game a previous run of the agent left running in its host (docs/architecture/06, "Server hosts"): the
    /// server is running (or still starting) again without a start, its console continues where that run stopped
    /// reading, and an exit that happened meanwhile is handled now. False when the server already runs something.
    /// </summary>
    public async Task<bool> AttachAsync(IGameProcess process, GameHostState state, bool desiredRunning)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Post(new Attach(done, process, state, desiredRunning));
        return await done.Task;
    }

    /// <summary>
    /// The agent is stopping but its servers keep running: lets go of a game that runs in a host, which keeps it. False
    /// when there is none to leave (not running, stopping, or started without a host): such a server is shut down.
    /// </summary>
    public async Task<bool> DetachAsync()
    {
        await _agentStopping.CancelAsync(); // see ShutdownAsync
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Post(new Detach(done));
        return await done.Task;
    }

    /// <summary>
    /// Sends a command line to the running game: to its console input, or over RCON for <c>logfile+rcon</c> games (in
    /// the background: the answer, or why there is none, shows up in the console). Rejected when it is not running;
    /// <see cref="AckStatus.Busy"/> when the game does not take it within <see cref="SupervisorOptions.InputTimeout"/>
    /// (it stopped reading its input). Cancelling stops waiting for the answer, not the command.
    /// </summary>
    public async Task<AckStatus> SendInputAsync(string line, CancellationToken cancellationToken = default)
    {
        var answer = new TaskCompletionSource<AckStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Post(new Input(answer, line));
        return await answer.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Usage of the running game since the previous sample; null when it is not running, when the game does not answer
    /// within <see cref="SupervisorOptions.SampleTimeout"/>, or while the previous sample is still being taken.
    /// </summary>
    public async Task<ServerMetrics?> SampleAsync()
    {
        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
        {
            return null; // a game slow to answer must not pile samples up
        }

        // Released here, before the result is returned: released by a continuation of the sample (which runs
        // asynchronously), a sample asked for right after this one could find it still taken.
        try
        {
            var sample = new TaskCompletionSource<ServerMetrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Post(new Sample(sample));
            return await sample.Task;
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    /// <summary>
    /// An operation that must not see the game start (an install, a restore, a delete) begins: from now on a start is
    /// refused (<see cref="AckStatus.Busy"/>), automatic ones included (a crash restart then runs at <see cref="EndExclusive"/>),
    /// and the account's stray processes are left alone, until <see cref="EndExclusive"/>. False when a
    /// start came first: the server is starting or running (only when <paramref name="requireStopped"/>).
    /// </summary>
    public bool TryBeginExclusive(bool requireStopped)
    {
        lock (_gate)
        {
            if (_exclusive || (requireStopped && _state is not (RuntimeState.Offline or RuntimeState.Crashed)))
            {
                return false;
            }

            _exclusive = true;
            return true;
        }
    }

    /// <summary>The operation ended: starts are accepted again, and a crash restart it refused runs now.</summary>
    public void EndExclusive()
    {
        lock (_gate)
        {
            _exclusive = false;
        }

        _mailbox.Writer.TryWrite(new ExclusiveEnded());
    }

    public Task<AckStatus> StartAsync(StateChangeReason reason = StateChangeReason.Requested) => Ask(tcs => new Start(tcs, reason));

    public Task<AckStatus> StopAsync() => Ask(tcs => new Stop(tcs));

    public Task<AckStatus> RestartAsync() => Ask(tcs => new Restart(tcs));

    public Task<AckStatus> KillAsync() => Ask(tcs => new Kill(tcs));

    /// <summary>Uses a new definition for the next start (a running process keeps the one it started with).</summary>
    public Task UpdateAsync(ServerDefinition definition) => Ask(tcs => new Update(tcs, definition));

    /// <summary>
    /// The agent is stopping: a graceful stop within <paramref name="budget"/>, then the tree is killed. Completes when the
    /// server is no longer running. Returns whether it was running (so the agent can start it again next time). A start
    /// still creating its process is not waited for (that can take up to <see cref="SupervisorOptions.StartTimeout"/>):
    /// it ends as a late start does, and counts as running.
    /// </summary>
    public async Task<bool> ShutdownAsync(TimeSpan budget)
    {
        await _agentStopping.CancelAsync();
        var wasRunning = await Ask(tcs => new Shutdown(tcs, budget)) == AckStatus.Accepted;
        await WaitUntilSettledAsync();
        return wasRunning;
    }

    /// <summary>Completes once the server is offline or crashed with no process left (tests, shutdown).</summary>
    public async Task WaitUntilSettledAsync()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Post(new WhenSettled(settled));
        await settled.Task;
    }

    /// <summary>Completes when every message posted before it has been handled (tests: timers are registered).</summary>
    internal Task FlushAsync() => Ask(tcs => new Flush(tcs));

    public async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryComplete();
        await _stopping.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }

        while (_mailbox.Reader.TryRead(out var pending))
        {
            (pending as Command)?.Fail(new ObjectDisposedException(nameof(ServerSupervisor)));
            (pending as WhenSettled)?.Done.TrySetResult();
        }

        _settledWaiters.ForEach(w => w.TrySetResult());
        _agentStopping.Dispose();
        StopWatchers();
        // A console command still running was cancelled above; its session goes once it let go of it.
        if (await _rconLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await CloseConsoleRconAsync();
            _rconLock.Release();
        }
        foreach (var process in new[] { _process, _lingering })
        {
            if (process is not null)
            {
                await process.DisposeAsync();
                Released(process);
            }
        }

        _stopping.Dispose();
    }

    private async Task<AckStatus> Ask(Func<TaskCompletionSource<AckStatus>, Message> create)
    {
        var tcs = new TaskCompletionSource<AckStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Post(create(tcs));
        return await tcs.Task;
    }

    private async Task Ask(Func<TaskCompletionSource, Message> create)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Post(create(tcs));
        await tcs.Task;
    }

    private ValueTask Post(Message message)
    {
        ObjectDisposedException.ThrowIf(!_mailbox.Writer.TryWrite(message), this);
        return ValueTask.CompletedTask;
    }

    private void PostLater(TimeSpan delay, Message message) => _ = PostLaterAsync(delay, message);

    private async Task PostLaterAsync(TimeSpan delay, Message message)
    {
        try
        {
            await Task.Delay(delay, _clock, _stopping.Token);
            _mailbox.Writer.TryWrite(message);
        }
        catch (OperationCanceledException)
        {
            // The supervisor is gone.
        }
    }

    private async Task RunAsync()
    {
        await foreach (var message in _mailbox.Reader.ReadAllAsync(_stopping.Token))
        {
            try
            {
                await HandleAsync(message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bug must not take the whole agent down; the server keeps its last known state.
                LogUnexpected(ex, ServerId, message.GetType().Name);
                (message as Command)?.Fail(ex);
            }

            NotifySettled();
        }
    }

    private async Task HandleAsync(Message message)
    {
        switch (message)
        {
            case Start start:
                await HandleStartAsync(start, start.Reason);
                break;
            case Stop stop:
                stop.Answer(HandleStop());
                break;
            case Restart restart:
                await HandleRestartAsync(restart);
                break;
            case Kill kill:
                HandleKill(kill);
                break;
            case Update update:
                ApplyLimits(update.Definition.Limits);
                _definition = update.Definition;
                update.Done.TrySetResult();
                break;
            case Shutdown shutdown:
                shutdown.Answer(HandleShutdown(shutdown.Budget));
                break;
            case Attach attach:
                attach.Done.TrySetResult(HandleAttach(attach));
                break;
            case Detach detach:
                detach.Done.TrySetResult(await HandleDetachAsync());
                break;
            case WhenSettled waiter:
                _settledWaiters.Add(waiter.Done);
                break;
            case Flush flush:
                flush.Done.TrySetResult();
                break;
            case Input input:
                HandleInput(input);
                break;
            case Sample sample:
                StartSample(sample.Result);
                break;
            case Exited exited when exited.Generation == _generation:
                await HandleExitedAsync(exited.ExitCode);
                break;
            case Ready ready when ready.Generation == _generation && _state == RuntimeState.Starting:
                Transition(RuntimeState.Running, StateChangeReason.Ready);
                break;
            case ReadinessTimeout timeout when timeout.Generation == _generation && _state == RuntimeState.Starting:
                LogReadinessTimedOut(ServerId);
                Transition(RuntimeState.Running, StateChangeReason.ReadinessTimedOut, detail: "readiness.timed_out");
                break;
            case NextStopStep step when step.Generation == _generation && _state == RuntimeState.Stopping:
                RunStopSteps(step.Index);
                break;
            case StopTimeout timeout when timeout.Generation == _generation && _state == RuntimeState.Stopping:
                LogStopTimedOut(ServerId);
                _stopReason = StateChangeReason.StopTimedOut;
                if (_process is { } stuck)
                {
                    _ = TerminateAsync(stuck, request: null);
                }
                else
                {
                    // No process whose exit could end the stop: it ends here.
                    Transition(RuntimeState.Offline, StateChangeReason.StopTimedOut);
                    if (_restartAfterStop)
                    {
                        _restartAfterStop = false;
                        await LaunchAsync(StateChangeReason.Requested);
                    }
                }

                break;
            case AutoRestart restart when restart.Timer == _restartTimer && _state == RuntimeState.Crashed && _desiredRunning:
                _restarts.Enqueue(_clock.GetUtcNow());
                await LaunchAsync(StateChangeReason.AutoRestart);
                break;
            case ExclusiveEnded when _deferredRestart == _restartTimer && _state == RuntimeState.Crashed && _desiredRunning:
                _deferredRestart = null;
                await LaunchAsync(StateChangeReason.AutoRestart);
                break;
            case Grace grace when grace.Process == _lingering:
                _lingering = null;
                await grace.Process.DisposeAsync();
                Released(grace.Process);
                SweepStrays();
                break;
        }
    }

    /// <summary>
    /// The ack is answered before the process exists: starting it can take seconds (an account's first logon creates its
    /// profile) and the ack only says the start was accepted. A start that fails is reported as a state.
    /// </summary>
    private async Task HandleStartAsync(Request request, StateChangeReason reason)
    {
        switch (_state)
        {
            case RuntimeState.Starting or RuntimeState.Running:
                SetDesired(true);
                request.Answer(AckStatus.AlreadyInState);
                break;
            case RuntimeState.Stopping:
                // Started again as soon as the stop completes.
                SetDesired(true);
                _restartAfterStop = true;
                request.Answer(AckStatus.Accepted);
                break;
            default:
                // A manual start leaves the crash history behind.
                _restartTimer++;
                _restarts.Clear();
                await LaunchAsync(reason, request);
                break;
        }
    }

    private AckStatus HandleStop()
    {
        SetDesired(false);
        _restartAfterStop = false;
        _restartTimer++;
        if (_state is not (RuntimeState.Starting or RuntimeState.Running))
        {
            return AckStatus.AlreadyInState;
        }

        BeginStop(StateChangeReason.Requested, null);
        return AckStatus.Accepted;
    }

    private async Task HandleRestartAsync(Request request)
    {
        switch (_state)
        {
            case RuntimeState.Starting or RuntimeState.Running:
                SetDesired(true);
                request.Answer(AckStatus.Accepted);
                _restartAfterStop = true;
                BeginStop(StateChangeReason.Requested, null);
                break;
            case RuntimeState.Stopping:
                SetDesired(true);
                request.Answer(AckStatus.Accepted);
                _restartAfterStop = true;
                break;
            default:
                _restartTimer++;
                _restarts.Clear();
                await LaunchAsync(StateChangeReason.Requested, request);
                break;
        }
    }

    /// <summary>
    /// The tree is killed beside the loop: a game in a host process is killed through its host, which may not answer
    /// (then the host is killed with it). The kill is answered once that is done.
    /// </summary>
    private void HandleKill(Request request)
    {
        SetDesired(false);
        _restartAfterStop = false;
        _restartTimer++;
        if (_process is not { } process || _state is RuntimeState.Offline or RuntimeState.Crashed)
        {
            request.Answer(AckStatus.AlreadyInState);
            return;
        }

        _stopReason = StateChangeReason.Killed;
        if (_state != RuntimeState.Stopping)
        {
            Transition(RuntimeState.Stopping, StateChangeReason.Killed);
        }

        _ = TerminateAsync(process, request);
    }

    /// <summary>Kills the process's tree (see <see cref="IGameProcess.TerminateTreeAsync"/>); its exit ends the stop.</summary>
    private async Task TerminateAsync(IGameProcess process, Request? request)
    {
        try
        {
            await process.TerminateTreeAsync(1, _stopping.Token);
            request?.Answer(AckStatus.Accepted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogKillFailed(ex, ServerId);
            request?.Fail(ex);
        }
    }

    /// <summary>Accepted when the server should start again with the agent: it was starting, running or restarting.</summary>
    private AckStatus HandleShutdown(TimeSpan budget)
    {
        var restarting = _restartAfterStop;
        _restartAfterStop = false;
        _restartTimer++;
        switch (_state)
        {
            case RuntimeState.Starting or RuntimeState.Running:
                BeginStop(StateChangeReason.AgentStopping, budget);
                return AckStatus.Accepted;
            case RuntimeState.Stopping:
                // A stop the user asked for stays a stop; a restart was meant to run again.
                return restarting ? AckStatus.Accepted : AckStatus.AlreadyInState;
            default:
                // A start the agent's stop interrupted was meant to run.
                return _startInterrupted ? AckStatus.Accepted : AckStatus.AlreadyInState;
        }
    }

    /// <summary>
    /// Starts the game, unless an exclusive operation runs (<see cref="TryBeginExclusive"/>): then the start is refused
    /// with <see cref="AckStatus.Busy"/> (a crash restart runs once it ends). <paramref name="request"/>, a start the user asked
    /// for, is answered once the start is accepted, before the process exists.
    /// </summary>
    private async Task LaunchAsync(StateChangeReason reason, Request? request = null)
    {
        // Decided first: a start that is refused touches nothing (the operation may be running SteamCMD as the account).
        lock (_gate)
        {
            if (_exclusive)
            {
                LogStartRefused(ServerId, reason);
                request?.Answer(AckStatus.Busy);
                if (reason == StateChangeReason.AutoRestart)
                {
                    // Run when the operation ends, unless the server is started or stopped meanwhile.
                    _deferredRestart = _restartTimer;
                }

                return;
            }

            // Under the gate: an operation that checks the state from now on sees it starting.
            _exitCode = null;
            _stopReason = null;
            Transition(RuntimeState.Starting, reason);
        }

        if (_lingering is { } previous)
        {
            // Leftovers of the previous run (still in their grace period) could hold its files or ports.
            _lingering = null;
            await previous.DisposeAsync();
        }

        SweepStrays();
        SetDesired(true);
        request?.Answer(AckStatus.Accepted);
        var plan = _definition.Plan;
        // logfile+rcon games still get a console: srcds dies without one (spike 0001), even though it writes nothing to it.
        var spec = new ProcessLaunchSpec(ServerId, _definition.Directory, plan.Executable, plan.Arguments, plan.WorkingDirectory, plan.Environment,
            plan.Console.Mode == ManifestConsoleMode.Pipes ? ConsoleMode.Pipes : ConsoleMode.Pty, Encoding(plan.Console.Encoding), _definition.Identity,
            _definition.Limits, plan.UserProfile);
        IGameProcess process;
        try
        {
            _console.Hide(plan.Secrets, _clock.GetUtcNow());
            PrepareFiles(plan);
            process = await StartProcessAsync(spec);
        }
        catch (GameProcessException ex)
        {
            LogStartFailed(ServerId, ex.Code, ex.Message);
            if (ex.Code.StartsWith("config.", StringComparison.Ordinal) || ex.Code.StartsWith("addon.", StringComparison.Ordinal))
            {
                // A file of the server's that its user can fix (one saved as Windows-1252, say): the console says which and how.
                _console.Note($"The game was not started: {ex.Message}", _clock.GetUtcNow());
            }

            SetDesired(false);
            Transition(RuntimeState.Crashed, StateChangeReason.StartFailed, detail: ex.Code);
            return;
        }
        catch (OperationCanceledException) when (_agentStopping.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            // The agent is stopping (its stop has a budget the creation could take all of): the server stays wanted, so
            // it starts with the agent's next run; a process that still shows up is ended.
            LogStartInterrupted(ServerId);
            _startInterrupted = true;
            Transition(RuntimeState.Offline, StateChangeReason.AgentStopping);
            return;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Anything else must not leave the server Starting with no process (no exit would ever end a stop).
            LogStartFailedUnexpectedly(ex, ServerId);
            SetDesired(false);
            Transition(RuntimeState.Crashed, StateChangeReason.StartFailed, detail: "process.start_failed");
            return;
        }

        var generation = ++_generation;
        _process = process;
        _plan = plan;
        _snapshot = _snapshot with { StartedAt = process.StartedAt };
        Interlocked.Exchange(ref _consoleEnd, 0);
        Interlocked.Exchange(ref _logPosition, 0);
        _hosted = process as IHostedGameProcess;
        if (_hosted is not null)
        {
            _events.HostChanged(ServerId);
        }

        _ = WatchExitAsync(process, generation);
        Watch(process, plan, generation, plan.Readiness, logStart: 0);
    }

    /// <summary>
    /// Creates the process within <see cref="SupervisorOptions.StartTimeout"/>: a creation that never returns (a profile
    /// that never loads) must not keep stop and kill waiting behind it, nor the agent's stop (which cancels it). A
    /// process that shows up after that is ended.
    /// </summary>
    private async Task<IGameProcess> StartProcessAsync(ProcessLaunchSpec spec)
    {
        var starting = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, _agentStopping.Token);
        var start = _processes.StartAsync(spec, starting.Token);
        try
        {
            var process = await start.WaitAsync(_options.StartTimeout, _clock, starting.Token);
            starting.Dispose();
            return process;
        }
        catch (TimeoutException ex)
        {
            await starting.CancelAsync();
            _ = EndLateStartAsync(start, starting);
            throw new GameProcessException("process.start_failed",
                $"The game's process was not created within {_options.StartTimeout.TotalSeconds:0} seconds.", ex);
        }
        catch
        {
            if (start.IsCompleted)
            {
                starting.Dispose();
            }
            else
            {
                _ = EndLateStartAsync(start, starting);
            }

            throw;
        }
    }

    private async Task EndLateStartAsync(Task<IGameProcess> start, CancellationTokenSource starting)
    {
        try
        {
            var late = await start;
            LogLateProcessEnded(ServerId, late.ProcessId);
            await late.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // It never started: nothing to end.
        }
        finally
        {
            starting.Dispose();
        }
    }

    /// <summary>
    /// Follows a running process: its console (the process's output, or its log file), its readiness while it starts
    /// (<paramref name="readinessCheck"/>, null when it is ready already) and its query.
    /// </summary>
    private void Watch(IGameProcess process, LaunchPlan plan, int generation, ReadinessDefinition? readinessCheck, long logStart)
    {
        // Linear-time matching; the timeout only guards against a pathological line. It is wall-clock time, first match
        // included (the engine builds its states then): at 100 ms a busy machine tripped it on an ordinary line, and the
        // server then stayed Starting until the readiness timeout.
        var readiness = readinessCheck switch
        {
            LogPatternReadiness log => new Regex(log.Pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.Multiline,
                TimeSpan.FromSeconds(1)),
            _ => null,
        };
        StopWatchers();
        _watchers = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        var writes = Channel.CreateBounded<ConsoleWrite>(new BoundedChannelOptions(MaxQueuedWrites) { SingleReader = true });
        _writes = writes;
        _ = WriteConsoleAsync(process, writes.Reader, _watchers.Token);
        if (plan.Console.LogFile is not null)
        {
            // The game's console is its log file (logfile+rcon, or pipes for a game that writes only there); whatever reaches
            // the pseudo console or the pipes is drained and dropped.
            _pumping = PumpOutputAsync(process, generation, readiness: null, toConsole: false);
            _tailing = TailLogAsync(plan, generation, readiness, logStart, _watchers.Token);
        }
        else
        {
            // A game that prints the RCON commands it runs and their answers (Soulmask) shows them itself: they are kept to
            // tell, as with a log file, which answers need no second copy.
            _pumping = PumpOutputAsync(process, generation, readiness, toConsole: true, answers: plan.Console.Rcon?.LogsCommands == true);
        }

        switch (readinessCheck)
        {
            case LogPatternReadiness log:
                PostLater(TimeSpan.FromSeconds(log.TimeoutSeconds), new ReadinessTimeout(generation));
                break;
            case ProcessAliveReadiness alive:
                PostLater(TimeSpan.FromSeconds(alive.GraceSeconds), new Ready(generation));
                break;
            case RconReadiness rcon:
                PostLater(TimeSpan.FromSeconds(rcon.TimeoutSeconds), new ReadinessTimeout(generation));
                _ = ProbeRconAsync(plan.Rcon!, generation, _watchers.Token);
                break;
            case QueryReadiness queried:
                PostLater(TimeSpan.FromSeconds(queried.TimeoutSeconds), new ReadinessTimeout(generation));
                break;
        }

        if (plan.Query is { } query)
        {
            _ = QueryLoopAsync(query, generation, readiness: readinessCheck is QueryReadiness, _watchers.Token);
        }
    }

    private bool HandleAttach(Attach attach)
    {
        if (_process is not null || _lingering is not null || _state is not (RuntimeState.Offline or RuntimeState.Crashed))
        {
            return false;
        }

        // What the previous run of the agent wanted, without telling anyone again.
        _desiredRunning = attach.DesiredRunning;
        _exitCode = null;
        _stopReason = null;
        _restartTimer++;
        var plan = _definition.Plan;
        _console.Hide(plan.Secrets, _clock.GetUtcNow());
        var generation = ++_generation;
        _process = attach.Process;
        _plan = plan;
        _hosted = attach.Process as IHostedGameProcess;
        Interlocked.Exchange(ref _consoleEnd, attach.State.ConsoleOffset);
        Interlocked.Exchange(ref _logPosition, attach.State.LogPosition);

        // Reported by the next Hello's snapshot, which the panel always takes: no change to send.
        _state = attach.State.Stopping ? RuntimeState.Stopping : attach.State.Ready ? RuntimeState.Running : RuntimeState.Starting;
        _snapshot = new ServerSnapshot(ServerId, _state, ++_sequence, null, attach.Process.StartedAt);
        LogAttached(ServerId, attach.Process.ProcessId, _state);

        _ = WatchExitAsync(attach.Process, generation);
        if (attach.State.Stopping)
        {
            // The previous run was stopping it when it died: its exit ends that stop (not a crash), and the stop's
            // timeout runs again in case the game does not exit. A server that should run (a restart) starts again.
            _stopReason = StateChangeReason.Requested;
            _restartAfterStop = attach.DesiredRunning;
            PostLater(StopTimeLimit(null), new StopTimeout(generation));
            Watch(attach.Process, plan, generation, readinessCheck: null, attach.State.LogPosition);
            return true;
        }

        // A game that was still starting is checked again (a log line it printed already is not seen again: it is then
        // considered running when its readiness times out).
        Watch(attach.Process, plan, generation, attach.State.Ready ? null : plan.Readiness, attach.State.LogPosition);
        return true;
    }

    private async Task<bool> HandleDetachAsync()
    {
        _restartTimer++; // a crash restart that was waiting must not fire in an agent that is going away
        if (_process is not IHostedGameProcess hosted || _state is not (RuntimeState.Starting or RuntimeState.Running))
        {
            return false;
        }

        // Late messages about this process (its readiness, its timers) are ignored from here on.
        _generation++;
        StopWatchers();
        _process = null;
        await hosted.DetachAsync();
        // The console is read up to where the host will resume it: what was received is written before the offsets are kept.
        await Task.WhenAny(Task.WhenAll(_pumping, _tailing), Task.Delay(TimeSpan.FromSeconds(5), _clock));
        if (_lingering is { } lingering)
        {
            _lingering = null;
            await lingering.DisposeAsync();
        }

        return true;
    }

    /// <summary>
    /// Writes the configuration files and removes the previous run's log, through <see cref="IServerFiles"/>: the
    /// server's account controls these paths.
    /// </summary>
    private void PrepareFiles(LaunchPlan plan)
    {
        try
        {
            foreach (var file in plan.ConfigFiles)
            {
                string? existing = null;
                using (var stream = _files.OpenRead(_definition.Directory, file.Path))
                {
                    if (stream is not null)
                    {
                        if (stream.Length > ConfigFileWriter.MaximumSize)
                        {
                            throw new GameProcessException("config.too_large", $"{file.Path} is larger than {ConfigFileWriter.MaximumSize / 1024} KiB.");
                        }

                        existing = Utf8Text.Read(stream, file.Path);
                    }
                }

                // A json file's lists take out only the ids the panel wrote last time: what users added by hand stays.
                var listed = file.Lists.Count > 0 ? ConfigListRecords.Read(_files, _definition.Directory, file.Path) : null;
                using var content = new MemoryStream(new System.Text.UTF8Encoding(false).GetBytes(ConfigFileWriter.Apply(file, existing, listed)));
                _files.Write(_definition.Directory, file.Path, content);
                if (file.Lists.Count > 0 && !ConfigListRecords.Write(_files, _definition.Directory, file))
                {
                    LogListRecordNotWritten(ServerId, file.Path);
                }
            }

            // The installed addons' lines (Metamod's in gameinfo.gi), written again after a game update replaced the file.
            foreach (var edits in plan.LineEdits.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                string text;
                using (var stream = _files.OpenRead(_definition.Directory, edits.Key))
                {
                    if (stream is null || stream.Length > ConfigFileWriter.MaximumSize)
                    {
                        throw new GameProcessException("addon.file_missing", $"{edits.Key}, which an installed addon edits, is missing or too large.");
                    }

                    text = Utf8Text.Read(stream, edits.Key);
                }

                var edited = edits.Aggregate(text, LineEditor.Apply);
                if (edited != text)
                {
                    using var content = new MemoryStream(new System.Text.UTF8Encoding(false).GetBytes(edited));
                    _files.Write(_definition.Directory, edits.Key, content);
                }
            }

            if (plan.Console.LogFile is { } logFile)
            {
                // A log named by a pattern is the game's history (one file per start): the new one is the file that was not
                // there before.
                if (ManifestValidator.IsLogFilePattern(logFile))
                {
                    _logsBefore = MatchingLogs(logFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _files.Delete(_definition.Directory, logFile);
                }
            }

            // A Steam login lent to SteamCMD by an agent that stopped before it could take it back: never the game's.
            _files.Delete(_definition.Directory, Steam.SteamCmdRunner.LentLogin);

            _firewall.Allow(_definition.ShortId, Path.Combine(_definition.Directory, plan.Executable.Replace('/', Path.DirectorySeparatorChar)), plan.Ports,
                _definition.Identity);
        }
        catch (LaunchPlanException ex)
        {
            throw new GameProcessException("config.invalid", ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GameProcessException("config.write_failed", ex.Message, ex);
        }
    }

    /// <summary>
    /// Follows the game's log file (<c>logfile+rcon</c>): it is the server's console, and the readiness line may be in it.
    /// Ends when the process exits (a last read takes what it wrote while quitting).
    /// </summary>
    private async Task TailLogAsync(LaunchPlan plan, int generation, Regex? readiness, long start, CancellationToken cancellationToken)
    {
        var logFile = plan.Console.LogFile!;
        if (ManifestValidator.IsLogFilePattern(logFile))
        {
            // A game that names its log by its start (Space Engineers): the newest file the pattern matches that was not
            // there before the start, or, attached to a game an earlier agent started, the newest. Looked for until it appears,
            // less often as time goes by; a folder that cannot be listed is said once.
            var before = _logsBefore;
            string? found = null;
            var reported = false;
            for (var attempt = 1; found is null; attempt++)
            {
                try
                {
                    found = MatchingLogs(logFile).FirstOrDefault(path => before?.Contains(path) != true);
                }
                catch (Exception ex) when (ex is GameProcessException or IOException or UnauthorizedAccessException)
                {
                    if (!reported)
                    {
                        reported = true;
                        LogLogTailFailed(ServerId, ex.Message);
                        _console.Note($"Cannot look for the log file {logFile}: {ex.Message}", _clock.GetUtcNow());
                    }
                }

                if (found is null)
                {
                    try
                    {
                        await Task.Delay(attempt < 40 ? _options.LogPollInterval : s_maximumLogSearchInterval, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            logFile = found;
        }

        var tail = new LogTail(_files, _definition.Directory, logFile, TextEncoding(plan.Console.Encoding), _options.LogPollInterval,
            start, onUnreadable: ex =>
            {
                LogLogTailFailed(ServerId, ex.Message);
                _console.Note($"Cannot open the log file {plan.Console.LogFile}: {ex.Message}", _clock.GetUtcNow());
            });
        var lines = readiness is null ? null : new ConsoleLines();
        try
        {
            var logsCommands = plan.Console.Rcon?.LogsCommands == true;
            await foreach (var text in tail.ReadAsync(cancellationToken))
            {
                _console.Write(text, _clock.GetUtcNow());
                Interlocked.Exchange(ref _logPosition, tail.Position);
                if (logsCommands)
                {
                    _recentLog.Append(text);
                }

                if (lines is null)
                {
                    continue;
                }

                foreach (var line in lines.Add(text))
                {
                    bool ready;
                    try
                    {
                        ready = Matches(readiness!, line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // Logged by Matches; the readiness timeout decides, and the console goes on unchecked.
                        lines = null;
                        break;
                    }

                    if (ready)
                    {
                        _mailbox.Writer.TryWrite(new Ready(generation));
                        lines = null;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is GameProcessException or IOException or UnauthorizedAccessException)
        {
            LogLogTailFailed(ServerId, ex.Message);
            _console.Note($"Cannot follow the log file {plan.Console.LogFile}: {ex.Message}", _clock.GetUtcNow());
        }
    }

    /// <summary>The files a log pattern (<c>instance/SpaceEngineersDedicated_*.log</c>) matches, newest first.</summary>
    private List<string> MatchingLogs(string pattern)
    {
        var separator = pattern.LastIndexOf('/');
        var directory = separator < 0 ? "" : pattern[..separator];
        var name = pattern[(separator + 1)..];
        var star = name.IndexOf('*', StringComparison.Ordinal);
        var (prefix, suffix) = (name[..star], name[(star + 1)..]);
        return [.. _files.ListFiles(_definition.Directory, directory, MaximumLogFolderEntries)
            .Where(f => f.Name.Length >= prefix.Length + suffix.Length && f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWrite)
            .Select(f => directory.Length == 0 ? f.Name : $"{directory}/{f.Name}")];
    }

    /// <summary>
    /// Asks the game for its name, map and players while it runs (often while it starts: its first answer is what
    /// <see cref="QueryReadiness"/> waits for). The last answer goes with the metrics while it is recent.
    /// </summary>
    private async Task QueryLoopAsync(QueryEndpoint endpoint, int generation, bool readiness, CancellationToken cancellationToken)
    {
        var failures = 0;
        var unexpected = false; // logged once per run of the game
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var info = await _query.QueryAsync(endpoint, cancellationToken);
                _lastQuery = new QueryAnswer(generation, info);
                failures = 0;
                if (readiness)
                {
                    _mailbox.Writer.TryWrite(new Ready(generation));
                    readiness = false;
                }
            }
            catch (QueryException ex)
            {
                if (++failures == 3 && _snapshot.State == RuntimeState.Running)
                {
                    LogQueryFailing(ServerId, ex.Message);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A bug reading an odd answer must not end the queries (a query-readiness server would wait for nothing).
                if (!unexpected)
                {
                    unexpected = true;
                    LogQueryFailedUnexpectedly(ex, ServerId);
                }
            }

            try
            {
                await Task.Delay(_snapshot.State == RuntimeState.Running ? _options.QueryInterval : _options.RconRetryInterval, _clock, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The game counts as ready once it answers over RCON. Something else on its port (another server's account) is never
    /// sent the password: the probe keeps waiting, and the readiness timeout decides.
    /// </summary>
    private async Task ProbeRconAsync(RconEndpoint endpoint, int generation, CancellationToken cancellationToken)
    {
        var warned = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var session = await _rcon.ConnectAsync(endpoint, _definition.Identity, cancellationToken);
                _mailbox.Writer.TryWrite(new Ready(generation));
                return;
            }
            catch (RconException ex)
            {
                // Not listening yet.
                if (ex.NotTheGame && !warned)
                {
                    warned = true;
                    LogRconNotTheGame(ServerId, endpoint.Port);
                    _console.Note($"RCON: {ex.Message}", _clock.GetUtcNow());
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await Task.Delay(_options.RconRetryInterval, _clock, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SendRconAsync(RconEndpoint endpoint, string command, int index)
    {
        try
        {
            await using var session = await _rcon.ConnectAsync(endpoint, _definition.Identity, _stopping.Token);
            await session.ExecuteAsync(command, _stopping.Token);
        }
        catch (Exception ex) when (ex is RconException or OperationCanceledException)
        {
            // The game may close the connection as it quits; the stop timeout kills it if it did not.
            LogStopStepFailed(ServerId, index, ex.Message);
        }
    }

    /// <summary>
    /// A console command for the running game. Pseudo consoles echo what they receive; for pipes and RCON the console
    /// shows the command itself, unless the game logs RCON commands on its own (CS2), and the answer, unless the game
    /// logged it. Written to the game's input beside the loop (<see cref="WriteConsoleAsync"/>), which answers it.
    /// </summary>
    private void HandleInput(Input input)
    {
        var line = input.Line;
        if (_process is null || _plan is not { } plan || _writes is not { } writes || _state is not (RuntimeState.Starting or RuntimeState.Running)
            || !ConsoleInput.IsValid(line))
        {
            input.Answer(AckStatus.Rejected);
            return;
        }

        var now = _clock.GetUtcNow();
        // Through RCON whenever the console has it (logfile+rcon, or a process console whose game takes no input).
        if (plan.Console.Mode == ManifestConsoleMode.LogfileRcon || plan.Console.Rcon is not null)
        {
            if (plan.Rcon is not { } endpoint)
            {
                input.Answer(AckStatus.Rejected);
                return;
            }

            var logged = plan.Console.Rcon?.LogsCommands == true;
            if (!logged)
            {
                _console.Write($"> {line}\n", now);
            }

            // In the background: an answer can take a second (CS2) and the mailbox must stay free for stop and kill.
            _ = SendConsoleRconAsync(endpoint, line, logged, _generation, _watchers?.Token ?? _stopping.Token);
            input.Answer(AckStatus.Accepted);
            return;
        }

        if (!writes.Writer.TryWrite(new ConsoleWrite(line + InputNewline(plan), input.Result, now + _options.InputTimeout)))
        {
            // Commands are piling up behind one the game does not take.
            input.Answer(AckStatus.Busy);
            return;
        }

        if (plan.Console.Mode == ManifestConsoleMode.Pipes)
        {
            _console.Write($"> {line}\n", now);
        }
    }

    private static string InputNewline(LaunchPlan plan) => plan.Console.InputNewline ?? (plan.Console.Mode == ManifestConsoleMode.Pty ? "\r\n" : "\n");

    /// <summary>
    /// Writes the console input of one process, in order: a game that stopped reading its input (frozen) keeps a write
    /// waiting, and neither the loop nor a kill may wait for it. Each write must be taken within
    /// <see cref="SupervisorOptions.InputTimeout"/> of its command's arrival, its wait behind the others included (the
    /// panel waits for the ack only so long): a command the game does not take in time is refused (<see cref="AckStatus.Busy"/>),
    /// one whose time ran out before its turn is refused without being written (it must not run late), and whatever is
    /// left when the process goes away is refused too.
    /// </summary>
    private async Task WriteConsoleAsync(IGameProcess process, ChannelReader<ConsoleWrite> writes, CancellationToken cancellationToken)
    {
        await foreach (var write in writes.ReadAllAsync(CancellationToken.None))
        {
            var status = cancellationToken.IsCancellationRequested ? AckStatus.Rejected : await WriteAsync(process, write, cancellationToken);
            write.Answer?.TrySetResult(status);
        }
    }

    private async Task<AckStatus> WriteAsync(IGameProcess process, ConsoleWrite write, CancellationToken cancellationToken)
    {
        var left = write.Deadline - _clock.GetUtcNow();
        if (left <= TimeSpan.Zero)
        {
            NotTaken(write, "its time ran out behind a command the game did not take");
            return AckStatus.Busy;
        }

        using var deadline = new CancellationTokenSource(left, _clock);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            if (write.Text is { } text)
            {
                await process.WriteInputAsync(text, both.Token);
            }
            else
            {
                await process.SendInterruptAsync(both.Token);
            }

            return AckStatus.Accepted;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // It stopped reading its input (exiting, or frozen), or it is gone; its host may refuse input piling up.
            var late = (ex is OperationCanceledException && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                || ex.InnerException is GameProcessException { Code: GameHostErrors.InputBusy or GameHostErrors.InputExpired };
            if (late)
            {
                NotTaken(write, ex.Message);
                return AckStatus.Busy;
            }

            if (write.StopStep is { } index)
            {
                LogStopStepFailed(ServerId, index, ex.Message);
            }

            return AckStatus.Rejected;
        }
    }

    private void NotTaken(ConsoleWrite write, string reason)
    {
        if (write.StopStep is { } index)
        {
            LogStopStepFailed(ServerId, index, reason);
        }
        else
        {
            LogInputNotTaken(ServerId, _options.InputTimeout);
        }
    }

    /// <summary>One RCON session per process for console commands, opened on the first one and kept while it works.</summary>
    private async Task SendConsoleRconAsync(RconEndpoint endpoint, string line, bool logged, int generation, CancellationToken cancellationToken)
    {
        var mark = _recentLog.Position;
        var answer = await ExecuteConsoleRconAsync(endpoint, line, generation, cancellationToken);
        if (string.IsNullOrEmpty(answer))
        {
            return;
        }

        if (logged && FirstLine(answer) is { } probe)
        {
            try
            {
                if (await _recentLog.ShowsAsync(probe, mark, _options.LoggedAnswerWait, cancellationToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _console.Write(answer.EndsWith('\n') ? answer : answer + "\n", _clock.GetUtcNow());
    }

    private static string? FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>The answer; null when the command failed (noted in the console) or the process went away.</summary>
    private async Task<string?> ExecuteConsoleRconAsync(RconEndpoint endpoint, string line, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await _rconLock.WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }

        try
        {
            while (true)
            {
                var kept = _consoleRcon is { } current && current.Generation == generation;
                if (!kept)
                {
                    await CloseConsoleRconAsync();
                    _consoleRcon = (generation, await _rcon.ConnectAsync(endpoint, _definition.Identity, cancellationToken));
                }

                try
                {
                    return await _consoleRcon!.Value.Session.ExecuteAsync(line, cancellationToken);
                }
                catch (RconException ex) when (kept && ex.NotSent)
                {
                    // The game closed the session kept since an earlier command: once more on a fresh one. Never once the
                    // command was sent (an answer that was late, a connection lost after it), since the game may have run it.
                    await CloseConsoleRconAsync();
                }
            }
        }
        catch (RconException ex)
        {
            await CloseConsoleRconAsync();
            _console.Note($"RCON: {ex.Message}", _clock.GetUtcNow());
            return null;
        }
        catch (OperationCanceledException)
        {
            // The process exited or the supervisor is going away.
            return null;
        }
        finally
        {
            _rconLock.Release();
        }
    }

    /// <summary>Call holding <see cref="_rconLock"/>.</summary>
    private async Task CloseConsoleRconAsync()
    {
        if (_consoleRcon is { } current)
        {
            _consoleRcon = null;
            await current.Session.DisposeAsync();
        }
    }

    /// <summary>
    /// Takes a usage sample beside the loop, within <see cref="SupervisorOptions.SampleTimeout"/> (a game in a host is
    /// asked over its pipe). One at a time (<see cref="SampleAsync"/>), so <see cref="_lastUsage"/> has one writer.
    /// </summary>
    private void StartSample(TaskCompletionSource<ServerMetrics?> result)
    {
        if (_process is not { } process || _state is RuntimeState.Offline or RuntimeState.Crashed)
        {
            _lastUsage = null;
            result.TrySetResult(null);
            return;
        }

        _ = TakeSampleAsync(process, _generation, result);
    }

    private async Task TakeSampleAsync(IGameProcess process, int generation, TaskCompletionSource<ServerMetrics?> result)
    {
        ProcessUsage usage;
        try
        {
            using var limit = new CancellationTokenSource(_options.SampleTimeout, _clock);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, _stopping.Token);
            usage = await process.SampleUsageAsync(both.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSampleFailed(ServerId, ex.Message);
            result.TrySetResult(null);
            return;
        }

        var now = _clock.GetUtcNow();
        var cpu = 0.0;
        if (_lastUsage is { } last && last.Generation == generation && now > last.At)
        {
            // Share of the whole machine, like the node's CPU and Task Manager.
            var used = (usage.CpuTime - last.Usage.CpuTime).TotalSeconds;
            cpu = Math.Clamp(used / (now - last.At).TotalSeconds / Environment.ProcessorCount * 100, 0, 100);
        }

        _lastUsage = (generation, usage, now);
        // An answer older than a few queries is from a game that stopped answering: better nothing than stale players.
        var query = _lastQuery is { } answer && answer.Generation == generation && now - answer.Info.At <= _options.QueryInterval * 3 ? answer.Info : null;
        result.TrySetResult(new ServerMetrics(ServerId, Math.Round(cpu, 1), usage.MemoryBytes, usage.ProcessCount, now - process.StartedAt, query,
            usage.CommittedBytes));
    }

    /// <summary>New caps apply to the running game at once, beside the loop (the next start gets them from the definition).</summary>
    private void ApplyLimits(ProcessLimits? limits)
    {
        if (limits == _definition.Limits || _process is not { } process)
        {
            return;
        }

        _ = ApplyLimitsAsync(process, limits);
    }

    private async Task ApplyLimitsAsync(IGameProcess process, ProcessLimits? limits)
    {
        try
        {
            await process.ApplyLimitsAsync(limits, _stopping.Token);
            LogLimitsApplied(ServerId, limits?.CpuPercent, limits?.MemoryBytes);
        }
        catch (Exception ex) when (ex is GameProcessException or OperationCanceledException or ObjectDisposedException)
        {
            LogLimitsFailed(ServerId, ex.Message);
        }
    }

    private void StopWatchers()
    {
        // Console input still waiting is refused: the process it was for is gone.
        _writes?.Writer.TryComplete();
        _writes = null;
        if (_watchers is { } watchers)
        {
            _watchers = null;
            watchers.Cancel();
            watchers.Dispose();
        }
    }

    private void BeginStop(StateChangeReason reason, TimeSpan? budget)
    {
        // The exit that follows reports the same reason (unless the stop times out or a kill overtakes it).
        _stopReason = reason;
        Transition(RuntimeState.Stopping, reason);
        PostLater(StopTimeLimit(budget), new StopTimeout(_generation));
        _mailbox.Writer.TryWrite(new NextStopStep(_generation, 0));
    }

    /// <summary>How long a stop may take before the tree is killed: the manifest's timeout, within <paramref name="budget"/>.</summary>
    private TimeSpan StopTimeLimit(TimeSpan? budget)
    {
        var timeout = TimeSpan.FromSeconds((_plan ?? _definition.Plan).Stop.TimeoutSeconds);
        return budget is { } limit && limit < timeout ? limit : timeout;
    }

    /// <summary>
    /// The stop's steps from <paramref name="index"/> to the next wait. Console commands and Ctrl+C go through the console
    /// writer, after any command still waiting there; one the game does not take only fails (the timeout kills it).
    /// </summary>
    private void RunStopSteps(int index)
    {
        var plan = _plan ?? _definition.Plan;
        var steps = plan.Stop.Steps;
        for (var i = index; i < steps.Count && _process is { } process; i++)
        {
            switch (steps[i])
            {
                case ConsoleCommandStop command:
                    QueueStopStep(new ConsoleWrite(command.Command + InputNewline(plan), null, _clock.GetUtcNow() + _options.InputTimeout, i));
                    break;
                case RconCommandStop command when plan.Rcon is { } endpoint:
                    // In the background: connecting can take seconds, and a kill must never wait for it.
                    _ = SendRconAsync(endpoint, command.Command, i);
                    break;
                case CtrlCStop when process.SupportsInterrupt:
                    QueueStopStep(new ConsoleWrite(null, null, _clock.GetUtcNow() + _options.InputTimeout, i));
                    break;
                case WaitStop wait:
                    PostLater(TimeSpan.FromSeconds(wait.Seconds), new NextStopStep(_generation, i + 1));
                    return;
            }
        }
    }

    private void QueueStopStep(ConsoleWrite write)
    {
        if (_writes?.Writer.TryWrite(write) != true)
        {
            LogStopStepFailed(ServerId, write.StopStep ?? -1, "the console's input is full or closed");
        }
    }

    private async Task HandleExitedAsync(int exitCode)
    {
        var process = _process!;
        _process = null;
        StopWatchers();
        _console.Flush(_clock.GetUtcNow());
        _exitCode = exitCode;
        // A crash after a process was refused memory by the server's limit: its cause is known.
        var crashReason = process.ReachedMemoryLimit ? StateChangeReason.MemoryLimit : StateChangeReason.Crashed;
        if (process.CountProcesses() > 0)
        {
            // Children (crash handlers, helpers) get a moment to finish; then the job is terminated with them in it.
            _lingering = process;
            PostLater(_options.TreeGrace, new Grace(process));
        }
        else
        {
            await process.DisposeAsync();
            Released(process);
            SweepStrays();
        }

        if (_state == RuntimeState.Stopping)
        {
            Transition(RuntimeState.Offline, _stopReason ?? StateChangeReason.Requested, exitCode);
            if (_restartAfterStop)
            {
                _restartAfterStop = false;
                await LaunchAsync(StateChangeReason.Requested);
            }

            return;
        }

        if (exitCode == 0)
        {
            // Someone typed "quit": the game asked to stop, so it stays stopped.
            LogExitedByItself(ServerId);
            SetDesired(false);
            Transition(RuntimeState.Offline, StateChangeReason.ExitedByItself, exitCode);
            return;
        }

        LogCrashed(ServerId, exitCode, crashReason);
        if (!_desiredRunning || !_definition.RestartOnCrash)
        {
            Transition(RuntimeState.Crashed, crashReason, exitCode);
            return;
        }

        var now = _clock.GetUtcNow();
        while (_restarts.TryPeek(out var oldest) && now - oldest > _options.CrashWindow)
        {
            _restarts.Dequeue();
        }

        if (_restarts.Count >= _options.MaxRestarts)
        {
            LogCrashLoop(ServerId, _restarts.Count, _options.CrashWindow);
            SetDesired(false);
            Transition(RuntimeState.Crashed, StateChangeReason.CrashLoop, exitCode);
            return;
        }

        var delay = _options.CrashBackoff[Math.Min(_restarts.Count, _options.CrashBackoff.Count - 1)];
        Transition(RuntimeState.Crashed, crashReason, exitCode, $"restart in {delay.TotalSeconds:0} s");
        PostLater(delay, new AutoRestart(++_restartTimer));
    }

    /// <summary>A process that ran in a host is gone with it: the agent forgets the host.</summary>
    private void Released(IGameProcess process)
    {
        if (process == _hosted)
        {
            _hosted = null;
            _events.HostChanged(ServerId);
        }
    }

    /// <summary>
    /// Kills what the server's account still runs outside the Job (see <see cref="IGameProcessManager.TerminateStrayProcesses"/>),
    /// unless an exclusive operation runs: its own processes (SteamCMD) run as that account, and it sweeps before it
    /// starts them. Under the gate, so no such operation begins in the middle.
    /// </summary>
    private void SweepStrays()
    {
        lock (_gate)
        {
            if (_exclusive)
            {
                return;
            }

            try
            {
                if (_processes.TerminateStrayProcesses(_definition.Identity) is > 0 and var killed)
                {
                    LogStraysKilled(ServerId, killed);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogStraySweepFailed(ex, ServerId);
            }
        }
    }

    private async Task WatchExitAsync(IGameProcess process, int generation)
    {
        var code = await process.Exited;
        _mailbox.Writer.TryWrite(new Exited(generation, code));
    }

    /// <summary>
    /// Drains the output (a process that cannot write blocks), copies it to the console unless the game's console is its
    /// log file, and looks for the readiness line while starting.
    /// </summary>
    /// <param name="answers">Keep the output to look RCON answers up in (<see cref="RconDefinition.LogsCommands"/>).</param>
    private async Task PumpOutputAsync(IGameProcess process, int generation, Regex? readiness, bool toConsole, bool answers = false)
    {
        var lines = new ConsoleLines();
        var ready = readiness is null;
        try
        {
            await foreach (var chunk in process.Output.ReadAllAsync(_stopping.Token))
            {
                if (toConsole)
                {
                    _console.Write(chunk.Text, chunk.At);
                }

                if (answers)
                {
                    _recentLog.Append(chunk.Text);
                }

                if (chunk.End >= 0 && process == _hosted)
                {
                    Interlocked.Exchange(ref _consoleEnd, chunk.End);
                }

                if (ready)
                {
                    continue;
                }

                foreach (var line in lines.Add(chunk.Text))
                {
                    try
                    {
                        if (!readiness!.IsMatch(line))
                        {
                            continue;
                        }

                        _mailbox.Writer.TryWrite(new Ready(generation));
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // Keep draining (a process whose output is not read blocks); the readiness timeout decides.
                        LogReadinessPatternTooSlow(ServerId);
                    }

                    ready = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Supervisor disposed.
        }
    }

    private bool Matches(Regex readiness, string line)
    {
        try
        {
            return readiness.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            LogReadinessPatternTooSlow(ServerId);
            throw;
        }
    }

    private void Transition(RuntimeState state, StateChangeReason reason, int? exitCode = null, string? detail = null)
    {
        var previous = _state;
        _state = state;
        var now = _clock.GetUtcNow();
        var change = new ServerStateChanged(ServerId, state, previous, now, ++_sequence, exitCode ?? _exitCode, reason, detail);
        _snapshot = new ServerSnapshot(ServerId, state, _sequence, change.ExitCode, _process?.StartedAt);
        _lastChange = change;
        LogTransition(ServerId, previous, state, reason);
        _events.StateChanged(change);
        if (_hosted is not null)
        {
            _events.HostChanged(ServerId); // ready or not, for the next run of the agent
        }
    }

    /// <summary>
    /// Keeps what the server should do, then tells the agent to save it. Saving never fails a transition: a server whose
    /// game exited while the disk was full must not stay Running with no process.
    /// </summary>
    private void SetDesired(bool running)
    {
        if (_desiredRunning != running)
        {
            _desiredRunning = running;
            try
            {
                _events.DesiredChanged(ServerId, running);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogDesiredNotSaved(ex, ServerId, running);
            }
        }
    }

    private void NotifySettled()
    {
        if (_settledWaiters.Count > 0 && _process is null && _lingering is null && _state is RuntimeState.Offline or RuntimeState.Crashed)
        {
            foreach (var waiter in _settledWaiters)
            {
                waiter.TrySetResult();
            }

            _settledWaiters.Clear();
        }
    }

    private static System.Text.Encoding TextEncoding(ManifestConsoleEncoding encoding) => encoding switch
    {
        ManifestConsoleEncoding.Oem => System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
        ManifestConsoleEncoding.Ansi => System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage),
        _ => System.Text.Encoding.UTF8,
    };

    private static ConsoleEncoding Encoding(ManifestConsoleEncoding encoding) => encoding switch
    {
        ManifestConsoleEncoding.Oem => ConsoleEncoding.Oem,
        ManifestConsoleEncoding.Ansi => ConsoleEncoding.Ansi,
        _ => ConsoleEncoding.Utf8,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: the record of the ids written into {Path} cannot be written; the next start leaves them in place")]
    private partial void LogListRecordNotWritten(Guid serverId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: killed {Count} processes its account ran outside the server's job")]
    private partial void LogStraysKilled(Guid serverId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: could not look for processes of its account outside its job")]
    private partial void LogStraySweepFailed(Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: took over its game (process {ProcessId}) from its host, {State}")]
    private partial void LogAttached(Guid serverId, int processId, RuntimeState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: {Previous} → {State} ({Reason})")]
    private partial void LogTransition(Guid serverId, RuntimeState previous, RuntimeState state, StateChangeReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} could not start: {Code} ({Reason})")]
    private partial void LogStartFailed(Guid serverId, string code, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId} could not start")]
    private partial void LogStartFailedUnexpectedly(Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} crashed with exit code {ExitCode} ({Reason})")]
    private partial void LogCrashed(Guid serverId, int exitCode, StateChangeReason reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: limits applied to the running game (CPU {CpuPercent} %, memory {MemoryBytes} bytes)")]
    private partial void LogLimitsApplied(Guid serverId, int? cpuPercent, long? memoryBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: the new limits could not be applied to the running game (they apply at its next start): {Reason}")]
    private partial void LogLimitsFailed(Guid serverId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} crashed {Count} times within {Window}; not restarting it again")]
    private partial void LogCrashLoop(Guid serverId, int count, TimeSpan window);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId} exited by itself with code 0; it stays stopped")]
    private partial void LogExitedByItself(Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} did not report it was ready in time; considering it running")]
    private partial void LogReadinessTimedOut(Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: the readiness pattern took too long on a line; not checking it anymore")]
    private partial void LogReadinessPatternTooSlow(Guid serverId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Server {ServerId}: usage not sampled: {Reason}")]
    private partial void LogSampleFailed(Guid serverId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId} does not answer its query: {Reason}")]
    private partial void LogQueryFailing(Guid serverId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId}: its query failed unexpectedly")]
    private partial void LogQueryFailedUnexpectedly(Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Server {ServerId}: what listens on its RCON port {Port} does not run as the server's account; its password is not sent there")]
    private partial void LogRconNotTheGame(Guid serverId, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: cannot follow its log file: {Reason}")]
    private partial void LogLogTailFailed(Guid serverId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} did not stop in time; killing its processes")]
    private partial void LogStopTimedOut(Guid serverId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Server {ServerId}: stop step {Index} failed: {Reason}")]
    private partial void LogStopStepFailed(Guid serverId, int index, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId}: unexpected error handling {Message}")]
    private partial void LogUnexpected(Exception exception, Guid serverId, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: the game did not take a console command within {Timeout}; refused")]
    private partial void LogInputNotTaken(Guid serverId, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId}: its processes could not be killed")]
    private partial void LogKillFailed(Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: start ({Reason}) refused: an operation that keeps it stopped is running")]
    private partial void LogStartRefused(Guid serverId, StateChangeReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: the agent is stopping while its game's process is being created; it starts with the agent's next run")]
    private partial void LogStartInterrupted(Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: its process {ProcessId} was created after its start had failed; ended it")]
    private partial void LogLateProcessEnded(Guid serverId, int processId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId}: whether it should run ({Running}) could not be saved")]
    private partial void LogDesiredNotSaved(Exception exception, Guid serverId, bool running);

    private abstract record Message;

    private abstract record Command : Message
    {
        public abstract void Fail(Exception exception);
    }

    private abstract record Request(TaskCompletionSource<AckStatus> Result) : Command
    {
        public void Answer(AckStatus status) => Result.TrySetResult(status);

        public override void Fail(Exception exception) => Result.TrySetException(exception);
    }

    private sealed record Start(TaskCompletionSource<AckStatus> Result, StateChangeReason Reason) : Request(Result);

    private sealed record Stop(TaskCompletionSource<AckStatus> Result) : Request(Result);

    private sealed record Restart(TaskCompletionSource<AckStatus> Result) : Request(Result);

    private sealed record Kill(TaskCompletionSource<AckStatus> Result) : Request(Result);

    private sealed record Shutdown(TaskCompletionSource<AckStatus> Result, TimeSpan Budget) : Request(Result);

    private sealed record Update(TaskCompletionSource Done, ServerDefinition Definition) : Command
    {
        public override void Fail(Exception exception) => Done.TrySetException(exception);
    }

    private sealed record Input(TaskCompletionSource<AckStatus> Result, string Line) : Request(Result);

    private sealed record Sample(TaskCompletionSource<ServerMetrics?> Result) : Command
    {
        public override void Fail(Exception exception) => Result.TrySetException(exception);
    }

    private sealed record WhenSettled(TaskCompletionSource Done) : Message;

    private sealed record Flush(TaskCompletionSource Done) : Command
    {
        public override void Fail(Exception exception) => Done.TrySetException(exception);
    }

    private sealed record Exited(int Generation, int ExitCode) : Message;

    private sealed record Ready(int Generation) : Message;

    private sealed record QueryAnswer(int Generation, ServerQueryInfo Info);

    private sealed record ReadinessTimeout(int Generation) : Message;

    private sealed record NextStopStep(int Generation, int Index) : Message;

    private sealed record StopTimeout(int Generation) : Message;

    private sealed record AutoRestart(int Timer) : Message;

    private sealed record ExclusiveEnded : Message;

    private sealed record Grace(IGameProcess Process) : Message;

    private sealed record Attach(TaskCompletionSource<bool> Done, IGameProcess Process, GameHostState State, bool DesiredRunning) : Message;

    private sealed record Detach(TaskCompletionSource<bool> Done) : Message;

    /// <summary>Console input for the running process: <paramref name="Text"/>, or Ctrl+C when null.</summary>
    /// <param name="Answer">The command's ack; null for a stop step (<paramref name="StopStep"/>, its index).</param>
    /// <param name="Deadline">When it must have been taken: <see cref="SupervisorOptions.InputTimeout"/> after it came.</param>
    private sealed record ConsoleWrite(string? Text, TaskCompletionSource<AckStatus>? Answer, DateTimeOffset Deadline, int? StopStep = null);
}
