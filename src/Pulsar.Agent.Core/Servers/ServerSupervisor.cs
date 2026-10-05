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
    bool RestartOnCrash);

/// <summary>Where a supervisor reports what happens to its server.</summary>
public interface IServerEventSink
{
    void StateChanged(ServerStateChanged change);

    /// <summary>The server should (not) be running: persisted by the agent and mirrored by the panel.</summary>
    void DesiredChanged(Guid serverId, bool running);
}

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
}

/// <summary>
/// Runs one server (docs/architecture/05): a single-reader mailbox processes commands and process events one at a
/// time, so nothing races ("stop arrives while start is creating the process"). Nothing in the loop waits for long:
/// readiness, stop steps, timeouts and crash backoff are timers that post messages back, so a kill is always handled
/// right away. Messages from an earlier process (a late timer, an old exit) carry its generation and are ignored.
/// </summary>
public sealed partial class ServerSupervisor : IAsyncDisposable
{
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
    private readonly Queue<DateTimeOffset> _restarts = new();
    private readonly List<TaskCompletionSource> _settledWaiters = [];
    private readonly Task _loop;

    private ServerDefinition _definition;
    private RuntimeState _state = RuntimeState.Offline;
    private IGameProcess? _process;
    private IGameProcess? _lingering;
    private LaunchPlan? _plan; // what the running process was started with
    private CancellationTokenSource? _watchers; // log tail and RCON probe of the running process
    private int _generation;
    private long _sequence;
    private int? _exitCode;
    private bool _desiredRunning;
    private bool _restartAfterStop;
    private StateChangeReason? _stopReason;
    private int _restartTimer;
    private volatile ServerSnapshot _snapshot;
    private volatile ServerStateChanged? _lastChange;
    private (int Generation, IRconSession Session)? _consoleRcon; // guarded by _rconLock
    private (int Generation, ProcessUsage Usage, DateTimeOffset At)? _lastUsage;
    private volatile QueryAnswer? _lastQuery;

