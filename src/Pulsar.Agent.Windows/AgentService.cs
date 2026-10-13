using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Services;

namespace Pulsar.Agent.Windows;

public enum AgentServiceState
{
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Other,
}

/// <summary>
/// The PulsarAgent Windows service: LocalSystem, automatic start, restarted by the service control manager when it
/// fails. Every operation needs administrator rights.
/// </summary>
public static class AgentService
{
    public const string Name = "PulsarAgent";
    public const string DisplayName = "Pulsar Agent";
    public const string Description = "Runs and supervises the game servers of this node for its Pulsar panel.";

    /// <summary>Installs (or reconfigures) the service to run <c>"&lt;exe&gt;" run [arguments]</c>.</summary>
    public static unsafe void Install(string executable, IReadOnlyList<string> arguments)
    {
        var commandLine = CommandLineFor(executable, arguments);
        using var manager = OpenManager();
        using var existing = PInvoke.OpenService(manager, Name, PInvoke.SERVICE_ALL_ACCESS);
        if (!existing.IsInvalid)
        {
            if (!PInvoke.ChangeServiceConfig(existing, ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS, SERVICE_START_TYPE.SERVICE_AUTO_START,
                SERVICE_ERROR.SERVICE_ERROR_NORMAL, commandLine, null, null, null, null, DisplayName))
            {
                throw new Win32Exception();
            }

            Configure(existing);
            return;
        }

        using var service = PInvoke.CreateService(manager, Name, DisplayName, PInvoke.SERVICE_ALL_ACCESS,
            ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS, SERVICE_START_TYPE.SERVICE_AUTO_START, SERVICE_ERROR.SERVICE_ERROR_NORMAL,
            commandLine, null, null, null, null);
        if (service.IsInvalid)
        {
            throw new Win32Exception();
        }

        Configure(service);
    }

