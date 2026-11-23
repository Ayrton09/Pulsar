using Pulsar.Agent.Core;
using Pulsar.Agent.Platform;

namespace Pulsar.Agent;

/// <summary>
/// What must hold before the agent runs as the PulsarAgent service (SYSTEM on Windows, root on Linux). Checked when the
/// service is installed or started, and again by the agent itself when the service manager runs it.
/// </summary>
internal static class ServiceChecks
{
    /// <summary>
    /// The agent runs as the service: started by the service manager, or as the service's account in any other way. (Not
    /// "not interactive": SSH, WinRM, scheduled tasks and CI runners are not interactive either.)
    /// </summary>
    public static bool RunsAsService() => AgentPlatform.Current.RunsAsService();

    internal static bool RunsAsService(bool startedByServiceManager, bool serviceAccount) => startedByServiceManager || serviceAccount;

    /// <summary>
    /// A development enrollment starts servers as the current user, which for the service is its own account (ADR-0005),
    /// and its state directory also grants the user who enrolled: it never runs as the service. Null when it may.
    /// </summary>
    public static string? DevelopmentRefusal(AgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var platform = AgentPlatform.Current;
        return new AgentConfigStore(paths).Load() is { Development: true }
            ? $"This agent was enrolled for development (--dev), which runs game servers as the current user: it cannot run as the {platform.Service.Name} service, where that user is {platform.ServiceAccount}. Enroll it again without --dev, {platform.ElevatedPrompt}."
            : null;
    }

    /// <summary>The service runs this executable as its own account: refused when anyone else could replace it. Null when it may run.</summary>
    public static string? ExecutableRefusal(string executablePath) => AgentPlatform.Current.ExecutableRefusal(executablePath);
}
