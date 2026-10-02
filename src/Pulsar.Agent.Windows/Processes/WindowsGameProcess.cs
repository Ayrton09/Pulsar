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
    private readonly Lock _inputLock = new();
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
            _exited.TrySetResult(PInvoke.GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public ValueTask WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = _inputEncoding.GetBytes(text);
        return Write(bytes);
    }

    public ValueTask SendInterruptAsync(CancellationToken cancellationToken)
    {
        if (!SupportsInterrupt)
        {
            throw new NotSupportedException("Ctrl+C needs a pseudo console.");
        }

        // A pseudo console turns ETX into CTRL_C_EVENT for the attached processes.
        return Write([0x03]);
    }

    public int CountProcesses() => Volatile.Read(ref _disposed) == 1 ? 0 : _job.ActiveProcesses;

    public void TerminateTree(int exitCode)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _job.Terminate(unchecked((uint)exitCode));
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

        lock (_inputLock)
        {
            _input.Dispose();
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

    private ValueTask Write(byte[] bytes)
    {
        lock (_inputLock)
        {
            if (Volatile.Read(ref _disposed) == 1 || _exited.Task.IsCompleted)
            {
                throw new InvalidOperationException("The process has exited.");
            }

            try
            {
                _input.Write(bytes);
                _input.Flush();
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("The process no longer reads its input.", ex);
            }
        }

        return ValueTask.CompletedTask;
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
