using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Pulsar.Agent.Linux;

/// <summary>The agent's systemd unit (<c>pulsar-agent.service</c>, beside the panel's <c>pulsar-panel.service</c>) and <c>systemctl</c>.</summary>
[SupportedOSPlatform("linux")]
public static class SystemdUnits
{
    public const string AgentUnit = "pulsar-agent.service";

    /// <summary>Where administrators' and installers' units go.</summary>
    public const string UnitDirectory = "/etc/systemd/system";

    /// <summary>
    /// The agent's unit: root (it creates the servers' users and starts their games as them), started at boot once the
    /// network is up, restarted when it fails. Its game servers run in units of their own (their hosts), so stopping or
    /// restarting it leaves them running.
    /// </summary>
    public static string AgentUnitFile(string executable, IReadOnlyList<string> arguments, TimeSpan stopTimeout)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"""
            # Written by "PulsarAgent enroll" (and "PulsarAgent service install"): enrolling again rewrites it.
            [Unit]
            Description=Pulsar Agent
            Documentation=https://github.com/Ayrton09/Pulsar
            Wants=network-online.target
            After=network-online.target

            [Service]
            # Ready once it runs: .NET tells systemd.
            Type=notify
            ExecStart={ExecLine(executable, ["run", .. arguments])}
            WorkingDirectory={PathSetting(Path.GetDirectoryName(executable)!)}
            Environment=DOTNET_NOLOGO=1
            Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
            Restart=on-failure
            RestartSec=5
            TimeoutStopSec={(int)stopTimeout.TotalSeconds + 30}
            UMask=0077
            # The game servers' cgroups go under the agent's, and their hosts' pipes in /run/pulsar-agent, kept while the
            # agent restarts (its hosts and their games run on).
            Delegate=yes
            RuntimeDirectory=pulsar-agent
            RuntimeDirectoryMode=0700
            RuntimeDirectoryPreserve=yes
            NoNewPrivileges=true
            # Games it runs itself (Pulsar:ServerHosts false) see their own user's processes alone: other servers' command
            # lines hold their passwords. Root, the agent, still sees every process.
            ProtectProc=invisible
            PrivateTmp=true
            ProtectKernelModules=true
            ProtectKernelLogs=true
            ProtectClock=true
            LockPersonality=true

            [Install]
            WantedBy=multi-user.target

            """);
        // The text's lines are the source file's: Linux' own line ends, whatever the checkout had.
        return builder.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>A command line as systemd reads <c>ExecStart=</c>: each word quoted, so it comes back as it is.</summary>
    public static string ExecLine(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    /// <summary>
    /// One word in systemd's quoting: in double quotes, with backslashes and quotes escaped, and <c>%</c> (its specifiers)
    /// and <c>$</c> (its variables, expanded even in quotes) doubled. Control characters are refused: no path or argument the agent writes has one.
    /// </summary>
    public static string Quote(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Any(char.IsControl))
        {
            throw new ArgumentException("A control character cannot be written into a systemd unit.", nameof(word));
        }

        return "\"" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// A path as a setting that takes the rest of its line (<c>WorkingDirectory=</c>): as it is, unquoted, with <c>%</c>
    /// doubled; it must be absolute and hold no control character.
    /// </summary>
    public static string PathSetting(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
        {
            throw new ArgumentException("Only an absolute path without control characters can be written into a systemd unit.", nameof(path));
        }

        return path.Replace("%", "%%", StringComparison.Ordinal);
    }

    /// <summary>The words of a unit's <c>ExecStart=</c> line, undoing <see cref="Quote"/> (and plain unquoted words).</summary>
    public static IReadOnlyList<string> SplitExecLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var words = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            var word = new StringBuilder();
            if (line[i] == '"')
            {
                for (i++; i < line.Length && line[i] != '"'; i++)
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i++;
                    }

                    word.Append(line[i]);
                }

                i++;
            }
            else
            {
                for (; i < line.Length && !char.IsWhiteSpace(line[i]); i++)
                {
                    word.Append(line[i]);
                }
            }

            words.Add(word.ToString().Replace("%%", "%", StringComparison.Ordinal).Replace("$$", "$", StringComparison.Ordinal));
        }

        return words;
    }

    /// <summary>The <c>ExecStart=</c> of a unit file, as written; null when it has none.</summary>
    public static string? ExecStartOf(string unitFile) =>
        unitFile.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal))?["ExecStart=".Length..];

    /// <summary>What <c>systemctl show -p LoadState -p ActiveState</c> says, in the agent's words.</summary>
    public static UnitState StateOf(string loadState, string activeState) => (loadState.Trim(), activeState.Trim()) switch
    {
        ("not-found", _) => UnitState.NotInstalled,
        (_, "active" or "reloading") => UnitState.Running,
        (_, "activating") => UnitState.StartPending,
        (_, "deactivating") => UnitState.StopPending,
        (_, "inactive" or "failed") => UnitState.Stopped,
        _ => UnitState.Other,
    };

    /// <summary>Runs <c>systemctl</c> with these arguments (never through a shell) and returns what it printed.</summary>
    /// <exception cref="Win32Exception">It failed; its message has what it printed on its error output.</exception>
    public static string Systemctl(params string[] arguments) => Run("systemctl", arguments);

    /// <summary>Runs a program of the system (by name, found on the standard path) and returns its output.</summary>
    /// <exception cref="Win32Exception">It could not run, or ended with an error.</exception>
    public static string Run(string program, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(Locate(program))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // English, whatever the machine's language: the agent reads what these programs print.
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new Win32Exception($"{program} could not be started.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new Win32Exception($"{program} {string.Join(' ', arguments)} did not end in time.");
        }

        if (process.ExitCode != 0)
        {
            throw new Win32Exception(process.ExitCode, $"{program} {string.Join(' ', arguments)} failed ({process.ExitCode}): {error.Result.Trim()}");
        }

        return output.Result;
    }

    /// <summary>
    /// A system program, from the system's own folders only (never a folder of <c>PATH</c> someone else may control).
    /// </summary>
    public static string Locate(string program)
    {
        foreach (var folder in (string[])["/usr/sbin", "/usr/bin", "/sbin", "/bin"])
        {
            var path = Path.Combine(folder, program);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new Win32Exception($"{program} was not found in /usr/sbin, /usr/bin, /sbin or /bin.");
    }
}

public enum UnitState
{
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Other,
}
