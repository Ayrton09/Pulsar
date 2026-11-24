using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Starts game servers on Linux (docs/architecture/06, "Start sequence"): paths checked to stay in the server's folder,
/// pipes for the console, and the game started through <see cref="GameExec"/> (<c>PulsarAgent game-exec</c>), which makes
/// it the leader of a process group and session of its own (its tree), puts it in its cgroup when there is one, and
/// runs it as the server's user with <c>no_new_privs</c>.
/// </summary>
/// <param name="agentExecutable">The agent's program, which runs <c>game-exec</c>.</param>
/// <param name="cgroups">Where games get cgroups of their own (their limits and usage); null: none (no limits).</param>
/// <param name="allowCurrentUser">
/// Development agents only (enrolled with <c>--dev</c>): a spec without a server account starts the game as the agent's
/// own user. In production that is root, so the default refuses it (and <see cref="GameExec"/> never runs a game as root).
/// </param>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxGameProcessManager(LinuxServerAccounts accounts, string agentExecutable, TimeProvider clock,
    ILogger<LinuxGameProcessManager> logger, GameCgroups? cgroups = null, bool allowCurrentUser = false)
    : IGameProcessManager
{
    /// <summary>How long <see cref="EndProcessesOf"/> waits for a process it ended to be gone.</summary>
    private static readonly TimeSpan s_endWait = TimeSpan.FromSeconds(10);

    // Variables the server cannot override: the agent owns them (docs/architecture/04, "Environment").
    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TMPDIR", "LANG",
    };

    /// <summary>The server's temporary directory, inside its own directory (docs/architecture/06).</summary>
    public static string TempDirectory(string serverRoot) => Path.Combine(serverRoot, ".pulsar", "temp");

    public Task<IGameProcess> StartAsync(ProcessLaunchSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();
        if (spec.ConsoleMode != ConsoleMode.Pipes)
        {
            throw new GameProcessException("process.console_unsupported", "Linux runs games with pipes: there is no pseudo console on Linux yet.");
        }

        var root = LinuxServerPaths.NormalizeRoot(spec.ServerRoot);
        LinuxAccount account;
        if (spec.Identity is ProcessIdentity.AccountIdentity named)
        {
            account = accounts.Lookup(named.Name)
                ?? throw new GameProcessException("process.account_missing", $"The server's account {named.Name} does not exist: install the server again.");
        }
        else if (allowCurrentUser && Libc.geteuid() != 0)
        {
            account = new LinuxAccount(Environment.UserName, Libc.geteuid(), Libc.getegid(), root);
        }
        else
        {
            throw new GameProcessException("process.identity_refused",
                "A server without its own account would run as the agent's user (root for the service). Only an agent enrolled with --dev may do that, and never as root.");
        }

        var executable = LinuxServerPaths.ResolveFile(root, spec.Executable);
        var workingDirectory = LinuxServerPaths.ResolveDirectory(root, spec.WorkingDirectory);
        string? home = spec.UserProfile is { } profile ? LinuxServerPaths.Combine(root, profile) : null;
        var libraries = LibraryPath(root, spec.LibraryPath);
        GameCgroup? cgroup = null;
        string? environmentFile = null;
        if (spec.Limits is not null && cgroups is null)
        {
            throw new GameProcessException("process.limits_failed", "This node cannot cap a server's CPU or memory: it has no cgroups for its games.");
        }

        try
        {
            cgroup = cgroups?.Create(spec.ServerId, spec.Limits);
            var start = new ProcessStartInfo(agentExecutable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = "/",
            };
            string[] directories = home is null
                ? [TempDirectory(root)]
                : [TempDirectory(root), Path.Combine(home, ".config"), Path.Combine(home, ".local", "share")];
            // game-exec runs as root until it becomes the server's user: with the agent's own environment, never the game's
            // (its DOTNET_ROOT, its TMPDIR are the server's), which it reads from a file only root can read.
            environmentFile = GameExec.WriteEnvironment(BuildEnvironment(spec.Environment, account, root, home, libraries));
            foreach (var argument in GameExec.Arguments(account.Uid, account.Gid, environmentFile, cgroup?.Path, workingDirectory, executable,
                         spec.Arguments, directories))
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
            start.Environment["LANG"] = "C.UTF-8";
            // Root's runtime opens no diagnostics pipe (in the temporary folder) for the moment it lives.
            start.Environment["DOTNET_EnableDiagnostics"] = "0";
            foreach (var name in (string[])["DOTNET_ROOT", "DOTNET_ROOT_X64"])
            {
                // Where a framework-dependent agent finds .NET: the agent's own, as it runs.
                if (System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } own)
                {
                    start.Environment[name] = own;
                }
            }

            var process = Process.Start(start) ?? throw new GameProcessException("process.start_failed", "The game's process could not be started.");

            var game = new LinuxGameProcess(process, cgroup, clock, environmentFile);
            cgroup = null; // the game's now
            environmentFile = null; // game-exec deletes it as it reads it, the game's process once it ends
            LogStarted(spec.ServerId, game.ProcessId, account.Name);
            return Task.FromResult<IGameProcess>(game);
        }
        catch (Win32Exception ex)
        {
            throw new GameProcessException("process.start_failed", $"The process could not be started: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new GameProcessException("process.limits_failed", $"The server's cgroup could not be made: {ex.Message}", ex);
        }
        finally
        {
            cgroup?.Dispose();
            if (environmentFile is not null)
            {
                File.Delete(environmentFile);
            }
        }
    }

    public int TerminateStrayProcesses(ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        LinuxAccount? account;
        try
        {
            account = identity is ProcessIdentity.AccountIdentity named ? accounts.Lookup(named.Name) : null;
        }
        catch (GameProcessException)
        {
            return 0; // someone else's account: its processes are not the agent's to end
        }

        if (account is null)
        {
            return 0;
        }

        var killed = KillAllOf(account.Uid);
        if (killed > 0)
        {
            LogStrayProcesses(account.Name, killed);
        }

        return killed;
    }

    /// <summary>
    /// Ends every process of a server's account (unenroll, before the account goes): how many are still running after the
    /// wait (that account is kept).
    /// </summary>
    public static int EndProcessesOf(LinuxServerAccounts accounts, string name)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        LinuxAccount? account;
        try
        {
            account = accounts.Lookup(name);
        }
        catch (GameProcessException)
        {
            return 0; // someone else's account: its processes are not the agent's to end (its deletion is refused too)
        }

        if (account is null)
        {
            return 0;
        }

        _ = KillAllOf(account.Uid);
        var deadline = Environment.TickCount64 + (long)s_endWait.TotalMilliseconds;
        int left;
        while ((left = ProcFs.ProcessesOf(account.Uid).Count) > 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(100);
            _ = KillAllOf(account.Uid);
        }

        return left;
    }

    /// <summary>Kills every process running as <paramref name="uid"/> (never root's); how many were sent the signal.</summary>
    private static int KillAllOf(uint uid)
    {
        if (uid == 0)
        {
            return 0;
        }

        var killed = 0;
        foreach (var pid in ProcFs.ProcessesOf(uid))
        {
            if (Libc.kill(pid, Libc.SigKill) == 0)
            {
                killed++;
            }
        }

        return killed;
    }

    /// <summary>
    /// The folders of the server a game loads its own libraries from, as <c>LD_LIBRARY_PATH</c>: each one inside the
    /// server's folder, or <c>.</c>, the folder itself (the game's account controls them, as it controls the game's
    /// program); null when none.
    /// </summary>
    internal static string? LibraryPath(string root, IReadOnlyList<string>? folders)
    {
        if (folders is not { Count: > 0 })
        {
            return null;
        }

        return string.Join(':', folders.Select(folder => folder.IndexOfAny([':', ';']) >= 0
            ? throw new GameProcessException("process.invalid_path", $"'{folder}' cannot be in a library path.")
            : folder == "." ? root : LinuxServerPaths.Combine(root, folder)));
    }

    /// <summary>
    /// The game's environment: only what the agent sets (nothing of root's own), then the manifest's. Its home is the
    /// server's folder, or the folder of its user profile.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string> overrides, LinuxAccount account, string root,
        string? home, string? libraryPath = null)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["HOME"] = home ?? root,
            ["USER"] = account.Name,
            ["LOGNAME"] = account.Name,
            ["TMPDIR"] = TempDirectory(root),
            ["LANG"] = "C.UTF-8",
        };
        if (libraryPath is not null)
        {
            variables["LD_LIBRARY_PATH"] = libraryPath;
        }

        if (home is not null)
        {
            variables["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
            variables["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");
        }

        foreach (var (name, value) in overrides)
        {
            if (s_reserved.Contains(name) || name.Length == 0 || name.Contains('=', StringComparison.Ordinal) || name.StartsWith("LD_", StringComparison.Ordinal)
                || name.Contains('\0', StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new GameProcessException("process.invalid_environment", $"The environment variable '{name}' cannot be set.");
            }

            variables[name] = value;
        }

        return variables;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: started process {ProcessId} as {Account}")]
    private partial void LogStarted(Guid serverId, int processId, string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killed {Count} process(es) still running as {Account}")]
    private partial void LogStrayProcesses(string account, int count);
}
