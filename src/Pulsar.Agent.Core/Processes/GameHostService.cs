using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace Pulsar.Agent.Core.Processes;

public sealed class GameHostOptions
{
    /// <summary>Characters of output kept for an agent that is not reading (about 2 MiB).</summary>
    public int OutputCapacity { get; set; } = 1024 * 1024;

    /// <summary>A host nobody connects to in this time after it started ends (the agent died before connecting).</summary>
    public TimeSpan FirstConnectionTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long children may outlive the game's main process while no agent is connected to decide.</summary>
    public TimeSpan TreeGrace { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The host of one game server (<c>PulsarAgent host</c>, docs/architecture/06, "Server hosts"): a process of its own
/// that starts the game with the agent's process manager and owns its Job and console, so the game keeps running when
/// the agent stops, crashes or is updated. The agent talks to it over a named pipe only it may open; one connection at
/// a time, and the next one when an agent comes back. While no agent reads, output waits in a bounded ring; when the
/// game exits, the host keeps its exit code until an agent collects it and releases the host. If the host itself dies,
/// the Job closes and the game dies with it: nothing is left running unwatched.
/// </summary>
public sealed partial class GameHostService(
    IGameProcessManager processes,
    Guid serverId,
    string pipe,
    GameHostOptions options,
    TimeProvider clock,
    ILogger<GameHostService> logger,
    Func<string, NamedPipeServerStream>? createPipe = null) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly OutputRing _output = new(options.OutputCapacity);
    private readonly CancellationTokenSource _released = new();
    private IGameProcess? _game;
    private int? _exitCode;
    private bool _reachedMemoryLimit;
    private Session? _session;
    private Task _pump = Task.CompletedTask;

    /// <summary>Serves agents until one releases the host (or none came at all). Returns the process's exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream server;
        try
        {
            server = (createPipe ?? GameHostPipes.CreateServer)(pipe);
        }
        catch (IOException ex)
        {
            // Another process has the pipe already: never serve beside it.
            LogPipeTaken(serverId, pipe, ex.Message);
            return 3;
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _released.Token);
        await using (server)
        {
            LogListening(serverId, pipe);
            while (!stopping.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                if (Volatile.Read(ref _game) is null)
                {
                    wait.CancelAfter(options.FirstConnectionTimeout);
                }

                try
                {
                    await server.WaitForConnectionAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                {
                    LogNobodyCame(serverId);
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await ServeAsync(server, stopping.Token).ConfigureAwait(false);
                if (server.IsConnected)
                {
                    try
                    {
                        server.Disconnect();
                    }
                    catch (IOException)
                    {
                        // Already gone.
                    }
                }
            }
        }

        await EndGameAsync().ConfigureAwait(false);
        LogEnded(serverId);
        return 0;
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var session = new Session(this, stream, cancellationToken);
        lock (_lock)
        {
            _session = session;
        }

        LogConnected(serverId);
        try
        {
            await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (_session == session)
                {
                    _session = null;
                }
            }

            await session.DisposeAsync().ConfigureAwait(false);
            LogDisconnected(serverId);
        }
    }

    private GameHostStatus Status()
    {
        lock (_lock)
        {
            return new GameHostStatus(serverId, _game?.ProcessId, _game?.StartedAt, _exitCode, _reachedMemoryLimit, _game?.SupportsInterrupt ?? false,
                _output.Start, _output.End);
        }
    }

    private async Task<HostReply> StartAsync(HostStart start)
    {
        if (start.Launch.ServerId != serverId)
        {
            return new HostReply(start.Id, "host.wrong_server", "This host belongs to another server.");
        }

        if (Volatile.Read(ref _game) is not null)
        {
            return new HostReply(start.Id, "host.already_started", "This host already started its game.");
        }

        IGameProcess game;
        try
        {
            game = await processes.StartAsync(start.Launch.ToSpec(), _released.Token).ConfigureAwait(false);
        }
        catch (GameProcessException ex)
        {
            LogStartFailed(serverId, ex.Code, ex.Message);
            return new HostReply(start.Id, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogStartFailedUnexpectedly(ex, serverId);
            return new HostReply(start.Id, "process.start_failed", ex.Message);
        }

        lock (_lock)
        {
            _game = game;
        }

        LogStarted(serverId, game.ProcessId);
        _pump = PumpAsync(game);
        _ = WatchExitAsync(game);
        return new HostReply(start.Id, Status: Status());
    }

    /// <summary>Drains the game's output into the ring (a game whose output is not read blocks) whether or not anyone reads.</summary>
    private async Task PumpAsync(IGameProcess game)
    {
        try
        {
            await foreach (var chunk in game.Output.ReadAllAsync().ConfigureAwait(false))
            {
                _output.Append(chunk.Text, chunk.At);
            }
        }
        finally
        {
            _output.Complete();
        }
    }

    private async Task WatchExitAsync(IGameProcess game)
    {
        var code = await game.Exited.ConfigureAwait(false);
        int remaining;
        Session? session;
        lock (_lock)
        {
            _exitCode = code;
            _reachedMemoryLimit = game.ReachedMemoryLimit;
            remaining = game.CountProcesses();
            session = _session;
        }

        LogExited(serverId, code, remaining);
        if (session is not null)
        {
            // The agent decides about what is left of the tree (its own grace period).
            await session.SendAsync(new HostExited(code, _reachedMemoryLimit, remaining)).ConfigureAwait(false);
            return;
        }

        if (remaining > 0)
        {
            // Nobody to decide: children (crash handlers, helpers) get the same grace the agent would give them.
            try
            {
                await Task.Delay(options.TreeGrace, clock, _released.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            game.TerminateTree(1);
        }
    }

    private async Task EndGameAsync()
    {
        if (Volatile.Read(ref _game) is { } game)
        {
            // Disposing terminates the job: whatever is left of the tree ends with the host.
            await game.DisposeAsync().ConfigureAwait(false);
            await _pump.ConfigureAwait(false);
        }
    }

    /// <summary>One agent's connection.</summary>
    private sealed class Session(GameHostService host, NamedPipeServerStream stream, CancellationToken stopping) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _closed = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        private Task _forwarding = Task.CompletedTask;
        private bool _greeted;

        public async Task RunAsync()
        {
            try
            {
                while (!_closed.IsCancellationRequested)
                {
                    var frame = await GameHostProtocol.ReadAsync(stream, _closed.Token).ConfigureAwait(false);
                    if (frame is null)
                    {
                        return;
                    }

                    if (frame is not HostRequest request)
                    {
                        host.LogUnexpectedFrame(host.ServerId, frame.GetType().Name);
                        return;
                    }

                    var reply = await HandleAsync(request).ConfigureAwait(false);
                    if (!await SendAsync(reply).ConfigureAwait(false))
                    {
                        return;
                    }

                    if (request is HostRelease)
                    {
                        await host._released.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                // The agent went away (or spoke nonsense): the game keeps running, and the next agent connects again.
                if (ex is InvalidDataException)
                {
                    host.LogUnexpectedFrame(host.ServerId, ex.Message);
                }
            }
        }

        private async Task<HostReply> HandleAsync(HostRequest request)
        {
            if (request is HostHello hello)
            {
                if (hello.Protocol != GameHostProtocol.Version)
                {
                    return new HostReply(hello.Id, "host.protocol", $"This host speaks protocol {GameHostProtocol.Version}, not {hello.Protocol}.");
                }

                _greeted = true;
                return new HostReply(hello.Id, Status: host.Status());
            }

            if (!_greeted)
            {
                return new HostReply(request.Id, "host.protocol", "Say hello first.");
            }

            if (request is HostStart start)
            {
                return await host.StartAsync(start).ConfigureAwait(false);
            }

            if (request is HostRelease)
            {
                // Answered once the tree is gone, as disposing a local process returns: the agent may delete the
                // server's files right after.
                if (Volatile.Read(ref host._game) is { } running)
                {
                    running.TerminateTree(1);
                    try
                    {
                        await running.Exited.WaitAsync(TimeSpan.FromSeconds(5), host.Clock).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Reported as released anyway; the host ends right after and the job dies with it.
                    }
                }

                return new HostReply(request.Id);
            }

            if (request is HostSubscribe subscribe)
            {
                await _forwarding.ConfigureAwait(false);
                _forwarding = ForwardAsync(subscribe.From);
                return new HostReply(request.Id);
            }

            if (Volatile.Read(ref host._game) is not { } game)
            {
                return new HostReply(request.Id, "host.not_started", "No game was started.");
            }

            try
            {
                switch (request)
                {
                    case HostInput input:
                        await game.WriteInputAsync(input.Text, _closed.Token).ConfigureAwait(false);
                        return new HostReply(request.Id);
                    case HostInterrupt:
                        await game.SendInterruptAsync(_closed.Token).ConfigureAwait(false);
                        return new HostReply(request.Id);
                    case HostTerminate terminate:
                        game.TerminateTree(terminate.ExitCode);
                        return new HostReply(request.Id);
                    case HostLimits limits:
                        game.ApplyLimits(limits.Limits);
                        return new HostReply(request.Id);
                    case HostSample:
                        return new HostReply(request.Id, Usage: game.SampleUsage());
                    default:
                        return new HostReply(request.Id, "host.unknown_request", $"{request.GetType().Name} is not a request this host knows.");
                }
            }
            catch (GameProcessException ex)
            {
                return new HostReply(request.Id, ex.Code, ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                return new HostReply(request.Id, "host.request_failed", ex.Message);
            }
        }

        /// <summary>Output from <paramref name="from"/> on: what the ring still has, then as it comes, until the output ends.</summary>
        private async Task ForwardAsync(long from)
        {
            var cursor = from;
            try
            {
                while (!_closed.IsCancellationRequested)
                {
                    var read = host._output.Read(cursor, GameHostProtocol.MaxOutputCharacters);
                    if (read.Lost is { } lost)
                    {
                        if (!await SendAsync(new HostOutputLost(lost.From, lost.To)).ConfigureAwait(false))
                        {
                            return;
                        }

                        cursor = lost.To;
                    }

                    foreach (var piece in read.Pieces)
                    {
                        if (!await SendAsync(new HostOutput(piece.Offset, piece.At, piece.Text)).ConfigureAwait(false))
                        {
                            return;
                        }

                        cursor = piece.Offset + piece.Text.Length;
                    }

                    if (read.Pieces.Count > 0 || read.Lost is not null)
                    {
                        continue;
                    }

                    if (host._output.Completed && cursor >= host._output.End)
                    {
                        await SendAsync(new HostOutputEnded(cursor)).ConfigureAwait(false);
                        return;
                    }

                    await host._output.WaitAsync(cursor, _closed.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The connection ended.
            }
        }

        /// <summary>False once the connection is gone.</summary>
        public async Task<bool> SendAsync(HostFrame frame)
        {
            try
            {
                await _writeLock.WaitAsync(_closed.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return false;
            }

            try
            {
                await GameHostProtocol.WriteAsync(stream, frame, _closed.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                await _closed.CancelAsync().ConfigureAwait(false);
                return false;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _closed.CancelAsync().ConfigureAwait(false);
            await _forwarding.ConfigureAwait(false);
            _closed.Dispose();
            _writeLock.Dispose();
        }
    }

    public void Dispose() => _released.Dispose();

    private Guid ServerId => serverId;

    private TimeProvider Clock => clock;

    [LoggerMessage(Level = LogLevel.Information, Message = "Host of server {ServerId} listening on pipe {Pipe}")]
    private partial void LogListening(Guid serverId, string pipe);

    [LoggerMessage(Level = LogLevel.Error, Message = "Host of server {ServerId}: the pipe {Pipe} is taken ({Reason}); ending")]
    private partial void LogPipeTaken(Guid serverId, string pipe, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Host of server {ServerId}: no agent connected in time; ending")]
    private partial void LogNobodyCame(Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Host of server {ServerId}: agent connected")]
    private partial void LogConnected(Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Host of server {ServerId}: agent disconnected")]
    private partial void LogDisconnected(Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Host of server {ServerId}: started process {ProcessId}")]
    private partial void LogStarted(Guid serverId, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Host of server {ServerId}: the game could not start: {Code} ({Reason})")]
    private partial void LogStartFailed(Guid serverId, string code, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Host of server {ServerId}: the game could not start")]
    private partial void LogStartFailedUnexpectedly(Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Host of server {ServerId}: the game exited with code {ExitCode} ({Remaining} processes left)")]
    private partial void LogExited(Guid serverId, int exitCode, int remaining);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Host of server {ServerId}: unexpected frame from the agent ({Frame})")]
    private partial void LogUnexpectedFrame(Guid serverId, string frame);

    [LoggerMessage(Level = LogLevel.Information, Message = "Host of server {ServerId}: released; ending")]
    private partial void LogEnded(Guid serverId);
}

/// <summary>The pipes between the agent and its hosts.</summary>
public static class GameHostPipes
{
    /// <summary>
    /// The host's end: open only to the account the host runs as (SYSTEM for the agent's service, the developer in
    /// development), and refused when the name exists already, so nobody can serve it first.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

    /// <summary>The agent's end: it connects only to a pipe served by its own account.</summary>
    public static NamedPipeClientStream CreateClient(string name) =>
        new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
