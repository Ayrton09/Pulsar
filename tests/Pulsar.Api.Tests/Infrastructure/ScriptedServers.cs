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

        var process = new ScriptedProcess(_started.Count + 100) { Arguments = spec.Arguments, StartLimits = spec.Limits };
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

    /// <summary>Started SteamCMD runs that install or update (not the update checks, which come by themselves).</summary>
    public int SteamCmdRuns => _started.Count(p => p.IsSteamCmd && p.Arguments.Contains("+app_update"));

    /// <summary>Update checks SteamCMD ran (<c>+app_info_print</c>).</summary>
    public int SteamCmdChecks => _started.Count(p => p.IsSteamCmd && p.Arguments.Contains("+app_info_print"));

    /// <summary>The build a scripted Steam offers per app on its public branch: what an install writes, what a check finds.</summary>
    public ConcurrentDictionary<string, string> LatestBuilds { get; } = new() { ["232330"] = "1000", ["730"] = "2000" };

    /// <summary>The branches a scripted Steam offers per app besides public (Pulsar 1.0.14): name, build, description, password needed.</summary>
    public ConcurrentDictionary<string, IReadOnlyList<(string Name, string Build, string? Description, bool Password)>> Branches { get; } = new();

    /// <summary>The <c>-beta</c> each install or update ran with, in order (null: none).</summary>
    public ConcurrentQueue<string?> SteamBetas { get; } = new();

    /// <summary>
    /// The Steam Guard code a scripted Steam asks for when an account signs in with its password; null asks for none. A
    /// sign-in without the password works only with the login a previous one cached (as SteamCMD's config.vdf).
    /// </summary>
    public string? SteamGuardCode { get; set; }

    /// <summary>How each SteamCMD run signed in: "anonymous", "user", "user+password" or "user+password+code".</summary>
    public ConcurrentQueue<string> SteamSignIns { get; } = new();

    /// <summary>The executables a scripted SteamCMD "installs" per Steam app.</summary>
    public static IReadOnlyDictionary<string, string> AppExecutables { get; } = new Dictionary<string, string>
    {
        ["232330"] = "srcds.exe",
        ["232370"] = "srcds.exe",
        ["258550"] = "RustDedicated.exe",
        ["730"] = "game/bin/win64/cs2.exe",
    };

    /// <summary>Steam's side of "+login": false when the run stops here (refused, or waiting for a Steam Guard code).</summary>
    private async Task<bool> SignInAsync(ScriptedProcess process, ProcessLaunchSpec spec)
    {
        var arguments = spec.Arguments.ToList();
        var login = arguments.SkipWhile(a => a != "+login").Skip(1).TakeWhile(a => !a.StartsWith('+')).ToList();
        if (login is ["anonymous"])
        {
            SteamSignIns.Enqueue("anonymous");
            process.Print("Connecting anonymously to Steam Public...OK\r\n");
            return true;
        }

        var cache = Path.Combine(spec.ServerRoot, ".pulsar", "steamcmd", "config", "config.vdf");
        SteamSignIns.Enqueue(login.Count switch { 1 => "user", 2 => "user+password", _ => "user+password+code" });
        if (login.Count == 1)
        {
            if (!File.Exists(cache) || await File.ReadAllTextAsync(cache) != $"login of {login[0]}")
            {
                process.Print($"Logging in user '{login[0]}' to Steam Public...FAILED (Invalid Password)\r\n");
                process.Exit(5);
                return false;
            }
        }
        else if (SteamGuardCode is { } code && (login.Count < 3 || login[2] != code))
        {
            process.Print("This computer has not been authenticated for your account using Steam Guard.\r\nSteam Guard code:");
            return false; // SteamCMD waits for the code: the runner has to stop it
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await File.WriteAllTextAsync(cache, $"login of {login[0]}");
        process.Print($"Logging in user '{login[0]}' to Steam Public...OK\r\n");
        return true;
    }

    private async Task PlaySteamCmdAsync(ScriptedProcess process, ProcessLaunchSpec spec)
    {
        process.IsSteamCmd = true;
        var arguments = spec.Arguments.ToList();
        if (arguments.IndexOf("+app_info_print") is >= 0 and var print)
        {
            // An update check: anonymous, the app's info as SteamCMD prints it (not counted among the sign-ins of installs).
            var asked = arguments[print + 1];
            process.Print("Connecting anonymously to Steam Public...OK\r\n");
            process.Print($"AppID : {asked}, change number : 1/0, last change : Sun Oct 25 12:00:00 2026\r\n");
            if (LatestBuilds.TryGetValue(asked, out var latest))
            {
                var others = string.Concat(Branches.GetValueOrDefault(asked, []).Select(b =>
                    $"\t\t\t\"{b.Name}\"\r\n\t\t\t{{\r\n\t\t\t\t\"buildid\"\t\t\"{b.Build}\"\r\n"
                    + (b.Description is null ? "" : $"\t\t\t\t\"description\"\t\t\"{b.Description}\"\r\n")
                    + (b.Password ? "\t\t\t\t\"pwdrequired\"\t\t\"1\"\r\n" : "") + "\t\t\t}\r\n"));
                process.Print($"\"{asked}\"\r\n{{\r\n\t\"depots\"\r\n\t{{\r\n\t\t\"branches\"\r\n\t\t{{\r\n\t\t\t\"public\"\r\n\t\t\t{{\r\n" +
                    $"\t\t\t\t\"buildid\"\t\t\"{latest}\"\r\n\t\t\t}}\r\n{others}\t\t}}\r\n\t}}\r\n}}\r\n");
            }

            process.Exit(0);
            return;
        }

        if (!await SignInAsync(process, spec))
        {
            return;
        }

        var app = spec.Arguments[arguments.IndexOf("+app_update") + 1];
        var beta = arguments.IndexOf("-beta") is >= 0 and var at ? arguments[at + 1] : null;
        SteamBetas.Enqueue(beta);
        // A branch installs its own build; public (or none) the app's latest.
        var build = Branches.GetValueOrDefault(app, []).FirstOrDefault(b => b.Name == beta).Build ?? LatestBuilds.GetValueOrDefault(app, "1");
        if (AppExecutables.TryGetValue(app, out var executable))
        {
            var path = Path.Combine(spec.ServerRoot, executable);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, $"app {app}");
        }

        // What SteamCMD keeps of what it installed: the build an update check compares.
        var appManifest = Path.Combine(spec.ServerRoot, "steamapps", $"appmanifest_{app}.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(appManifest)!);
        await File.WriteAllTextAsync(appManifest,
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{app}\"\n\t\"buildid\"\t\t\"{build}\"\n}}\n");

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

    /// <summary>When the game was told to save (the pulsar-test manifest's <c>backup.save</c>).</summary>
    public System.Collections.Concurrent.ConcurrentBag<DateTimeOffset> Saves { get; } = [];

    /// <summary>Ends the process by itself (a crash when <paramref name="code"/> is not 0).</summary>
    public void Exit(int code)
    {
        _exited.TrySetResult(code);
        _output.Writer.TryComplete();
    }

    /// <summary>Like the test game server on a pseudo console: the console echoes the line, "say" answers.</summary>
    public ValueTask WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        Print(text);
        if (text.StartsWith("quit", StringComparison.Ordinal))
        {
            Exit(0);
        }
        else if (text.StartsWith("say ", StringComparison.Ordinal))
        {
            Print($"Test server: {text[4..].TrimEnd()}\r\n");
        }
        else if (text.TrimEnd() == "save")
        {
            Saves.Add(DateTimeOffset.UtcNow);
            Print($"Test server: saved ({Saves.Count})\r\n");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendInterruptAsync(CancellationToken cancellationToken)
    {
        Exit(130);
        return ValueTask.CompletedTask;
    }

    public int CountProcesses() => IsAlive ? 1 : 0;

    public ProcessUsage SampleUsage() => IsAlive ? new ProcessUsage(TimeSpan.FromMilliseconds(250), 64 * 1024 * 1024, 1) : new ProcessUsage(TimeSpan.Zero, 0, 0);

    public void TerminateTree(int exitCode) => Exit(exitCode);

    /// <summary>The caps it was started with.</summary>
    public ProcessLimits? StartLimits { get; init; }

    /// <summary>The caps applied while it ran, in order.</summary>
    public ConcurrentQueue<ProcessLimits?> AppliedLimits { get; } = new();

    public void ApplyLimits(ProcessLimits? limits) => AppliedLimits.Enqueue(limits);

    /// <summary>Set before <see cref="Exit"/> for a crash at the memory limit.</summary>
    public bool ReachedMemoryLimit { get; set; }

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

    /// <summary>Set: provisioning waits until it is set again (the node does not know the server meanwhile).</summary>
    public ManualResetEventSlim? HoldProvision { get; set; }

    public ProvisionedServer Provision(string serversRoot, string shortId)
    {
        HoldProvision?.Wait(TimeSpan.FromSeconds(30));
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

    public Task<Pulsar.Agent.Core.Servers.IRconSession> ConnectAsync(Pulsar.Manifests.RconEndpoint endpoint, Pulsar.Agent.Core.Processes.ProcessIdentity owner,
        CancellationToken cancellationToken)
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

            return Task.FromResult(command == "status" ? "hostname: scripted\n" : "");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
