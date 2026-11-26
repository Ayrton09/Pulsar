using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Core.Steam;
using Pulsar.Agent.Core.Updates;
using Pulsar.Agent.Linux;
using Pulsar.Manifests;

[assembly: SupportedOSPlatform("linux")]

namespace Pulsar.Agent.Platform;

/// <summary>
/// The Linux agent (docs/architecture/06, "Linux"): a root systemd service, a key file, a state key file, one system user
/// per server, process groups and cgroups.
/// </summary>
internal sealed partial class LinuxAgentPlatform : IAgentPlatform
{
    public string Name => ManifestValidator.LinuxX64;

    public string ElevatedPrompt => "as root (sudo)";

    public string ServiceAccount => "root";

    public string ExecutableName => "PulsarAgent";

    public bool SupportsServerHosts => true;

    public IAgentService Service { get; } = new SystemdService();

    public bool IsElevated() => LinuxDirectorySecurity.IsElevated();

    // systemd says so in the service's environment (INVOCATION_ID); root, the service's account, in any other way.
    public bool RunsAsService() => ServiceChecks.RunsAsService(SystemdHelpers.IsSystemdService(), IsElevated());

    public string? ExecutableRefusal(string executablePath)
    {
        var findings = LinuxDirectorySecurity.FindUntrustedWriters(executablePath);
        return findings.Count == 0
            ? null
            : $"""
                The service would run {executablePath} as root, but other users could replace it:
                  {string.Join(Environment.NewLine + "  ", findings)}
                Install the agent in a folder only root can change, such as /opt/pulsar/agent, and run it from there.
                """;
    }

    public INodeKeyStore Keys(AgentPaths paths) => new FileNodeKeyStore(paths.KeysDirectory);

    public INodeSystem NodeSystem() => new LinuxNodeSystem(TimeProvider.System);

    public IAgentDirectorySecurity DirectorySecurity() => new LinuxDirectorySecurity();

    public IStateProtector StateProtector(AgentPaths paths) => new FileKeyStateProtector(paths.StateKeyFile);

    public string DescribeKeyStorage(string storage) => storage == NodeKeyStorage.File ? "a file only root can read" : storage;

    public IGameHostLauncher HostLauncher(AgentPaths paths)
    {
        UseRuntimeDirectory();
        return new LinuxGameHostLauncher(() => Environment.ProcessPath!, paths.Root, null, NullLogger<LinuxGameHostLauncher>.Instance,
            development: !IsElevated());
    }

