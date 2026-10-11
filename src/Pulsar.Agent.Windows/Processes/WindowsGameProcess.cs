using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;
using Windows.Win32;
using Windows.Win32.System.Console;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// A running server process: the main process handle, its Job, the console (pseudo console or pipes) and whatever
/// must live as long as the process (the logged-on account and its window station).
/// </summary>
internal sealed class WindowsGameProcess : IGameProcess
{
    // Bounded: a consumer that stops reading eventually blocks the process's writes instead of growing memory.
    private const int OutputCapacity = 1024;

    private readonly JobObject _job;
    private readonly SafeFileHandle _process;
    private readonly FileStream _input;
    private readonly Encoding _inputEncoding;
    private readonly HPCON _pseudoConsole;
    private readonly IDisposable[] _resources;
    private readonly Channel<ConsoleChunk> _output = Channel.CreateBounded<ConsoleChunk>(
        new BoundedChannelOptions(OutputCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource _readersDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _inputLock = new(1, 1); // held until a write ends, even one its caller gave up waiting for
    private readonly TimeProvider _clock;
    private RegisteredWaitHandle? _wait;
    private ProcessWaitHandle? _waitHandle;
    private int _readersRunning;
    private int _disposed;

    internal WindowsGameProcess(JobObject job, SafeFileHandle process, int processId, FileStream input, Encoding inputEncoding,
        HPCON pseudoConsole, TimeProvider clock, IDisposable[] resources)
    {
        _job = job;
        _process = process;
        _input = input;
        _inputEncoding = inputEncoding;
        _pseudoConsole = pseudoConsole;
        _clock = clock;
        _resources = resources;
        ProcessId = processId;
        StartedAt = clock.GetUtcNow();
    }

    public int ProcessId { get; }

    public DateTimeOffset StartedAt { get; }

    public Task<int> Exited => _exited.Task;

    public ChannelReader<ConsoleChunk> Output => _output.Reader;

    public bool SupportsInterrupt => _pseudoConsole != default;

    internal void StartReader(SafeFileHandle handle, Encoding encoding, string name)
    {
        var stream = new FileStream(handle, FileAccess.Read, 1);
        Interlocked.Increment(ref _readersRunning);
        new Thread(() => Read(stream, encoding)) { IsBackground = true, Name = $"pulsar-output-{ProcessId}-{name}" }.Start();
    }

    /// <summary>Waits on the process handle without blocking a thread (a thread-pool wait registration).</summary>
    internal void WatchExit()
    {
        _waitHandle = new ProcessWaitHandle(_process);
        _wait = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) =>
        {
            int code;
            try
            {
                code = PInvoke.GetExitCodeProcess(_process, out var exitCode) ? unchecked((int)exitCode) : -1;
            }
            catch (ObjectDisposedException)
            {
                // Run after DisposeAsync closed the handle (unregistering does not wait for a callback already queued).
                code = -1;
            }

            _exited.TrySetResult(code);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public ValueTask WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = _inputEncoding.GetBytes(text);
        return new ValueTask(WriteAsync(bytes, cancellationToken));
    }

    public ValueTask SendInterruptAsync(CancellationToken cancellationToken)
    {
        if (!SupportsInterrupt)
        {
            throw new NotSupportedException("Ctrl+C needs a pseudo console.");
        }

        // A pseudo console turns ETX into CTRL_C_EVENT for the attached processes.
        return new ValueTask(WriteAsync([0x03], cancellationToken));
    }

    public int CountProcesses() => Volatile.Read(ref _disposed) == 1 ? 0 : _job.ActiveProcesses;

    public ProcessUsage SampleUsage() => Volatile.Read(ref _disposed) == 1 ? new ProcessUsage(TimeSpan.Zero, 0, 0) : _job.Usage();

    public void TerminateTree(int exitCode)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _job.Terminate(unchecked((uint)exitCode));
        }
    }

    public bool ReachedMemoryLimit => _job.ReachedMemoryLimit;

