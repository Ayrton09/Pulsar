using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Starts <c>PulsarAgent host</c> processes on Linux (docs/architecture/06, "Server hosts"): each in a systemd unit of its
/// own (<c>pulsar-host-&lt;server&gt;.service</c>, transient), outside the agent's, so it outlives the agent's stops,
/// restarts and updates; its game runs in a cgroup under it (systemd delegates the unit's to it), and the unit ends with
/// the host. A host is identified by its process id and start time, so a reused process id is never mistaken for it.
/// </summary>
/// <param name="program">
/// <c>PulsarAgent</c>, in a copy made by <see cref="GameHostProgram.Prepare"/> when it is the service's. Asked for when
/// first needed, and again at each start while it fails.
/// </param>
/// <param name="hostPrograms">Where those copies are (null: the executable is not a copy, as in tests).</param>
/// <param name="development">
/// A development agent (not root, no system manager to ask): its hosts are plain child processes, with <c>--dev</c>.
/// </param>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxGameHostLauncher(Func<string> program, string dataDirectory, string? hostPrograms, ILogger<LinuxGameHostLauncher> logger,
    bool development = false)
    : IGameHostLauncher
{
    /// <summary>
    /// Where the agent and its hosts keep their pipes (<see cref="GameHostPipes.SocketDirectory"/>): a folder of <c>/run</c>,
    /// root's alone, never emptied while the machine runs (<c>/tmp</c> is, of files left alone for days).
    /// </summary>
    public const string RuntimeDirectory = "/run/pulsar-agent";

    private static readonly TimeSpan s_killWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_startWait = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private string? _executable;

    /// <summary>The programs it runs, and how a test replaces them.</summary>
    internal Func<string, IReadOnlyList<string>, string> Run { get; init; } = (name, arguments) => SystemdUnits.Run(name, arguments);

    /// <summary>Variables the hosts get besides their unit's own (tests: where a framework-dependent build finds .NET).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>The program hosts start from.</summary>
    public string Executable
    {
        get
        {
            lock (_lock)
            {
                if (_executable is null)
                {
                    try
                    {
                        _executable = Path.GetFullPath(program());
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogProgramUnavailable(ex.Message);
                        throw;
                    }
                }

                return _executable;
            }
        }
    }

    /// <summary>The systemd unit of a server's host.</summary>
    public static string UnitOf(Guid serverId) => $"pulsar-host-{serverId:N}.service";

    public GameHostReference Launch(Guid serverId, string pipe)
    {
        if (!pipe.StartsWith("pulsar-host-", StringComparison.Ordinal) || pipe.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException("Not a host pipe name.", nameof(pipe));
        }

        var executable = Executable;
        string[] arguments = ["host", "--server", serverId.ToString("D"), "--pipe", pipe, "--data-dir", dataDirectory];
        var pid = development ? StartChild(executable, [.. arguments, "--dev"]) : StartUnit(serverId, executable, arguments);
        var started = ProcFs.StartTime(pid) ?? throw new GameProcessException("host.start_failed", "The server's host process ended as it started.");
        LogLaunched(serverId, pid);
        return new GameHostReference(pipe, pid, started);
    }

    public bool IsAlive(GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return ProcFs.StartTime(host.ProcessId) is { } started && started == host.ProcessStartedAt;
    }

    public bool Kill(GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!IsAlive(host))
        {
            return true; // gone already
        }

        LogKilled(host.ProcessId);
        // Its unit ends with it, and everything still in the unit's cgroup (its game) with it.
        _ = Libc.kill(host.ProcessId, Libc.SigKill);
        var deadline = System.Environment.TickCount64 + (long)s_killWait.TotalMilliseconds;
        while (IsAlive(host))
        {
            if (System.Environment.TickCount64 >= deadline)
            {
                LogNotKilled(host.ProcessId);
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }

    public bool Serves(PipeStream pipe, GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(host);
        // The kernel's record of who listens on the socket: the host's own process, still running.
        var socket = (int)pipe.SafePipeHandle.DangerousGetHandle();
        return Libc.PeerOf(socket) is { } peer && peer.Pid == host.ProcessId && peer.Uid == Libc.geteuid() && IsAlive(host);
    }

    public void CleanUpPrograms(IEnumerable<GameHostReference> running)
    {
        ArgumentNullException.ThrowIfNull(running);
        if (hostPrograms is null)
        {
            return;
        }

        string current;
        try
        {
            current = Executable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        GameHostProgram.CleanUp(hostPrograms, current,
            [.. running.Where(IsAlive).Select(h => ProcFs.ProgramOf(h.ProcessId)).OfType<string>()]);
    }

    /// <summary>
    /// The host in a transient unit: ordered before the agent's, so a machine that shuts down stops the agent (which stops
    /// its games gracefully) before their hosts; its cgroup delegated (the game's goes under it); gone once it ends.
    /// </summary>
    private int StartUnit(Guid serverId, string executable, IReadOnlyList<string> arguments)
    {
        var unit = UnitOf(serverId);
        // A unit an earlier host left failed (killed, say) would keep its name taken.
        try
        {
            Run("systemctl", ["reset-failed", unit]);
        }
        catch (Win32Exception)
        {
            // Not loaded: nothing to reset.
        }

        try
        {
            Run("systemd-run", [
                "--unit", unit, "--description", $"Pulsar game host of server {serverId:D}", "--collect", "--quiet",
                "--property", "Delegate=yes", "--property", $"Before={SystemdUnits.AgentUnit}", "--property", "KillMode=control-group",
                "--property", "TimeoutStopSec=30", "--property", "UMask=0077",
                // A game sees its own user's processes alone: other servers' command lines hold their passwords.
                "--property", "ProtectProc=invisible",
                "--setenv", "DOTNET_NOLOGO=1", "--setenv", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
                .. Environment.SelectMany(v => (string[])["--setenv", $"{v.Key}={v.Value}"]),
                "--working-directory", Path.GetDirectoryName(executable)!,
                "--", executable, .. arguments]);
        }
        catch (Win32Exception ex)
        {
            throw new GameProcessException("host.start_failed", $"The server's host process could not be started: {ex.Message}", ex);
        }

        var deadline = DateTime.UtcNow + s_startWait;
        while (true)
        {
            var output = Run("systemctl", ["show", unit, "-p", "MainPID", "--value"]).Trim();
            if (int.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0)
            {
                return pid;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new GameProcessException("host.start_failed", $"The server's host ({unit}) did not start.");
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>A development agent's host: a child of its own (not root, it cannot ask the system's manager).</summary>
    private int StartChild(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in Environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new GameProcessException("host.start_failed", "The server's host process could not be started.");
        process.StandardInput.Close();
        return process.Id;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: host process {ProcessId} started")]
    private partial void LogLaunched(Guid serverId, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killing host process {ProcessId}")]
    private partial void LogKilled(int processId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Host process {ProcessId} could not be ended")]
    private partial void LogNotKilled(int processId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The program hosts start from is not available: {Reason}")]
    private partial void LogProgramUnavailable(string reason);
}
