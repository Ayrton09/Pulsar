using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;

namespace Pulsar.Agent.Platform;

/// <summary>
/// What the agent's program does differently on Windows and on Linux (Pulsar's Linux agent): its commands and its run
/// are the same, and ask the platform for what the machine provides. <see cref="AgentPlatform.Current"/> is the one this
/// build is for.
/// </summary>
internal interface IAgentPlatform
{
    /// <summary>"windows-x64" or "linux-x64": which runtime of a manifest the agent starts.</summary>
    string Name { get; }

    /// <summary>How the administrator gets the rights the agent needs, as the end of a sentence: "from an elevated prompt (Run as administrator)".</summary>
    string ElevatedPrompt { get; }

    /// <summary>The account the service runs as: "SYSTEM", "root".</summary>
    string ServiceAccount { get; }

    /// <summary>The agent's executable file name (hosts are started from a copy of it).</summary>
    string ExecutableName { get; }

    /// <summary>Whether games can run in host processes of their own here (<c>Pulsar:ServerHosts</c> decides when they can).</summary>
    bool SupportsServerHosts { get; }

    bool IsElevated();

    /// <summary>Whether this process is the service itself (started by the service manager, or as its account in any other way).</summary>
    bool RunsAsService();

    /// <summary>Who other than the service's account could replace the executable it runs; null when nobody can.</summary>
    string? ExecutableRefusal(string executablePath);

    IAgentService Service { get; }

    INodeKeyStore Keys(AgentPaths paths);

    INodeSystem NodeSystem();

    IAgentDirectorySecurity DirectorySecurity();

    IStateProtector StateProtector(AgentPaths paths);

    /// <summary>Where the node's key lives, for people: "TPM", "a file only root can read".</summary>
    string DescribeKeyStorage(string storage);

    /// <summary>A launcher that finds and ends the host processes an agent left (unenroll, <c>service stop --stop-servers</c>).</summary>
    IGameHostLauncher HostLauncher(AgentPaths paths);

    /// <summary>
    /// Whether the machine is shutting down as the agent stops: its game servers then stop with it (they would die with
    /// the machine). Windows tells the service so itself (<c>AgentServiceLifetime</c>); false when the platform cannot tell.
    /// </summary>
    bool MachineShuttingDown();

    /// <summary>What unenroll removes besides the server folders: the servers' accounts, firewall rules and reservations.</summary>
    ServerRemoval ServerRemoval(AgentPaths paths, bool development);

    /// <summary>Registers the service lifetime and every machine-specific service of <c>PulsarAgent run</c>.</summary>
    void AddServices(HostApplicationBuilder builder, AgentPaths paths, PlatformOptions options);

    /// <summary>The game process manager of a host process (<c>PulsarAgent host</c>), which runs one game.</summary>
    IGameProcessManager HostProcesses(AgentPaths paths, ILoggerFactory loggers, bool development, Serilog.ILogger log);
}

/// <summary>What <c>PulsarAgent run</c> decided before the platform registers its services.</summary>
/// <param name="ServerHosts">Games run in host processes of their own (<c>Pulsar:ServerHosts</c>).</param>
/// <param name="IsDevelopment">Whether the enrollment is a development one (read when the services are first asked for).</param>
internal sealed record PlatformOptions(bool ServerHosts, Func<IServiceProvider, bool> IsDevelopment);

/// <summary>The servers' traces outside their folders, as unenroll removes them (see <see cref="AgentCommands.RemoveServers"/>).</summary>
/// <param name="AccountName">The name of a server's account (as its short id gives it).</param>
/// <param name="DeleteAccount">Deletes an account (false: there was none); null when there are none to delete.</param>
/// <param name="EndProcesses">Ends what runs as an account; returns how many could not be ended.</param>
/// <param name="RemoveFirewallRules">Removes every rule of the agent; returns how many.</param>
/// <param name="RemoveHttpUrls">Releases the servers' HTTP URL reservations; returns how many.</param>
/// <param name="Files">The agent's access to server folders.</param>
/// <param name="Hints">How the administrator removes by hand what could not be removed.</param>
internal sealed record ServerRemoval(
    Func<string, string> AccountName,
    Func<string, bool>? DeleteAccount,
    Func<string, int>? EndProcesses,
    Func<int> RemoveFirewallRules,
    Func<int>? RemoveHttpUrls,
    IServerFiles? Files,
    RemovalHints Hints);

/// <summary>The words unenroll uses for what is left to the administrator (each platform has its own tools).</summary>
internal sealed record RemovalHints(
    string FirewallGroup,
    string RemoveFirewallRules,
    string RemoveHttpUrls,
    string EndProcesses,
    string DeleteAccount,
    string DeleteAccounts,
    string ServiceGone);

/// <summary>The agent's service (Windows' service control manager, Linux' systemd).</summary>
internal interface IAgentService
{
    string Name { get; }

    TimeSpan StopTimeout { get; }

    AgentServiceStatus QueryState();

    void Install(string executable, IReadOnlyList<string> arguments);

    /// <summary>Removes the service; false when it was still stopping (it goes once it stops).</summary>
    bool Uninstall();

    void Start();

    AgentServiceStatus StartAndWait(TimeSpan timeout);

    /// <exception cref="TimeoutException">Still stopping after <paramref name="timeout"/>.</exception>
    void Stop(TimeSpan timeout);

    /// <summary>The command line the service runs; null when it is not installed.</summary>
    string? CommandLine();
}

internal enum AgentServiceStatus
{
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Other,
}
