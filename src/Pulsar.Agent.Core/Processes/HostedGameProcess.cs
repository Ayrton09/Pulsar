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

    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(10);

    private readonly NamedPipeClientStream _pipe;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HostReply>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ConsoleChunk> _output = Channel.CreateBounded<ConsoleChunk>(
        new BoundedChannelOptions(OutputCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private Task _reader = Task.CompletedTask;
    private GameHostStatus _status = null!;
    private int _nextId;
    private int _closed; // 1 once detached or released: the connection ending is expected
    private volatile bool _reachedMemoryLimit;
    private volatile int _remainingAtExit;

    private HostedGameProcess(GameHostReference host, NamedPipeClientStream pipe, ILogger logger)
    {
        Host = host;
        _pipe = pipe;
        _logger = logger;
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
    /// Connects to a host and greets it. <paramref name="verify"/> checks that the pipe is served by the expected
    /// process (the pipe's own security already limits it to the agent's account).
    /// </summary>
    /// <exception cref="GameProcessException">The host cannot be reached or does not speak this protocol.</exception>
    internal static async Task<HostedGameProcess> ConnectAsync(GameHostReference host, Func<PipeStream, GameHostReference, bool> verify,
        TimeSpan timeout, ILogger logger, CancellationToken cancellationToken)
    {
        var pipe = GameHostPipes.CreateClient(host.Pipe);
        HostedGameProcess? process = null;
        try
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connecting.CancelAfter(timeout);
                try
                {
                    await pipe.ConnectAsync(connecting.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new GameProcessException("host.unreachable", "The server's host process does not answer.");
                }
            }

            if (!verify(pipe, host))
            {
                throw new GameProcessException("host.unexpected", "The server's pipe is not served by its host process.");
            }

            process = new HostedGameProcess(host, pipe, logger);
            process._reader = process.ReadAsync();
            var reply = await process.RequestAsync(id => new HostHello(id, GameHostProtocol.Version), cancellationToken).ConfigureAwait(false);
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
    internal Task SubscribeAsync(long from, CancellationToken cancellationToken) =>
        RequestAsync(id => new HostSubscribe(id, from), cancellationToken);

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

    // The interface is synchronous for these (a local Job answers at once); a pipe to a local process does too.
    public int CountProcesses() => SampleUsage().ProcessCount;

    public ProcessUsage SampleUsage()
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return new ProcessUsage(TimeSpan.Zero, 0, 0);
        }

        try
        {
            var reply = RequestAsync(id => new HostSample(id), CancellationToken.None).WaitAsync(s_requestTimeout).GetAwaiter().GetResult();
            return reply.Usage ?? new ProcessUsage(TimeSpan.Zero, 0, 0);
        }
        catch (Exception ex) when (ex is GameProcessException or TimeoutException or OperationCanceledException)
        {
            // Gone (the exit is reported on its own) or busy: nothing this time.
            return Exited.IsCompleted ? new ProcessUsage(TimeSpan.Zero, 0, _remainingAtExit) : throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public void TerminateTree(int exitCode)
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return;
        }

        try
        {
            RequestAsync(id => new HostTerminate(id, exitCode), CancellationToken.None).WaitAsync(s_requestTimeout).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is GameProcessException or TimeoutException or OperationCanceledException)
        {
            LogTerminateFailed(ServerId, ex.Message);
        }
    }

    public void ApplyLimits(ProcessLimits? limits)
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return;
        }

        try
        {
            RequestAsync(id => new HostLimits(id, limits), CancellationToken.None).WaitAsync(s_requestTimeout).GetAwaiter().GetResult();
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

    /// <summary>Releases the host: whatever is left of the game's tree is killed and the host ends.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        // Output nobody reads anymore must not hold up the host's answer behind it.
        _output.Writer.TryComplete();
        try
        {
            await RequestAsync(id => new HostRelease(id), CancellationToken.None).WaitAsync(s_requestTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GameProcessException or TimeoutException or OperationCanceledException)
        {
            // A host that is gone released everything already.
            LogReleaseFailed(ServerId, ex.Message);
        }

        await CloseAsync().ConfigureAwait(false);
    }

    private async Task CloseAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Closed.
        }

        _output.Writer.TryComplete();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new GameProcessException("host.unreachable", "The connection to the server's host is closed."));
        }
    }

    private async Task ControlAsync(Func<int, HostRequest> create, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) == 1 || Exited.IsCompleted)
        {
            throw new InvalidOperationException("The process has exited.");
        }

        try
        {
            await RequestAsync(create, cancellationToken).ConfigureAwait(false);
        }
        catch (GameProcessException ex)
        {
            // As a local process says it: it no longer reads its input (exiting), or the host is gone.
            throw new InvalidOperationException(ex.Message, ex);
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

    /// <summary>Waits while the consumer is behind (back-pressure on the pipe); drops once the output was closed.</summary>
    private async Task PublishAsync(ConsoleChunk chunk)
    {
        while (!_output.Writer.TryWrite(chunk))
        {
            if (!await _output.Writer.WaitToWriteAsync(_closing.Token).ConfigureAwait(false))
            {
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Server {ServerId}: its host was not released cleanly: {Reason}")]
    private partial void LogReleaseFailed(Guid serverId, string reason);
}