    /// <summary>
    /// How long a stop is waited for: an agent asked to stop its game servers gives each one a graceful stop first
    /// (within its 30 s shutdown), and the service control manager needs a moment around that.
    /// </summary>
    public static TimeSpan StopTimeout { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Stops the service and removes it. False when it was still stopping after <see cref="StopTimeout"/>: it is removed
    /// anyway, and Windows deletes it once it has stopped.
    /// </summary>
    public static bool Uninstall()
    {
        var stopped = true;
        try
        {
            Stop(StopTimeout);
        }
        catch (TimeoutException)
        {
            stopped = false;
        }

        using var manager = OpenManager();
        using var service = PInvoke.OpenService(manager, Name, PInvoke.SERVICE_ALL_ACCESS);
        if (service.IsInvalid)
        {
            return stopped;
        }

        // Marks a service that is still stopping for deletion (ERROR_SERVICE_MARKED_FOR_DELETE when it already is).
        if (!PInvoke.DeleteService(service) && (WIN32_ERROR)Marshal.GetLastPInvokeError() != WIN32_ERROR.ERROR_SERVICE_MARKED_FOR_DELETE)
        {
            throw new Win32Exception();
        }

        return stopped;
    }

    public static void Start()
    {
        using var manager = OpenManager();
        using var service = OpenExisting(manager);
        if (!PInvoke.StartService(service, []) && (WIN32_ERROR)Marshal.GetLastPInvokeError()
            != WIN32_ERROR.ERROR_SERVICE_ALREADY_RUNNING)
        {
            throw new Win32Exception();
        }
    }

    /// <summary>Asks the service to stop and waits for it (no-op when not installed or already stopped).</summary>
    public static void Stop(TimeSpan timeout)
    {
        using var manager = OpenManager();
        using var service = PInvoke.OpenService(manager, Name, PInvoke.SERVICE_ALL_ACCESS);
        var state = service.IsInvalid ? AgentServiceState.NotInstalled : State(service);
        if (state is AgentServiceState.NotInstalled or AgentServiceState.Stopped)
        {
            return;
        }

        // A stop already under way is waited for (the service accepts no control meanwhile).
        if (state != AgentServiceState.StopPending && !PInvoke.ControlService(service, PInvoke.SERVICE_CONTROL_STOP, out _)
            && (WIN32_ERROR)Marshal.GetLastPInvokeError() != WIN32_ERROR.ERROR_SERVICE_NOT_ACTIVE)
        {
            throw new Win32Exception();
        }

        var deadline = DateTime.UtcNow + timeout;
        while (State(service) != AgentServiceState.Stopped)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The {Name} service did not stop within {timeout.TotalSeconds:0} s.");
            }

            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// <c>"&lt;exe&gt;" run [arguments]</c>, each argument quoted so the program gets it back as it is (a folder that ends in
    /// a backslash included: quoted as is, its backslash would escape the closing quote).
    /// </summary>
    public static string CommandLineFor(string executable, IReadOnlyList<string> arguments) =>
        Processes.WindowsCommandLine.Build(executable, ["run", .. arguments]);

    /// <summary>The command line the service runs, null when it is not installed; reading it does not need administrator rights.</summary>
    public static string? CommandLine()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Name}");
        return key?.GetValue("ImagePath") as string;
    }

    /// <summary>Current state; reading it does not need administrator rights.</summary>
    public static AgentServiceState QueryState()
    {
        using var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
        if (manager.IsInvalid)
        {
            throw new Win32Exception();
        }

        using var service = PInvoke.OpenService(manager, Name, PInvoke.SERVICE_QUERY_STATUS);
        return service.IsInvalid ? AgentServiceState.NotInstalled : State(service);
    }

    private static AgentServiceState State(SafeHandle service)
    {
        if (!PInvoke.QueryServiceStatus(service, out var status))
        {
            throw new Win32Exception();
        }

        return status.dwCurrentState switch
        {
            SERVICE_STATUS_CURRENT_STATE.SERVICE_STOPPED => AgentServiceState.Stopped,
            SERVICE_STATUS_CURRENT_STATE.SERVICE_START_PENDING => AgentServiceState.StartPending,
            SERVICE_STATUS_CURRENT_STATE.SERVICE_STOP_PENDING => AgentServiceState.StopPending,
            SERVICE_STATUS_CURRENT_STATE.SERVICE_RUNNING => AgentServiceState.Running,
            _ => AgentServiceState.Other,
        };
    }

    /// <summary>Description, and restarts after a failure: 5 s, 15 s, then every 60 s; the count resets after a day.</summary>
    private static unsafe void Configure(SafeHandle service)
    {
        fixed (char* description = Description)
        {
            var info = new SERVICE_DESCRIPTIONW { lpDescription = description };
            if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_DESCRIPTION, &info))
            {
                throw new Win32Exception();
            }
        }

        var actions = stackalloc SC_ACTION[3];
        actions[0] = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 5_000 };
        actions[1] = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 15_000 };
        actions[2] = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 60_000 };
        var failure = new SERVICE_FAILURE_ACTIONSW { dwResetPeriod = 86_400, cActions = 3, lpsaActions = actions };
        if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_FAILURE_ACTIONS, &failure))
        {
            throw new Win32Exception();
        }

        // Also restart when the service stops itself with an error (e.g. it could not open its key), not only on crashes.
        var flag = new SERVICE_FAILURE_ACTIONS_FLAG { fFailureActionsOnNonCrashFailures = true };
        if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, &flag))
        {
            throw new Win32Exception();
        }
    }

    private static CloseServiceHandleSafeHandle OpenManager()
    {
        var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_ALL_ACCESS);
        return manager.IsInvalid ? throw new Win32Exception() : manager;
    }

    private static CloseServiceHandleSafeHandle OpenExisting(SafeHandle manager)
    {
        var service = PInvoke.OpenService(manager, Name, PInvoke.SERVICE_ALL_ACCESS);
        return service.IsInvalid ? throw new InvalidOperationException($"The {Name} service is not installed.") : service;
    }
}
