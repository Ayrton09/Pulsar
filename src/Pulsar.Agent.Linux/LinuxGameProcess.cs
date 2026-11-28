using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux;

/// <summary>
/// A running game on Linux: its process (which leads the session and process group of its whole tree, see
/// <see cref="GameExec"/>), its pipes and, when the node has them, its cgroup: its usage, caps and tree are read and
/// killed there; without one, through the session's processes.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxGameProcess : IGameProcess
{
    // Bounded: a consumer that stops reading eventually blocks the game's writes instead of growing memory.
    private const int OutputCapacity = 1024;
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly GameCgroup? _cgroup;
    private readonly Stream _input;
    private readonly TimeProvider _clock;
    private readonly Channel<ConsoleChunk> _output = Channel.CreateBounded<ConsoleChunk>(
        new BoundedChannelOptions(OutputCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _readersDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly Task<int> _exited;
    private readonly string? _environmentFile;
    private readonly PseudoTerminal? _terminal;
    private int _readersRunning;
    private int _disposed;

    /// <param name="terminal">
    /// The game's pseudo terminal (its console is 'pty'): its input goes there and its output comes from there, and from
    /// its pipes what <c>game-exec</c> said before the game had its terminal.
    /// </param>
    internal LinuxGameProcess(Process process, GameCgroup? cgroup, TimeProvider clock, string? environmentFile = null, PseudoTerminal? terminal = null)
    {
        _environmentFile = environmentFile;
        _terminal = terminal;
        _process = process;
        _cgroup = cgroup;
        _clock = clock;
        ProcessId = process.Id;
        StartedAt = clock.GetUtcNow();
        _input = terminal?.OpenWriter() ?? process.StandardInput.BaseStream;
        StartReader(process.StandardOutput.BaseStream, "out");
        StartReader(process.StandardError.BaseStream, "err");
        if (terminal is not null)
        {
            StartReader(terminal.OpenReader(), "tty");
        }

        _exited = WaitForExitAsync();
    }

    public int ProcessId { get; }

    public DateTimeOffset StartedAt { get; }

    public Task<int> Exited => _exited;

    public ChannelReader<ConsoleChunk> Output => _output.Reader;

    /// <summary>Ctrl+C is SIGINT to the game's process group, as a terminal sends it.</summary>
    public bool SupportsInterrupt => true;

    public ValueTask WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ValueTask(WriteAsync(s_utf8.GetBytes(text), cancellationToken));
    }

    public ValueTask SendInterruptAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_exited.IsCompleted)
        {
            throw new InvalidOperationException("The process has exited.");
        }

        // The group the game leads (its main process is running, so the group's id is still its own).
        _ = Libc.kill(-ProcessId, Libc.SigInt);
        return ValueTask.CompletedTask;
    }

    public int CountProcesses()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return 0;
        }

        return _cgroup is { } cgroup ? cgroup.Processes().Count : ProcFs.InSession(ProcessId).Count;
    }

    public ProcessUsage SampleUsage()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return new ProcessUsage(TimeSpan.Zero, 0, 0);
        }

        if (_cgroup is { } cgroup)
        {
            return cgroup.Usage();
        }

        // Without a cgroup: the session's live processes, and what the main one's ended children used.
        long ticks = 0, pages = 0;
        var members = ProcFs.InSession(ProcessId);
        foreach (var (pid, stat) in members)
        {
            ticks += stat.UserTicks + stat.SystemTicks + (pid == ProcessId ? stat.ChildrenUserTicks + stat.ChildrenSystemTicks : 0);
            pages += stat.ResidentPages;
        }

        var memory = pages * ProcFs.PageSize;
        return new ProcessUsage(TimeSpan.FromSeconds((double)ticks / ProcFs.TicksPerSecond), memory, members.Count, memory);
    }

    public void TerminateTree(int exitCode)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            KillTree();
        }
    }

    private void KillTree()
    {
        if (_cgroup is { } cgroup)
        {
            // Its cgroup holds the whole tree, and nothing else: it is killed whole. The main process too while it runs:
            // until game-exec joins the cgroup (its runtime starting) it is not in it yet.
            cgroup.Kill();
            if (!_exited.IsCompleted)
            {
                _ = Libc.kill(ProcessId, Libc.SigKill);
            }

            return;
        }

        // Without one (a development agent): the group, then each process of the session (one that made a group of its
        // own), then the main one (before it made its group). Only while the main process runs: once it is gone (and
        // reaped) its ids may be another process's, and what it left of its tree is left (no cgroup to tell it apart).
        if (_exited.IsCompleted)
        {
            return;
        }

        _ = Libc.kill(-ProcessId, Libc.SigKill);
        foreach (var (pid, _) in ProcFs.InSession(ProcessId))
        {
            _ = Libc.kill(pid, Libc.SigKill);
        }

        _ = Libc.kill(ProcessId, Libc.SigKill);
    }

    public void ApplyLimits(ProcessLimits? limits)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (_cgroup is not { } cgroup)
        {
            if (limits is not null)
            {
                throw new GameProcessException("process.limits_failed", "This node cannot cap a server's CPU or memory: it has no cgroups for its games.");
            }

            return;
        }

        try
        {
            cgroup.ApplyLimits(limits);
        }
        catch (IOException ex)
        {
            throw new GameProcessException("process.limits_failed", $"The server's limits could not be applied: {ex.Message}", ex);
        }
    }

    public bool ReachedMemoryLimit => _cgroup?.ReachedMemoryLimit ?? false;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Nothing outlives the object that represents it.
        KillTree();
        try
        {
            await _exited.WaitAsync(s_closeTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        if (await _inputLock.WaitAsync(s_closeTimeout).ConfigureAwait(false))
        {
            _inputLock.Release();
        }

        try
        {
            _input.Dispose();
        }
        catch (IOException)
        {
            // The game was gone with data unread.
        }

        if (await Task.WhenAny(_readersDone.Task, Task.Delay(s_closeTimeout)).ConfigureAwait(false) != _readersDone.Task)
        {
            _output.Writer.TryComplete();
        }

        DeleteEnvironmentFile();
        _terminal?.Dispose();
        _cgroup?.Dispose();
        _process.Dispose();
    }

    private async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        // game-exec deletes the game's environment as it reads it; one that ended before (or was killed) leaves it.
        DeleteEnvironmentFile();
        // Reading the terminal ends once nothing of the game holds its slave any more (EIO), this side's copy included.
        _terminal?.CloseSlave();
        return _process.ExitCode;
    }

    private void DeleteEnvironmentFile()
    {
        if (_environmentFile is null)
        {
            return;
        }

        try
        {
            File.Delete(_environmentFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone already, or the temporary folder is unreachable: nothing more to do for it.
        }
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        ThrowIfExited();
        await _inputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfExited();
            await _input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new InvalidOperationException("The process no longer reads its input.", ex);
        }
        finally
        {
            _inputLock.Release();
        }
    }

    private void ThrowIfExited()
    {
        if (Volatile.Read(ref _disposed) == 1 || _exited.IsCompleted)
        {
            throw new InvalidOperationException("The process has exited.");
        }
    }

    private void StartReader(Stream stream, string name)
    {
        Interlocked.Increment(ref _readersRunning);
        new Thread(() => Read(stream)) { IsBackground = true, Name = $"pulsar-output-{ProcessId}-{name}" }.Start();
    }

    private void Read(Stream stream)
    {
        var decoder = s_utf8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[s_utf8.GetMaxCharCount(bytes.Length)];
        try
        {
            int read;
            while ((read = stream.Read(bytes)) > 0)
            {
                // Stateful: a UTF-8 sequence split across two reads is decoded once both halves arrived.
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count > 0 && !Publish(new string(chars, 0, count)))
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed.
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

    /// <summary>False when the output was completed meanwhile (disposed with nobody reading).</summary>
    private bool Publish(string text)
    {
        try
        {
            _output.Writer.WriteAsync(new ConsoleChunk(_clock.GetUtcNow(), text)).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }
}