    public void ApplyLimits(ProcessLimits? limits)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            _job.SetLimits(limits);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GameProcessException("process.limits_failed", $"The server's limits could not be applied: {ex.Message}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Nothing outlives the object that represents it: whatever is still running dies with the job.
        _job.Terminate(1);
        try
        {
            await _exited.Task.WaitAsync(s_closeTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The wait registration is released below either way.
        }

        // A write the game never took is cancelled (its reader may be the pseudo console, still open), so its thread
        // ends; the input closes once it has. No write starts anymore (they check _disposed holding the lock).
        var deadline = Environment.TickCount64 + (long)s_closeTimeout.TotalMilliseconds;
        bool idle;
        while (!(idle = await _inputLock.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false)) && Environment.TickCount64 < deadline)
        {
            CancelPendingWrite();
        }

        _input.Dispose();
        if (idle)
        {
            _inputLock.Release(); // writes waiting for it learn the process is gone
        }

        // The readers keep draining while the pseudo console closes, so closing cannot block on a full pipe; if the
        // consumer stopped reading, the output channel is completed and the rest is dropped.
        var closing = _pseudoConsole != default ? Task.Run(() => PInvoke.ClosePseudoConsole(_pseudoConsole)) : Task.CompletedTask;
        var drained = Task.WhenAll(closing, _readersDone.Task);
        if (await Task.WhenAny(drained, Task.Delay(s_closeTimeout)).ConfigureAwait(false) != drained)
        {
            _output.Writer.TryComplete();
            await Task.WhenAny(drained, Task.Delay(s_closeTimeout)).ConfigureAwait(false);
        }

        _wait?.Unregister(null);
        _waitHandle?.Dispose();
        _process.Dispose();
        _job.Dispose();
        foreach (var resource in _resources)
        {
            resource.Dispose();
        }
    }

    /// <summary>
    /// Writes on a thread of its own: the pipe is synchronous, and a game that stops reading its input blocks the write
    /// until it reads again. The caller can give up waiting; the write itself goes on (and holds the input, so writes
    /// never mix) until the game reads it, exits, or the process is disposed.
    /// </summary>
    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        ThrowIfExited();
        await _inputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task write;
        try
        {
            ThrowIfExited();
            write = Task.Run(() =>
            {
                try
                {
                    _input.Write(bytes);
                    _input.Flush();
                }
                finally
                {
                    _inputLock.Release();
                }
            }, CancellationToken.None);
        }
        catch
        {
            _inputLock.Release();
            throw;
        }

        try
        {
            await write.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Broken pipe (the game exited), closed input, or a write cancelled by DisposeAsync.
            throw new InvalidOperationException("The process no longer reads its input.", ex);
        }
    }

    private void ThrowIfExited()
    {
        if (Volatile.Read(ref _disposed) == 1 || _exited.Task.IsCompleted)
        {
            throw new InvalidOperationException("The process has exited.");
        }
    }

    private unsafe void CancelPendingWrite()
    {
        try
        {
            // Every I/O of this process on the handle, synchronous ones of other threads included.
            PInvoke.CancelIoEx(_input.SafeFileHandle, null);
        }
        catch (ObjectDisposedException)
        {
            // Closed already.
        }
    }

    private void Read(FileStream stream, Encoding encoding)
    {
        var decoder = encoding.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        try
        {
            int read;
            while ((read = stream.Read(bytes)) > 0)
            {
                // Stateful: a UTF-8 sequence split across two reads is decoded once both halves arrived.
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count > 0)
                {
                    Publish(new ConsoleChunk(_clock.GetUtcNow(), new string(chars, 0, count)));
                }
            }
        }
        catch (IOException)
        {
            // The other end closed (process gone or pseudo console closed).
        }
        finally
        {
            stream.Dispose();
            if (Interlocked.Decrement(ref _readersRunning) == 0)
            {
                _output.Writer.TryComplete();
                _readersDone.TrySetResult();
            }
        }
    }

    private void Publish(ConsoleChunk chunk)
    {
        while (!_output.Writer.TryWrite(chunk))
        {
            // Dedicated thread: blocking here is the back-pressure. Completed by DisposeAsync: drop and keep draining.
            if (!_output.Writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
            {
                return;
            }
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeFileHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
