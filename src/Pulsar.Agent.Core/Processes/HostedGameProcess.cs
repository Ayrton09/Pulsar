using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Pulsar.Agent.Core.Processes;

/// <summary>A game started under a host process: it can be left running when the agent stops.</summary>
public interface IHostedGameProcess : IGameProcess
{
    GameHostReference Host { get; }

    /// <summary>
    /// Lets go of the game without stopping it: the connection closes and the host keeps running it (and its output)
    /// until an agent connects again. Nothing else may be called afterwards.
    /// </summary>
    ValueTask DetachAsync();
}

/// <summary>
/// The agent's side of a server's host (docs/architecture/06, "Server hosts"): an <see cref="IGameProcess"/> whose
/// requests travel over the host's pipe. Output arrives with its offsets (<see cref="ConsoleChunk.End"/>), so the agent
/// can ask for the rest after a restart. If the connection breaks without the agent asking (the host died), the game
/// died with it: the process counts as exited.
/// </summary>
public sealed partial class HostedGameProcess : IHostedGameProcess
{
    // Bounded: a consumer that stops reading pushes back on the pipe; the host keeps buffering in its ring.
    private const int OutputCapacity = 1024;

    /// <summary>The exit code reported when the host itself went away (and took the game with it).</summary>
    public const int HostLostExitCode = -1;