    /// <summary>A machine that shuts down: a job of systemd's toward one of its ends is queued.</summary>
    public bool MachineShuttingDown()
    {
        try
        {
            return SystemdUnits.Systemctl("list-jobs", "--no-legend", "shutdown.target", "reboot.target", "poweroff.target", "halt.target", "kexec.target")
                .Trim().Length > 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public ServerRemoval ServerRemoval(AgentPaths paths, bool development)
    {
        var accounts = development || !IsElevated() ? null : new LinuxServerAccounts(NullLogger<LinuxServerAccounts>.Instance);
        return new ServerRemoval(
            LinuxServerAccounts.AccountName,
            accounts is null ? null : accounts.Delete,
            accounts is null ? null : name => LinuxGameProcessManager.EndProcessesOf(accounts, name),
            () => 0,
            null,
            new LinuxServerFiles(),
            new RemovalHints(
                FirewallGroup: "pulsar",
                RemoveFirewallRules: "remove them by hand.",
                RemoveHttpUrls: "remove them by hand.",
                EndProcesses: "End them (sudo pkill -KILL -u <account>) and delete the account (sudo userdel <account>).",
                DeleteAccount: "delete it with sudo userdel.",
                DeleteAccounts: "the accounts with sudo userdel",
                ServiceGone: $"Once systemctl status {SystemdUnits.AgentUnit} says it is gone, delete them by hand"));
    }

    public void AddServices(HostApplicationBuilder builder, AgentPaths paths, PlatformOptions options)
    {
        var isDevelopment = options.IsDevelopment;
        builder.Services.AddSystemd();
        builder.Services.AddSingleton<INodeKeyStore>(new FileNodeKeyStore(paths.KeysDirectory));
        builder.Services.AddSingleton<INodeSystem>(services => new LinuxNodeSystem(services.GetRequiredService<TimeProvider>()));
        // Updates of Pulsar on this machine from the project's releases, when the panel asks (docs/guides/update.md).
        builder.Services.AddSingleton<IUpdatePlatform>(services => new LinuxUpdatePlatform(() => isDevelopment(services)));

        builder.Services.AddSingleton(services => new LinuxServerAccounts(services.GetRequiredService<ILogger<LinuxServerAccounts>>()));
        UseRuntimeDirectory();
        // Games the agent runs itself (Pulsar:ServerHosts false) get cgroups under its own; the others under their hosts'.
        builder.Services.AddSingleton<IGameProcessManager>(services => new LinuxGameProcessManager(services.GetRequiredService<LinuxServerAccounts>(),
            Environment.ProcessPath!, services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<LinuxGameProcessManager>>(),
            cgroups: options.ServerHosts ? null : DelegatedCgroups("agent", services.GetRequiredService<ILogger<LinuxAgentPlatform>>()),
            allowCurrentUser: isDevelopment(services)));
        // Each game in a host process of its own, in a systemd unit of its own, so games outlive the agent.
        builder.Services.AddSingleton<IGameHostLauncher>(services => new LinuxGameHostLauncher(
            () => options.ServerHosts
                ? LinuxDirectorySecurity.Readable(GameHostProgram.Prepare(AppContext.BaseDirectory, ExecutableName, paths.HostProgramsDirectory),
                    paths.HostProgramsDirectory)
                : Path.Combine(AppContext.BaseDirectory, ExecutableName),
            paths.Root, paths.HostProgramsDirectory, services.GetRequiredService<ILogger<LinuxGameHostLauncher>>(),
            development: isDevelopment(services)));
        builder.Services.AddSingleton<IServerProvisioner>(services => new LinuxServerProvisioner(services.GetRequiredService<LinuxServerAccounts>(),
            development: isDevelopment(services), services.GetRequiredService<ILogger<LinuxServerProvisioner>>()));
        builder.Services.AddSingleton<IStateProtector>(new FileKeyStateProtector(paths.StateKeyFile));
        // The RCON password goes only to a process running as the server's account (docs/architecture/08, T18).
        builder.Services.AddSingleton<IRconConnector>(services => new RconConnectors(peers: new LinuxLocalPeers(services.GetRequiredService<LinuxServerAccounts>())));
        builder.Services.AddSingleton<IServerFirewall>(new UnmanagedFirewall());
        builder.Services.AddSingleton<IHttpUrlReservations>(new NoHttpUrlReservations());
        builder.Services.AddSingleton(new InstallOptions { TestGameDirectory = builder.Configuration["Pulsar:TestGameDirectory"], Platform = ManifestValidator.LinuxX64 });
        // The agent reaches the servers' folders as their users (docs/architecture/08, "File manager").
        builder.Services.AddSingleton<IServerFiles>(new LinuxServerFiles());
        builder.Services.AddSingleton(services =>
        {
            var accounts = services.GetRequiredService<LinuxServerAccounts>();
            var identity = new ServerFileIdentity(accounts, isDevelopment(services), services.GetRequiredService<TimeProvider>());
            accounts.Deleted += identity.Forget;
            return identity;
        });
        builder.Services.AddSingleton<IServerFileManager>(services => new LinuxServerFileManager(services.GetRequiredService<ServerFileIdentity>()));
        builder.Services.AddSingleton<IVolumeSnapshots>(new NoVolumeSnapshots());
        builder.Services.AddSingleton<ISteamCmdSource, LinuxSteamCmdBootstrapper>();
    }

    public IGameProcessManager HostProcesses(AgentPaths paths, ILoggerFactory loggers, bool development, Serilog.ILogger log)
    {
        UseRuntimeDirectory();
        // Its game's cgroup goes under the host's unit, which systemd delegated to it; a development host has none.
        return new LinuxGameProcessManager(new LinuxServerAccounts(loggers.CreateLogger<LinuxServerAccounts>()), Environment.ProcessPath!, TimeProvider.System,
            loggers.CreateLogger<LinuxGameProcessManager>(), cgroups: development ? null : DelegatedCgroups("host", loggers.CreateLogger<LinuxAgentPlatform>()),
            allowCurrentUser: development);
    }

    /// <summary>
    /// The pipes between the agent and its hosts in <see cref="LinuxGameHostLauncher.RuntimeDirectory"/> (systemd makes
    /// it for the service and keeps it through its restarts): root's alone, never emptied. A development agent keeps
    /// them in its temporary folder.
    /// </summary>
    private void UseRuntimeDirectory()
    {
        if (!IsElevated())
        {
            return;
        }

        // /run is root's: nobody else can have put something there.
        if (!Directory.Exists(LinuxGameHostLauncher.RuntimeDirectory))
        {
            Directory.CreateDirectory(LinuxGameHostLauncher.RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        GameHostPipes.SocketDirectory = LinuxGameHostLauncher.RuntimeDirectory;
    }

    /// <summary>
    /// Cgroups for games under this process's own, which systemd delegated to its unit (the service's, or a host's):
    /// null when it did not (an agent run by hand), and the node then caps no server's CPU or memory.
    /// </summary>
    private static GameCgroups? DelegatedCgroups(string leaf, ILogger logger)
    {
        if (!LinuxDirectorySecurity.IsElevated() || GameCgroups.Own() is not { } own)
        {
            return null;
        }

        try
        {
            return GameCgroups.Delegated(own, leaf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNoCgroups(logger, own, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No cgroups for games under {Cgroup} ({Reason}): servers' CPU and memory cannot be capped")]
    private static partial void LogNoCgroups(ILogger logger, string cgroup, string reason);
}
