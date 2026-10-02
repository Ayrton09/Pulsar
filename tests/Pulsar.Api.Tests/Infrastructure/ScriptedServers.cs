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

        var process = new ScriptedProcess(_started.Count + 100) { Arguments = spec.Arguments };
        _started.Enqueue(process);
        if (spec.Executable == Pulsar.Agent.Core.Steam.SteamCmdRunner.Executable)
        {
            _ = PlaySteamCmdAsync(process, spec);
            return Task.FromResult<IGameProcess>(process);
        }

        process.Print("Test server ready\r\n");
        return Task.FromResult<IGameProcess>(process);
    }

    public int TerminateStrayProcesses(ProcessIdentity identity) => 0;

    /// <summary>When set, SteamCMD runs stop half way until it completes (to see progress, or cancel).</summary>
    public TaskCompletionSource? SteamCmdGate { get; set; }

    /// <summary>Started SteamCMD runs.</summary>
    public int SteamCmdRuns => _started.Count(p => p.IsSteamCmd);

    /// <summary>The executables a scripted SteamCMD "installs" per Steam app.</summary>
    public static IReadOnlyDictionary<string, string> AppExecutables { get; } = new Dictionary<string, string>
    {
        ["232330"] = "srcds.exe",
        ["730"] = "game/bin/win64/cs2.exe",
    };

    private async Task PlaySteamCmdAsync(ScriptedProcess process, ProcessLaunchSpec spec)
    {
        process.IsSteamCmd = true;
        var app = spec.Arguments[spec.Arguments.ToList().IndexOf("+app_update") + 1];
        if (AppExecutables.TryGetValue(app, out var executable))
        {
            var path = Path.Combine(spec.ServerRoot, executable);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, $"app {app}");
        }

        process.Print("Connecting anonymously to Steam Public...OK\r\n");
        process.Print(" Update state (0x61) downloading, progress: 45.00 (450 / 1000)\r\n");
        if (SteamCmdGate is { } gate)
        {
            await Task.WhenAny(gate.Task, process.Exited);
        }

        process.Print(" Update state (0x81) verifying update, progress: 100.00 (1000 / 1000)\r\n");
        process.Print("Success! App '232330' fully installed.\r\n");
        process.Exit(0);
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

    public bool IsSteamCmd { get; internal set; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The game port a Source game was started with (<c>-port</c>).</summary>
    public int? GamePort => Arguments.ToList().IndexOf("-port") is >= 0 and var i && int.TryParse(Arguments[i + 1], out var port) ? port : null;

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

/// <summary>A SteamCMD "bootstrapper" for the scripted processes: they play SteamCMD when started from a server's copy.</summary>
public sealed class ScriptedSteamCmdSource(string path) : Pulsar.Agent.Core.Steam.ISteamCmdSource
{
    public Task<string> GetAsync(CancellationToken cancellationToken)
    {
        File.WriteAllText(path, "steamcmd");
        return Task.FromResult(path);
    }
}

/// <summary>RCON to the scripted Source games: reachable while the game started on that port runs; "quit" stops it.</summary>
public sealed class ScriptedRcon(ScriptedProcessManager processes) : Pulsar.Agent.Core.Servers.IRconConnector
{
    public List<string> Commands { get; } = [];

    public Task<Pulsar.Agent.Core.Servers.IRconSession> ConnectAsync(Pulsar.Manifests.RconEndpoint endpoint, CancellationToken cancellationToken)
    {
        var game = processes.Started.LastOrDefault(p => p.IsAlive && !p.IsSteamCmd && p.GamePort == endpoint.Port)
            ?? throw new Pulsar.Agent.Core.Servers.RconException("Not listening.");
        return Task.FromResult<Pulsar.Agent.Core.Servers.IRconSession>(new Session(game, this));
    }

    private sealed class Session(ScriptedProcess game, ScriptedRcon owner) : Pulsar.Agent.Core.Servers.IRconSession
    {
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            lock (owner.Commands)
            {
                owner.Commands.Add(command);
            }

            if (command == "quit")
            {
                game.Exit(0);
            }

            return Task.FromResult("");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
