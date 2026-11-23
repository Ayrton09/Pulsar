using System.ComponentModel;
using Pulsar.Agent.Linux;

namespace Pulsar.Agent.Platform;

/// <summary>
/// The agent's systemd service (<c>pulsar-agent.service</c>): root, enabled at boot, restarted when it fails. Every
/// change needs root; reading its state does not.
/// </summary>
internal sealed class SystemdService : IAgentService
{
    private static readonly TimeSpan s_poll = TimeSpan.FromMilliseconds(250);

    public string Name => SystemdUnits.AgentUnit;

    public TimeSpan StopTimeout { get; } = TimeSpan.FromSeconds(60);

    private static string UnitFile => Path.Combine(SystemdUnits.UnitDirectory, SystemdUnits.AgentUnit);

    public AgentServiceStatus QueryState()
    {
        var lines = SystemdUnits.Systemctl("show", SystemdUnits.AgentUnit, "-p", "LoadState", "-p", "ActiveState", "--value").Split('\n');
        // In the order systemctl prints properties: as asked.
        return lines.Length < 2 ? AgentServiceStatus.Other : SystemdUnits.StateOf(lines[0], lines[1]) switch
        {
            UnitState.NotInstalled => AgentServiceStatus.NotInstalled,
            UnitState.Stopped => AgentServiceStatus.Stopped,
            UnitState.StartPending => AgentServiceStatus.StartPending,
            UnitState.StopPending => AgentServiceStatus.StopPending,
            UnitState.Running => AgentServiceStatus.Running,
            _ => AgentServiceStatus.Other,
        };
    }

    public void Install(string executable, IReadOnlyList<string> arguments)
    {
        // Written whole under another name, then renamed: systemd never reads half a unit.
        var temporary = UnitFile + ".tmp";
        File.WriteAllText(temporary, SystemdUnits.AgentUnitFile(executable, arguments, StopTimeout));
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temporary, UnitFile, overwrite: true);
        SystemdUnits.Systemctl("daemon-reload");
        SystemdUnits.Systemctl("enable", SystemdUnits.AgentUnit);
    }

    public bool Uninstall()
    {
        if (QueryState() == AgentServiceStatus.NotInstalled)
        {
            return true;
        }

        var stopped = true;
        try
        {
            Stop(StopTimeout);
        }
        catch (TimeoutException)
        {
            // It goes on stopping; once the unit is gone systemd lets it finish.
            stopped = false;
        }

        SystemdUnits.Systemctl("disable", SystemdUnits.AgentUnit);
        File.Delete(UnitFile);
        SystemdUnits.Systemctl("daemon-reload");
        return stopped;
    }

    public void Start() => SystemdUnits.Systemctl("start", "--no-block", SystemdUnits.AgentUnit);

    public AgentServiceStatus StartAndWait(TimeSpan timeout)
    {
        Start();
        var deadline = DateTime.UtcNow + timeout;
        AgentServiceStatus state;
        while ((state = QueryState()) is AgentServiceStatus.StartPending or AgentServiceStatus.Stopped && DateTime.UtcNow < deadline)
        {
            // Stopped right after asking is the start not taken up yet; still stopped later is a start that failed.
            if (state == AgentServiceStatus.Stopped && FailedSinceStart())
            {
                return AgentServiceStatus.Stopped;
            }

            Thread.Sleep(s_poll);
        }

        return state;
    }

    public void Stop(TimeSpan timeout)
    {
        if (QueryState() is AgentServiceStatus.NotInstalled or AgentServiceStatus.Stopped)
        {
            return;
        }

        SystemdUnits.Systemctl("stop", "--no-block", SystemdUnits.AgentUnit);
        var deadline = DateTime.UtcNow + timeout;
        while (QueryState() is not (AgentServiceStatus.Stopped or AgentServiceStatus.NotInstalled))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"The {Name} service did not stop within {timeout.TotalSeconds:0} s.");
            }

            Thread.Sleep(s_poll);
        }
    }

    public string? CommandLine()
    {
        try
        {
            return File.Exists(UnitFile) ? SystemdUnits.ExecStartOf(File.ReadAllText(UnitFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool FailedSinceStart()
    {
        try
        {
            return SystemdUnits.Systemctl("show", SystemdUnits.AgentUnit, "-p", "Result", "--value").Trim() is not ("success" or "");
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