    public ServerSupervisor(ServerDefinition definition, IGameProcessManager processes, IServerEventSink events, TimeProvider clock,
        SupervisorOptions options, ILogger logger, IServerFiles? files = null, IRconConnector? rcon = null, IServerFirewall? firewall = null,
        ServerConsole? console = null, IServerQuery? query = null)
    {
        _definition = definition;
        _processes = processes;
        _files = files ?? new DirectServerFiles();
        _rcon = rcon ?? new SourceRconConnector();
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

    /// <summary>
    /// Sends a command line to the running game: to its console input, or over RCON for <c>logfile+rcon</c> games (in
    /// the background: the answer, or why there is none, shows up in the console). Rejected when it is not running.
    /// </summary>
    public Task<AckStatus> SendInputAsync(string line) => Ask(tcs => new Input(tcs, line));

    /// <summary>Usage of the running game since the previous sample; null when it is not running.</summary>
    public async Task<ServerMetrics?> SampleAsync()
    {
        var sample = new TaskCompletionSource<ServerMetrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Post(new Sample(sample));
        return await sample.Task;
    }

    public Task<AckStatus> StartAsync(StateChangeReason reason = StateChangeReason.Requested) => Ask(tcs => new Start(tcs, reason));

    public Task<AckStatus> StopAsync() => Ask(tcs => new Stop(tcs));

    public Task<AckStatus> RestartAsync() => Ask(tcs => new Restart(tcs));

    public Task<AckStatus> KillAsync() => Ask(tcs => new Kill(tcs));

    /// <summary>Uses a new definition for the next start (a running process keeps the one it started with).</summary>
    public Task UpdateAsync(ServerDefinition definition) => Ask(tcs => new Update(tcs, definition));

    /// <summary>
    /// The agent is stopping: a graceful stop within <paramref name="budget"/>, then the tree is killed. Completes when the
    /// server is no longer running. Returns whether it was running (so the agent can start it again next time).
    /// </summary>
    public async Task<bool> ShutdownAsync(TimeSpan budget)
    {
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
                kill.Answer(HandleKill());
                break;
            case Update update:
                _definition = update.Definition;
                update.Done.TrySetResult();
                break;
            case Shutdown shutdown:
                shutdown.Answer(HandleShutdown(shutdown.Budget));
                break;
            case WhenSettled waiter:
                _settledWaiters.Add(waiter.Done);
                break;
            case Flush flush:
                flush.Done.TrySetResult();
                break;
            case Input input:
                input.Answer(await HandleInputAsync(input.Line));
                break;
            case Sample sample:
                sample.Result.TrySetResult(TakeSample());
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
                await RunStopStepsAsync(step.Index);
                break;
            case StopTimeout timeout when timeout.Generation == _generation && _state == RuntimeState.Stopping:
                LogStopTimedOut(ServerId);
                _stopReason = StateChangeReason.StopTimedOut;
                _process?.TerminateTree(1);
                break;
            case AutoRestart restart when restart.Timer == _restartTimer && _state == RuntimeState.Crashed && _desiredRunning:
                _restarts.Enqueue(_clock.GetUtcNow());
                await LaunchAsync(StateChangeReason.AutoRestart);
                break;
            case Grace grace when grace.Process == _lingering:
                _lingering = null;
                await grace.Process.DisposeAsync();
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
        SetDesired(true);
        switch (_state)
        {
            case RuntimeState.Starting or RuntimeState.Running:
                request.Answer(AckStatus.AlreadyInState);
                break;
            case RuntimeState.Stopping:
                // Started again as soon as the stop completes.
                _restartAfterStop = true;
                request.Answer(AckStatus.Accepted);
                break;
            default:
                // A manual start leaves the crash history behind.
                _restartTimer++;
                _restarts.Clear();
                request.Answer(AckStatus.Accepted);
                await LaunchAsync(reason);
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
        SetDesired(true);
        request.Answer(AckStatus.Accepted);
        switch (_state)
        {
            case RuntimeState.Starting or RuntimeState.Running:
                _restartAfterStop = true;
                BeginStop(StateChangeReason.Requested, null);
                break;
            case RuntimeState.Stopping:
                _restartAfterStop = true;
                break;
            default:
                _restartTimer++;
                _restarts.Clear();
                await LaunchAsync(StateChangeReason.Requested);
                break;
        }
    }

    private AckStatus HandleKill()
    {
        SetDesired(false);
        _restartAfterStop = false;
        _restartTimer++;
        if (_process is null || _state is RuntimeState.Offline or RuntimeState.Crashed)
        {
            return AckStatus.AlreadyInState;
        }

        _stopReason = StateChangeReason.Killed;
        if (_state != RuntimeState.Stopping)
        {
            Transition(RuntimeState.Stopping, StateChangeReason.Killed);
        }

        _process.TerminateTree(1);
        return AckStatus.Accepted;
    }

    private AckStatus HandleShutdown(TimeSpan budget)
    {
        _restartAfterStop = false;
        _restartTimer++;
        switch (_state)
        {
            case RuntimeState.Starting or RuntimeState.Running:
                BeginStop(StateChangeReason.AgentStopping, budget);
                return AckStatus.Accepted;
            case RuntimeState.Stopping:
                return AckStatus.Accepted;
            default:
                return AckStatus.AlreadyInState;
        }
    }

    private async Task LaunchAsync(StateChangeReason reason)
    {
        if (_lingering is { } previous)
        {
            // Leftovers of the previous run (still in their grace period) could hold its files or ports.
            _lingering = null;
            await previous.DisposeAsync();
        }

        SweepStrays();
        _exitCode = null;
        _stopReason = null;
        Transition(RuntimeState.Starting, reason);
        var plan = _definition.Plan;
        // logfile+rcon games still get a console: srcds dies without one (spike 0001), even though it writes nothing to it.
        var spec = new ProcessLaunchSpec(ServerId, _definition.Directory, plan.Executable, plan.Arguments, plan.WorkingDirectory, plan.Environment,
            plan.Console.Mode == ManifestConsoleMode.Pipes ? ConsoleMode.Pipes : ConsoleMode.Pty, Encoding(plan.Console.Encoding), _definition.Identity);
        IGameProcess process;
        try
        {
            PrepareFiles(plan);
            process = await _processes.StartAsync(spec, _stopping.Token);
        }
        catch (GameProcessException ex)
        {
            LogStartFailed(ServerId, ex.Code, ex.Message);
            SetDesired(false);
            Transition(RuntimeState.Crashed, StateChangeReason.StartFailed, detail: ex.Code);
            return;
        }

        var generation = ++_generation;
        _process = process;
        _plan = plan;
        _snapshot = _snapshot with { StartedAt = process.StartedAt };
        _ = WatchExitAsync(process, generation);
        var readiness = plan.Readiness switch
        {
            LogPatternReadiness log => new Regex(log.Pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.Multiline,
                TimeSpan.FromMilliseconds(100)),
            _ => null,
        };
        StopWatchers();
        _watchers = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (plan.Console.Mode == ManifestConsoleMode.LogfileRcon)
        {
            // The game's console is its log file; whatever reaches the pseudo console is drained and dropped.
            _ = PumpOutputAsync(process, generation, readiness: null, toConsole: false);
            _ = TailLogAsync(plan, generation, readiness, _watchers.Token);
        }
        else
        {
            _ = PumpOutputAsync(process, generation, readiness, toConsole: true);
        }

        switch (plan.Readiness)
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
            _ = QueryLoopAsync(query, generation, readiness: plan.Readiness is QueryReadiness, _watchers.Token);
        }
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

                        existing = new StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd();
                    }
                }

