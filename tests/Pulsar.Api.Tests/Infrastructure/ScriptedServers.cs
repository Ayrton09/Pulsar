using System.Collections.Concurrent;
using System.Threading.Channels;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// Game processes for the in-process agent, which runs on any OS: each one behaves like the test game server (it says
/// it is ready, quits on "quit", dies when its tree is killed). The real Windows processes are covered by
/// Pulsar.Agent.Windows.Tests and the Windows end-to-end run.
/// </summary>
public sealed class ScriptedProcessManager : IGameProcessManager
{
    private readonly ConcurrentQueue<ScriptedProcess> _started = new();

    public IReadOnlyList<ScriptedProcess> Started => [.. _started];

    public Task<IGameProcess> StartAsync(ProcessLaunchSpec spec, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(spec.ServerRoot, spec.Executable)))
        {
            throw new GameProcessException("process.executable_missing", $"{spec.Executable} does not exist in the server directory.");
        }

        var process = new ScriptedProcess(_started.Count + 100);
        _started.Enqueue(process);
        process.Print("Test server ready\r\n");
        return Task.FromResult<IGameProcess>(process);
    }
}

public sealed class ScriptedProcess(int processId) : IGameProcess
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ConsoleChunk> _output = Channel.CreateUnbounded<ConsoleChunk>();

    public int ProcessId { get; } = processId;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public Task<int> Exited => _exited.Task;

    public ChannelReader<ConsoleChunk> Output => _output.Reader;

    public bool SupportsInterrupt => true;

    public bool IsAlive => !_exited.Task.IsCompleted;

    public void Print(string text) => _output.Writer.TryWrite(new ConsoleChunk(DateTimeOffset.UtcNow, text));

    /// <summary>Ends the process by itself (a crash when <paramref name="code"/> is not 0).</summary>
    public void Exit(int code)
    {
        _exited.TrySetResult(code);
        _output.Writer.TryComplete();
    }

    public ValueTask WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        if (text.StartsWith("quit", StringComparison.Ordinal))
        {
            Exit(0);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendInterruptAsync(CancellationToken cancellationToken)
    {
        Exit(130);
        return ValueTask.CompletedTask;
    }

    public int CountProcesses() => IsAlive ? 1 : 0;

    public void TerminateTree(int exitCode) => Exit(exitCode);

    public ValueTask DisposeAsync()
    {
        Exit(1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Server directories under the test's own folder; no Windows accounts.</summary>
public sealed class FolderProvisioner : IServerProvisioner
{
    public ConcurrentDictionary<string, bool> Provisioned { get; } = new();

    public ProvisionedServer Provision(string serversRoot, string shortId)
    {
        var server = Describe(serversRoot, shortId);
        Directory.CreateDirectory(server.Directory);
        Provisioned[shortId] = true;
        return server;
    }

    public ProvisionedServer Describe(string serversRoot, string shortId)
    {
        ServerShortId.Validate(shortId);
        return new ProvisionedServer(Path.Combine(serversRoot, shortId), ProcessIdentity.CurrentUser);
    }

    public void Deprovision(string serversRoot, string shortId)
    {
        var directory = Describe(serversRoot, shortId).Directory;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Provisioned.TryRemove(shortId, out _);
    }
}
