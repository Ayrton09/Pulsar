using System.Security.Principal;
using Microsoft.Extensions.Hosting.WindowsServices;
using Pulsar.Agent.Core;
using Pulsar.Agent.Windows;

namespace Pulsar.Agent;

/// <summary>
/// What must hold before the agent runs as the PulsarAgent service (SYSTEM). Checked when the service is installed or
/// started, and again by the agent itself when the service control manager runs it.
/// </summary>
internal static class ServiceChecks
{
    /// <summary>
    /// The agent runs as the service: started by the service control manager, or as SYSTEM in any other way. (Not "not
    /// interactive": SSH, WinRM, scheduled tasks and CI runners are not interactive either.)
    /// </summary>
    public static bool RunsAsService() => RunsAsService(WindowsServiceHelpers.IsWindowsService(), IsSystem());

    internal static bool RunsAsService(bool windowsService, bool system) => windowsService || system;

    /// <summary>
    /// A development enrollment starts servers as the current user, which for the service is SYSTEM (ADR-0005), and its
    /// state directory also grants the user who enrolled: it never runs as the service. Null when it may.
    /// </summary>
    public static string? DevelopmentRefusal(AgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new AgentConfigStore(paths).Load() is { Development: true }
            ? $"This agent was enrolled for development (--dev), which runs game servers as the current user: it cannot run as the {AgentService.Name} service, where that user is SYSTEM. Enroll it again without --dev from an elevated prompt."
            : null;
    }

    /// <summary>The service runs this executable as SYSTEM: refused when anyone else could replace it. Null when it may run.</summary>
    public static string? ExecutableRefusal(string executablePath)
    {
        var findings = ServiceBinarySecurity.FindUntrustedWriters(executablePath);
        return findings.Count == 0
            ? null
            : $"""
                The service would run {executablePath} as SYSTEM, but other accounts could replace it:
                  {string.Join(Environment.NewLine + "  ", findings)}
                Move the agent's folder to one only administrators can modify, such as C:\Program Files\Pulsar\Agent, and run it from there.
                """;
    }

    private static bool IsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }
}
