using System.Collections.Concurrent;
using System.Threading.Channels;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>A process manager whose processes the test drives: output, exit, children.</summary>
internal sealed class FakeProcessManager : IGameProcessManager
{
    private readonly ConcurrentQueue<FakeGameProcess> _started = new();

    public List<ProcessLaunchSpec> Specs { get; } = [];

    /// <summary>When set, the next start fails with this error.</summary>
    public Exception? FailNext { get; set; }

    /// <summary>When set, starts wait for it (a slow first logon, say).</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Scripts each started process (its output, its exit).</summary>
    public Action<FakeGameProcess, ProcessLaunchSpec>? OnStarted { get; set; }

    public IReadOnlyList<FakeGameProcess> Started => [.. _started];

    public FakeGameProcess Last => _started.Last();

    /// <summary>How many times stray processes of the server's account were swept.</summary>
    public int StraySweeps { get; private set; }

    public int TerminateStrayProcesses(ProcessIdentity identity)
    {
        StraySweeps++;
        return 0;
    }

    public async Task<IGameProcess> StartAsync(ProcessLaunchSpec spec, CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        lock (Specs)
        {
            Specs.Add(spec);
        }

        if (FailNext is { } error)
        {
            FailNext = null;
            throw error;
        }

        var process = new FakeGameProcess(_started.Count + 1000);
        _started.Enqueue(process);
        OnStarted?.Invoke(process, spec);
        return process;
    }
}

internal sealed class FakeGameProcess(int processId) : IGameProcess
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ConsoleChunk> _output = Channel.CreateUnbounded<ConsoleChunk>();
    private readonly ConcurrentQueue<string> _input = new();

    public int ProcessId { get; } = processId;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public Task<int> Exited => _exited.Task;

    public ChannelReader<ConsoleChunk> Output => _output.Reader;

    public bool SupportsInterrupt => true;

    public int Children { get; set; }

    public int Interrupts { get; private set; }

    public bool Disposed { get; private set; }

    public int? TerminatedWith { get; private set; }

    /// <summary>When true, input is recorded but the process does not exit by itself on "quit".</summary>
    public bool IgnoreQuit { get; set; }

    public IReadOnlyList<string> Input => [.. _input];

    /// <summary>
    /// When set, the output stays open after the exit and this is its last text, written when the process is disposed:
    /// a pseudo console shows its final screen as it closes.
    /// </summary>
    public string? LastScreen { get; set; }

    public void Print(string text) => _output.Writer.TryWrite(new ConsoleChunk(DateTimeOffset.UtcNow, text));

    public void Exit(int code)
    {
        _exited.TrySetResult(code);
        if (LastScreen is null)
        {
            _output.Writer.TryComplete();
        }
    }

    public ValueTask WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        if (_exited.Task.IsCompleted)
        {
            throw new InvalidOperationException("The process has exited.");
        }

        _input.Enqueue(text);
        if (text.StartsWith("quit", StringComparison.Ordinal) && !IgnoreQuit)
        {
            Exit(0);
        }

        OnInput?.Invoke(text);
        return ValueTask.CompletedTask;
    }

    /// <summary>How the game answers what it is sent (a save command's confirmation).</summary>
    public Action<string>? OnInput { get; set; }

    public ValueTask SendInterruptAsync(CancellationToken cancellationToken)
    {
        Interrupts++;
        return ValueTask.CompletedTask;
    }

    public int CountProcesses() => Disposed || TerminatedWith is not null ? 0 : Children + (_exited.Task.IsCompleted ? 0 : 1);

    /// <summary>What <see cref="SampleUsage"/> reports.</summary>
    public ProcessUsage Usage { get; set; } = new(TimeSpan.Zero, 0, 1);

    public ProcessUsage SampleUsage() => Disposed ? new ProcessUsage(TimeSpan.Zero, 0, 0) : Usage;

    public void TerminateTree(int exitCode)
    {
        TerminatedWith = exitCode;
        Children = 0;
        Exit(exitCode);
    }

    /// <summary>The caps applied while it runs (the start's caps are in its <see cref="ProcessLaunchSpec"/>).</summary>
    public List<ProcessLimits?> AppliedLimits { get; } = [];

    public void ApplyLimits(ProcessLimits? limits) => AppliedLimits.Add(limits);

    public bool ReachedMemoryLimit { get; set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Exit(1);
        if (LastScreen is { } last)
        {
            Print(last);
        }

        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A fake game as if it ran in a host process (the supervisor keeps its host's state for the next agent).</summary>
internal sealed class FakeHostedProcess(FakeGameProcess game, GameHostReference host) : IHostedGameProcess
{
    public GameHostReference Host { get; } = host;

    public bool Detached { get; private set; }

    public int ProcessId => game.ProcessId;

    public DateTimeOffset StartedAt => game.StartedAt;

    public Task<int> Exited => game.Exited;

    public ChannelReader<ConsoleChunk> Output => game.Output;

    public bool SupportsInterrupt => game.SupportsInterrupt;

    public bool ReachedMemoryLimit => game.ReachedMemoryLimit;

    public ValueTask WriteInputAsync(string text, CancellationToken cancellationToken) => game.WriteInputAsync(text, cancellationToken);

    public ValueTask SendInterruptAsync(CancellationToken cancellationToken) => game.SendInterruptAsync(cancellationToken);

    public int CountProcesses() => game.CountProcesses();

    public ProcessUsage SampleUsage() => game.SampleUsage();

    public void TerminateTree(int exitCode) => game.TerminateTree(exitCode);

    public void ApplyLimits(ProcessLimits? limits) => game.ApplyLimits(limits);

    public ValueTask DetachAsync()
    {
        Detached = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => game.DisposeAsync();
}

/// <summary>Collects a supervisor's events and waits for them.</summary>
internal sealed class RecordingEvents : IServerEventSink
{
    private readonly List<ServerStateChanged> _changes = [];

    public bool? DesiredRunning { get; private set; }

    public IReadOnlyList<ServerStateChanged> Changes
    {
        get
        {
            lock (_changes)
            {
                return [.. _changes];
            }
        }
    }

    public void StateChanged(ServerStateChanged change)
    {
        lock (_changes)
        {
            _changes.Add(change);
        }
    }

    public void DesiredChanged(Guid serverId, bool running) => DesiredRunning = running;

    /// <summary>Waits for the <paramref name="count"/>-th change (1-based) to be <paramref name="state"/>.</summary>
    public async Task<ServerStateChanged> WaitForAsync(RuntimeState state, StateChangeReason? reason = null, int afterIndex = -1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var match = Changes.Skip(afterIndex + 1).FirstOrDefault(c => c.State == state && (reason is null || c.Reason == reason));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"No {state} ({reason}) change. Changes: {string.Join(", ", Changes.Select(c => $"{c.State}/{c.Reason}"))}");
    }
}