    private readonly NamedPipeClientStream _pipe;
    private readonly GameHostManager _manager;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HostReply>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationTokenSource _unread = new(); // cancelled once output nobody may read must not wait for room
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ConsoleChunk> _output = Channel.CreateBounded<ConsoleChunk>(
        new BoundedChannelOptions(OutputCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private Task _reader = Task.CompletedTask;
    private GameHostStatus _status = null!;
    private int _nextId;
    private int _closed; // 1 once detached or released: the connection ending is expected
    private int _disconnected; // 1 once the connection is closed and its resources disposed
    private volatile bool _reachedMemoryLimit;
    private volatile int _remainingAtExit;

    private HostedGameProcess(GameHostReference host, NamedPipeClientStream pipe, GameHostManager manager)
    {
        Host = host;
        _pipe = pipe;
        _manager = manager;
        _logger = manager.Logger;
    }

    public GameHostReference Host { get; }

    public Guid ServerId => _status.ServerId;

    /// <summary>What the host said when the agent connected.</summary>
    public GameHostStatus Status => _status;

    public int ProcessId => _status.ProcessId ?? 0;

    public DateTimeOffset StartedAt => _status.StartedAt ?? DateTimeOffset.MinValue;

    public Task<int> Exited => _exited.Task;

    public ChannelReader<ConsoleChunk> Output => _output.Reader;

    public bool SupportsInterrupt => _status.SupportsInterrupt;

    public bool ReachedMemoryLimit => _reachedMemoryLimit;

    /// <summary>
    /// Connects to a host and greets it: within <paramref name="connectTimeout"/> for its pipe, then within
    /// <paramref name="answerTimeout"/> for its answer. The manager checks that the pipe is served by the expected process
    /// (the pipe's own security already limits it to the agent's account).
    /// </summary>
    /// <exception cref="GameProcessException">
    /// The host cannot be reached or does not answer (<c>host.unreachable</c>), is not the process that should serve the
    /// pipe (<c>host.unexpected</c>), or runs another version of the agent's protocol (<c>host.incompatible</c>).
    /// </exception>
    internal static async Task<HostedGameProcess> ConnectAsync(GameHostManager manager, GameHostReference host, TimeSpan connectTimeout,
        TimeSpan answerTimeout, CancellationToken cancellationToken)
    {
        var pipe = GameHostPipes.CreateClient(host.Pipe);
        HostedGameProcess? process = null;
        try
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connecting.CancelAfter(connectTimeout);
                try
                {
                    await pipe.ConnectAsync(connecting.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new GameProcessException("host.unreachable", "The server's host process does not answer.");
                }
            }

            if (!manager.Serves(pipe, host))
            {
                throw new GameProcessException("host.unexpected", "The server's pipe is not served by its host process.");
            }

            process = new HostedGameProcess(host, pipe, manager);
            process._reader = process.ReadAsync();
            HostReply reply;
            using (var answering = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // A host that accepts the connection but never answers must not hold up the agent's start.
                answering.CancelAfter(answerTimeout);
                try
                {
                    reply = await process.RequestAsync(id => new HostHello(id, GameHostProtocol.Version), answering.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new GameProcessException("host.unreachable", "The server's host process does not answer.");
                }
                catch (GameProcessException ex) when (ex.Code == "host.protocol")
                {
                    throw new GameProcessException("host.incompatible", $"The server's host process runs another version of the agent: {ex.Message}", ex);
                }
            }

            process._status = reply.Status ?? throw new GameProcessException("host.protocol", "The host did not say how its game is.");
            if (process._status.ExitCode is { } code)
            {
                // The game exited while no agent was connected: reported now, as if it just happened.
                process._reachedMemoryLimit = process._status.ReachedMemoryLimit;
                process._exited.TrySetResult(code);
            }

            return process;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            await DisposeQuietlyAsync(process, pipe).ConfigureAwait(false);
            throw new GameProcessException("host.unreachable", $"The server's host process cannot be reached: {ex.Message}", ex);
        }
        catch
        {
            await DisposeQuietlyAsync(process, pipe).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeQuietlyAsync(HostedGameProcess? process, NamedPipeClientStream pipe)
    {
        if (process is not null)
        {
            await process.CloseAsync().ConfigureAwait(false);
        }
        else
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Starts the game in a new host.</summary>
    /// <exception cref="GameProcessException">The host could not start it (its code, as a local start would give).</exception>
    internal async Task StartGameAsync(GameHostLaunch launch, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(id => new HostStart(id, launch), cancellationToken).ConfigureAwait(false);
        _status = reply.Status ?? throw new GameProcessException("host.protocol", "The host did not say how its game is.");
    }

    /// <summary>Output from <paramref name="from"/> on (0 for everything the host still has).</summary>
    internal async Task SubscribeAsync(long from, CancellationToken cancellationToken)
    {
        try
        {
            await RequestAsync(id => new HostSubscribe(id, from), cancellationToken).WaitAsync(_manager.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new GameProcessException("host.unreachable", "The server's host process does not answer.", ex);
        }
    }

    public async ValueTask WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        await ControlAsync(id => new HostInput(id, text), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendInterruptAsync(CancellationToken cancellationToken)
    {
        if (!SupportsInterrupt)
        {
            throw new NotSupportedException("Ctrl+C needs a pseudo console.");
        }

        await ControlAsync(id => new HostInterrupt(id), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Once the game exited, what was left of its tree then (the host said so with the exit): no request, so handling an
    /// exit never waits for a host.
    /// </summary>
    public int CountProcesses() => Exited.IsCompleted ? _remainingAtExit : SampleUsage().ProcessCount;

    // The synchronous forms wait for the asynchronous ones (tests; the agent's supervisor uses the asynchronous ones).
    public ProcessUsage SampleUsage() => SampleUsageAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public void TerminateTree(int exitCode) => TerminateTreeAsync(exitCode, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public void ApplyLimits(ProcessLimits? limits) => ApplyLimitsAsync(limits, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <exception cref="InvalidOperationException">The host did not answer in time (or is gone while the game had not exited).</exception>
    public async ValueTask<ProcessUsage> SampleUsageAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return new ProcessUsage(TimeSpan.Zero, 0, 0);
        }

        try
        {
            var reply = await RequestWithinAsync(id => new HostSample(id), cancellationToken).ConfigureAwait(false);
            return reply.Usage ?? new ProcessUsage(TimeSpan.Zero, 0, 0);
        }
        catch (Exception ex) when (ex is GameProcessException or TimeoutException)
        {
            // Gone (the exit is reported on its own) or busy: nothing this time.
            return Exited.IsCompleted ? new ProcessUsage(TimeSpan.Zero, 0, _remainingAtExit) : throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Asks the host to kill the game's tree. A host that does not answer in time (or refuses) is killed itself: its Job
    /// closes and the game dies with it, and the connection's end reports the exit. Never fails: a kill must kill.
    /// </summary>
    public async ValueTask TerminateTreeAsync(int exitCode, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return;
        }

        try
        {
            await RequestWithinAsync(id => new HostTerminate(id, exitCode), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GameProcessException or TimeoutException)
        {
            if (Volatile.Read(ref _closed) == 0 && !Exited.IsCompleted && _manager.Kill(Host))
            {
                LogHostKilled(ServerId, Host.ProcessId, ex.Message);
            }
            else
            {
                LogTerminateFailed(ServerId, ex.Message);
            }
        }
    }

    public async ValueTask ApplyLimitsAsync(ProcessLimits? limits, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return;
        }

        try
        {
            await RequestWithinAsync(id => new HostLimits(id, limits), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new GameProcessException("process.limits_failed", "The server's host did not apply the limits in time.", ex);
        }
    }

    public async ValueTask DetachAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        LogDetached(ServerId, Host.ProcessId);
        _output.Writer.TryComplete();
        await CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the host: whatever is left of the game's tree is killed and the host ends. The output stays open until
    /// the host answers, since it sends the last of the game's output first (what a pseudo console shows as it closes).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        // Output nobody may read anymore must not hold up the host's answer behind it: it is dropped when there is no room.
        await _unread.CancelAsync().ConfigureAwait(false);
        try
        {
            await RequestAsync(id => new HostRelease(id), CancellationToken.None).WaitAsync(_manager.RequestTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GameProcessException or TimeoutException or OperationCanceledException)
        {
            // A host that is gone released everything already; one that is stuck is ended, its game with it, rather
            // than left running with nobody to watch it.
            if (_manager.Kill(Host))
            {
                LogReleaseFailed(ServerId, Host.ProcessId, ex.Message);
            }
        }

        await CloseAsync().ConfigureAwait(false);
    }

    private async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 1)
        {
            return;
        }

        await _closing.CancelAsync().ConfigureAwait(false);
        await _unread.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Closed (the reader already reported anything unexpected).
        }

        _output.Writer.TryComplete();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new GameProcessException("host.unreachable", "The connection to the server's host is closed."));
        }

        // Requests made from other threads meanwhile fail as an unreachable host (RequestAsync).
        _writeLock.Dispose();
        _closing.Dispose();
        _unread.Dispose();
    }

    private async Task ControlAsync(Func<int, HostRequest> create, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) == 1 || Exited.IsCompleted)
        {
            throw new InvalidOperationException("The process has exited.");
        }

        try
        {
            await RequestWithinAsync(create, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GameProcessException or TimeoutException)
        {
            // As a local process says it: it no longer reads its input (exiting, or frozen), or the host is gone.
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>A request whose answer is awaited at most <see cref="GameHostManager.RequestTimeout"/>.</summary>
    /// <exception cref="TimeoutException">No answer in time.</exception>
    private async Task<HostReply> RequestWithinAsync(Func<int, HostRequest> create, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_manager.RequestTimeout);
        try
        {
            return await RequestAsync(create, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The server's host process did not answer in time.", ex);
        }
    }

    /// <exception cref="GameProcessException">The host refused (its code) or cannot be reached.</exception>
    private async Task<HostReply> RequestAsync(Func<int, HostRequest> create, CancellationToken cancellationToken)
    {
        var request = create(Interlocked.Increment(ref _nextId));
        var answer = new TaskCompletionSource<HostReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = answer;
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await GameHostProtocol.WriteAsync(_pipe, request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            var reply = await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return reply.ErrorCode is { } code ? throw new GameProcessException(code, reply.Error ?? code) : reply;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new GameProcessException("host.unreachable", $"The server's host cannot be reached: {ex.Message}", ex);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private async Task ReadAsync()
    {
        var expected = false;
        try
        {
            while (true)
            {
                var frame = await GameHostProtocol.ReadAsync(_pipe, _closing.Token).ConfigureAwait(false);
                switch (frame)
                {
                    case null:
                        return;
                    case HostReply reply:
                        if (_pending.TryGetValue(reply.Id, out var waiting))
                        {
                            waiting.TrySetResult(reply);
                        }

                        break;
                    case HostOutput output:
                        await PublishAsync(new ConsoleChunk(output.At, output.Text, output.Offset + output.Text.Length)).ConfigureAwait(false);
                        break;
                    case HostOutputLost lost:
                        // The host kept less than came while no agent was reading: the console says so where it happened.
                        await PublishAsync(new ConsoleChunk(DateTimeOffset.UtcNow,
                            $"\u001b[2m[Pulsar] {lost.To - lost.From} characters of output were lost while the agent was away.\u001b[0m\r\n",
                            lost.To)).ConfigureAwait(false);
                        break;
                    case HostOutputEnded:
                        _output.Writer.TryComplete();
                        break;
                    case HostExited exited:
                        _reachedMemoryLimit = exited.ReachedMemoryLimit;
                        _remainingAtExit = exited.RemainingProcesses;
                        _exited.TrySetResult(exited.ExitCode);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            expected = Volatile.Read(ref _closed) == 1;
            if (ex is InvalidDataException)
            {
                LogProtocolError(Host.ProcessId, ex.Message);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never left to fault the reader: the connection counts as broken.
            LogProtocolError(Host.ProcessId, ex.Message);
        }
        finally
        {
            expected |= Volatile.Read(ref _closed) == 1;
            _output.Writer.TryComplete();
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new GameProcessException("host.unreachable", "The connection to the server's host closed."));
            }

            if (!expected && !_exited.Task.IsCompleted)
            {
                // The host ended without being asked: its job closed and the game ended with it.
                LogHostLost(Host.ProcessId);
                _exited.TrySetResult(HostLostExitCode);
            }
        }
    }

    /// <summary>
    /// Waits while the consumer is behind (back-pressure on the pipe); drops once the output was closed, or when there is
    /// no room once the host is being released (the consumer may be gone).
    /// </summary>
    private async Task PublishAsync(ConsoleChunk chunk)
    {
        while (!_output.Writer.TryWrite(chunk))
        {
            try
            {
                if (!await _output.Writer.WaitToWriteAsync(_unread.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (_unread.IsCancellationRequested)
            {
                _ = _output.Writer.TryWrite(chunk);
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host process {HostProcessId} of a server ended unexpectedly; its game ended with it")]
    private partial void LogHostLost(int hostProcessId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host process {HostProcessId} sent something unexpected: {Reason}")]
    private partial void LogProtocolError(int hostProcessId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: left running in its host process {HostProcessId}")]
    private partial void LogDetached(Guid serverId, int hostProcessId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: its host could not kill its processes: {Reason}")]
    private partial void LogTerminateFailed(Guid serverId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: its host process {HostProcessId} did not kill its game ({Reason}); killed the host, and the game with it")]
    private partial void LogHostKilled(Guid serverId, int hostProcessId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: its host process {HostProcessId} did not end when released ({Reason}); killed it")]
    private partial void LogReleaseFailed(Guid serverId, int hostProcessId, string reason);
}