                using var content = new MemoryStream(new System.Text.UTF8Encoding(false).GetBytes(ConfigFileWriter.Apply(file, existing)));
                _files.Write(_definition.Directory, file.Path, content);
            }

            if (plan.Console.LogFile is { } logFile)
            {
                _files.Delete(_definition.Directory, logFile);
            }

            _firewall.Allow(_definition.ShortId, Path.Combine(_definition.Directory, plan.Executable.Replace('/', Path.DirectorySeparatorChar)), plan.Ports,
                plan.PortNames);
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
    private async Task TailLogAsync(LaunchPlan plan, int generation, Regex? readiness, CancellationToken cancellationToken)
    {
        var tail = new LogTail(_files, _definition.Directory, plan.Console.LogFile!, TextEncoding(plan.Console.Encoding), _options.LogPollInterval);
        var lines = readiness is null ? null : new ConsoleLines();
        try
        {
            var logsCommands = plan.Console.Rcon?.LogsCommands == true;
            await foreach (var text in tail.ReadAsync(cancellationToken))
            {
                _console.Write(text, _clock.GetUtcNow());
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

    /// <summary>
    /// Asks the game for its name, map and players while it runs (often while it starts: its first answer is what
    /// <see cref="QueryReadiness"/> waits for). The last answer goes with the metrics while it is recent.
    /// </summary>
    private async Task QueryLoopAsync(QueryEndpoint endpoint, int generation, bool readiness, CancellationToken cancellationToken)
    {
        var failures = 0;
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

    /// <summary>The game counts as ready once it answers over RCON.</summary>
    private async Task ProbeRconAsync(RconEndpoint endpoint, int generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var session = await _rcon.ConnectAsync(endpoint, cancellationToken);
                _mailbox.Writer.TryWrite(new Ready(generation));
                return;
            }
            catch (RconException)
            {
                // Not listening yet.
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
            await using var session = await _rcon.ConnectAsync(endpoint, _stopping.Token);
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
    /// logged it.
    /// </summary>
    private async Task<AckStatus> HandleInputAsync(string line)
    {
        if (_process is not { } process || _plan is not { } plan || _state is not (RuntimeState.Starting or RuntimeState.Running)
            || !ConsoleInput.IsValid(line))
        {
            return AckStatus.Rejected;
        }

        var now = _clock.GetUtcNow();
        // Through RCON whenever the console has it (logfile+rcon, or a process console whose game takes no input).
        if (plan.Console.Mode == ManifestConsoleMode.LogfileRcon || plan.Console.Rcon is not null)
        {
            if (plan.Rcon is not { } endpoint)
            {
                return AckStatus.Rejected;
            }

            var logged = plan.Console.Rcon?.LogsCommands == true;
            if (!logged)
            {
                _console.Write($"> {line}\n", now);
            }

            // In the background: an answer can take a second (CS2) and the mailbox must stay free for stop and kill.
            _ = SendConsoleRconAsync(endpoint, line, logged, _generation, _watchers?.Token ?? _stopping.Token);
            return AckStatus.Accepted;
        }

        if (plan.Console.Mode == ManifestConsoleMode.Pipes)
        {
            _console.Write($"> {line}\n", now);
        }

        try
        {
            var newline = plan.Console.InputNewline ?? (plan.Console.Mode == ManifestConsoleMode.Pty ? "\r\n" : "\n");
            await process.WriteInputAsync(line + newline, _stopping.Token);
            return AckStatus.Accepted;
        }
        catch (InvalidOperationException)
        {
            // The process stopped reading its input (it is exiting).
            return AckStatus.Rejected;
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
                    _consoleRcon = (generation, await _rcon.ConnectAsync(endpoint, cancellationToken));
                }

                try
                {
                    return await _consoleRcon!.Value.Session.ExecuteAsync(line, cancellationToken);
                }
                catch (RconException ex) when (kept && ex.ConnectionLost)
                {
                    // The game closed the session kept since an earlier command: once more on a fresh one. Never after
                    // an answer that was late, since the game may have run the command.
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

    private ServerMetrics? TakeSample()
    {
        if (_process is not { } process || _state is RuntimeState.Offline or RuntimeState.Crashed)
        {
            _lastUsage = null;
            return null;
        }

        ProcessUsage usage;
        try
        {
            usage = process.SampleUsage();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSampleFailed(ServerId, ex.Message);
            return null;
        }

        var now = _clock.GetUtcNow();
        var cpu = 0.0;
        if (_lastUsage is { } last && last.Generation == _generation && now > last.At)
        {
            // Share of the whole machine, like the node's CPU and Task Manager.
            var used = (usage.CpuTime - last.Usage.CpuTime).TotalSeconds;
            cpu = Math.Clamp(used / (now - last.At).TotalSeconds / Environment.ProcessorCount * 100, 0, 100);
        }

        _lastUsage = (_generation, usage, now);
        // An answer older than a few queries is from a game that stopped answering: better nothing than stale players.
        var query = _lastQuery is { } answer && answer.Generation == _generation && now - answer.Info.At <= _options.QueryInterval * 3 ? answer.Info : null;
        return new ServerMetrics(ServerId, Math.Round(cpu, 1), usage.MemoryBytes, usage.ProcessCount, now - process.StartedAt, query);
    }

    private void StopWatchers()
    {
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
        var timeout = TimeSpan.FromSeconds((_plan ?? _definition.Plan).Stop.TimeoutSeconds);
        if (budget is { } limit && limit < timeout)
        {
            timeout = limit;
        }

        PostLater(timeout, new StopTimeout(_generation));
        _mailbox.Writer.TryWrite(new NextStopStep(_generation, 0));
    }

    private async Task RunStopStepsAsync(int index)
    {
        var plan = _plan ?? _definition.Plan;
        var steps = plan.Stop.Steps;
        var newline = plan.Console.InputNewline ?? (plan.Console.Mode == ManifestConsoleMode.Pty ? "\r\n" : "\n");
        for (var i = index; i < steps.Count && _process is { } process; i++)
        {
            try
            {
                switch (steps[i])
                {
                    case ConsoleCommandStop command:
                        await process.WriteInputAsync(command.Command + newline, _stopping.Token);
                        break;
                    case RconCommandStop command when plan.Rcon is { } endpoint:
                        // In the background: connecting can take seconds, and a kill must never wait for it.
                        _ = SendRconAsync(endpoint, command.Command, i);
                        break;
                    case CtrlCStop when process.SupportsInterrupt:
                        await process.SendInterruptAsync(_stopping.Token);
                        break;
                    case WaitStop wait:
                        PostLater(TimeSpan.FromSeconds(wait.Seconds), new NextStopStep(_generation, i + 1));
                        return;
                }
            }
            catch (InvalidOperationException ex)
            {
                // The process stopped reading (or exited): the timeout kills it if needed.
                LogStopStepFailed(ServerId, i, ex.Message);
            }
        }
    }

    private async Task HandleExitedAsync(int exitCode)
    {
        var process = _process!;
        _process = null;
        StopWatchers();
        _exitCode = exitCode;
        if (process.CountProcesses() > 0)
        {
            // Children (crash handlers, helpers) get a moment to finish; then the job is terminated with them in it.
            _lingering = process;
            PostLater(_options.TreeGrace, new Grace(process));
        }
        else
        {
            await process.DisposeAsync();
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

        LogCrashed(ServerId, exitCode);
        if (!_desiredRunning || !_definition.RestartOnCrash)
        {
            Transition(RuntimeState.Crashed, StateChangeReason.Crashed, exitCode);
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
        Transition(RuntimeState.Crashed, StateChangeReason.Crashed, exitCode, $"restart in {delay.TotalSeconds:0} s");
        PostLater(delay, new AutoRestart(++_restartTimer));
    }

    /// <summary>Kills what the server's account still runs outside the Job (see <see cref="IGameProcessManager.TerminateStrayProcesses"/>).</summary>
    private void SweepStrays()
    {
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

    private async Task WatchExitAsync(IGameProcess process, int generation)
    {
        var code = await process.Exited;
        _mailbox.Writer.TryWrite(new Exited(generation, code));
    }

    /// <summary>
    /// Drains the output (a process that cannot write blocks), copies it to the console unless the game's console is its
    /// log file, and looks for the readiness line while starting.
    /// </summary>
    private async Task PumpOutputAsync(IGameProcess process, int generation, Regex? readiness, bool toConsole)
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
    }

    private void SetDesired(bool running)
    {
        if (_desiredRunning != running)
        {
            _desiredRunning = running;
            _events.DesiredChanged(ServerId, running);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: killed {Count} processes its account ran outside the server's job")]
    private partial void LogStraysKilled(Guid serverId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: could not look for processes of its account outside its job")]
    private partial void LogStraySweepFailed(Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: {Previous} → {State} ({Reason})")]
    private partial void LogTransition(Guid serverId, RuntimeState previous, RuntimeState state, StateChangeReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} could not start: {Code} ({Reason})")]
    private partial void LogStartFailed(Guid serverId, string code, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} crashed with exit code {ExitCode}")]
    private partial void LogCrashed(Guid serverId, int exitCode);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: cannot follow its log file: {Reason}")]
    private partial void LogLogTailFailed(Guid serverId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} did not stop in time; killing its processes")]
    private partial void LogStopTimedOut(Guid serverId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Server {ServerId}: stop step {Index} failed: {Reason}")]
    private partial void LogStopStepFailed(Guid serverId, int index, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId}: unexpected error handling {Message}")]
    private partial void LogUnexpected(Exception exception, Guid serverId, string message);

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

    private sealed record Grace(IGameProcess Process) : Message;
}
