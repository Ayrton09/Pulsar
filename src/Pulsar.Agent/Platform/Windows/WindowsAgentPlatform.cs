using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
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
using Pulsar.Agent.Windows;
using Pulsar.Agent.Windows.Backups;
using Pulsar.Agent.Windows.Processes;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Platform;

/// <summary>The Windows agent: a SYSTEM service, CNG keys, DPAPI, Windows accounts, Jobs and the Windows Firewall.</summary>
internal sealed class WindowsAgentPlatform : IAgentPlatform
{
    public string Name => ManifestValidator.WindowsX64;

    public string ElevatedPrompt => "from an elevated prompt (Run as administrator)";

    public string ServiceAccount => "SYSTEM";

    public string ExecutableName => "PulsarAgent.exe";

    public bool SupportsServerHosts => true;

    public IAgentService Service { get; } = new WindowsService();

    public bool IsElevated() => WindowsDirectorySecurity.IsElevated();

    public bool RunsAsService() => ServiceChecks.RunsAsService(WindowsServiceHelpers.IsWindowsService(), IsSystem());

    public string? ExecutableRefusal(string executablePath)
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

    public INodeKeyStore Keys(AgentPaths paths) => new CngNodeKeyStore(NullLogger<CngNodeKeyStore>.Instance);

    public INodeSystem NodeSystem() => new WindowsNodeSystem(TimeProvider.System);

    public IAgentDirectorySecurity DirectorySecurity() => new WindowsDirectorySecurity();

    public IStateProtector StateProtector(AgentPaths paths) => new DpapiStateProtector();

    public string DescribeKeyStorage(string storage) => storage == NodeKeyStorage.Tpm ? "TPM" : "software key storage";

    public IGameHostLauncher HostLauncher(AgentPaths paths) =>
        new WindowsGameHostLauncher(Environment.ProcessPath!, paths.Root, null, NullLogger<WindowsGameHostLauncher>.Instance);

    // The service control manager tells the service itself (AgentServiceLifetime.OnShutdown).
    public bool MachineShuttingDown() => false;

    public ServerRemoval ServerRemoval(AgentPaths paths, bool development)
    {
        // Server accounts exist outside development; deleting them needs the elevation a production agent's removal has.
        var accounts = development || !IsElevated()
            ? null
            : new WindowsServerAccounts(NullLogger<WindowsServerAccounts>.Instance, paths.LogonLocksDirectory);
        return new ServerRemoval(
            WindowsServerAccounts.AccountName,
            accounts is null ? null : accounts.Delete,
            accounts is null ? null : WindowsGameProcessManager.EndProcessesOf,
            WindowsServerFirewall.RemoveAll,
            () => new HttpUrlReservations(new HttpServiceUrlAcls(), HttpServiceUrlAcls.SidOf, paths.HttpUrlsFile,
                NullLogger<HttpUrlReservations>.Instance).RemoveAll(),
            new WindowsServerFiles(),
            new RemovalHints(
                FirewallGroup: WindowsServerFirewall.Grouping,
                RemoveFirewallRules: "remove them in Windows Defender Firewall from an elevated prompt.",
                RemoveHttpUrls: "list them with netsh http show urlacl and remove those of the servers' accounts from an elevated prompt.",
                EndProcesses: "End them (Task Manager, Details tab, User name column) and delete the account in Computer Management (Local Users and Groups).",
                DeleteAccount: "delete it in Computer Management (Local Users and Groups).",
                DeleteAccounts: "the accounts in Computer Management, Local Users and Groups",
                ServiceGone: "Once the service no longer shows in services.msc, delete them by hand"));
    }

    public void AddServices(HostApplicationBuilder builder, AgentPaths paths, PlatformOptions options)
    {
        var isDevelopment = options.IsDevelopment;
        builder.Services.AddWindowsService(o => o.ServiceName = WindowsService.ServiceName);
        if (WindowsServiceHelpers.IsWindowsService())
        {
            // Tells a Windows shutdown from a service stop: only the first stops the game servers.
            builder.Services.AddSingleton<IHostLifetime, AgentServiceLifetime>();
        }

        builder.Services.AddSingleton<INodeKeyStore, CngNodeKeyStore>();
        builder.Services.AddSingleton<INodeSystem, WindowsNodeSystem>();
        // Updates of Pulsar on this machine from the project's releases, when the panel asks (Phase 19).
        builder.Services.AddSingleton<IUpdatePlatform>(services => new WindowsUpdatePlatform(() => isDevelopment(services)));

        // Game servers (docs/architecture/05 and 06).
        builder.Services.AddSingleton(services => new WindowsServerAccounts(services.GetRequiredService<ILogger<WindowsServerAccounts>>(),
            paths.LogonLocksDirectory));
        // Only an agent enrolled with --dev starts servers as its own account (the service is SYSTEM): the default refuses it.
        builder.Services.AddSingleton<IGameProcessManager>(services => new WindowsGameProcessManager(services.GetRequiredService<WindowsServerAccounts>(),
            services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<WindowsGameProcessManager>>(),
            allowCurrentUser: isDevelopment(services)));
        // Each game in a host process of its own, so games outlive the agent (docs/architecture/06, "Server hosts").
        // Pulsar:ServerHosts false runs them inside the agent, as before Phase 14 (they then stop with it); the hosts
        // an earlier run left are still ended.
        builder.Services.AddSingleton<IGameHostLauncher>(services => new WindowsGameHostLauncher(
            () => options.ServerHosts
                ? GameHostProgram.Prepare(AppContext.BaseDirectory, ExecutableName, paths.HostProgramsDirectory)
                : Path.Combine(AppContext.BaseDirectory, ExecutableName),
            paths.Root, paths.HostProgramsDirectory, services.GetRequiredService<ILogger<WindowsGameHostLauncher>>(),
            isDevelopment(services)));

        builder.Services.AddSingleton<IServerProvisioner>(services => new WindowsServerProvisioner(
            services.GetRequiredService<WindowsServerAccounts>(),
            development: isDevelopment(services),
            services.GetRequiredService<ILogger<WindowsServerProvisioner>>()));
        builder.Services.AddSingleton<IStateProtector, DpapiStateProtector>();
        // The RCON password goes only to a process running as the server's account (docs/architecture/08, T18).
        builder.Services.AddSingleton<IRconConnector>(new RconConnectors(peers: new WindowsLocalPeers()));
        // Off unless the machine's administrator turns it on: the firewall is theirs (docs/architecture/06).
        builder.Services.AddSingleton<IServerFirewall>(services => new WindowsServerFirewall(
            builder.Configuration.GetValue("Pulsar:ManageFirewall", false), services.GetRequiredService<ILogger<WindowsServerFirewall>>()));
        // A game that listens with HttpListener gets its web ports reserved for its own account, which is no administrator
        // (docs/architecture/06, "HTTP URLs").
        builder.Services.AddSingleton<IHttpUrlReservations>(services => new HttpUrlReservations(new HttpServiceUrlAcls(),
            HttpServiceUrlAcls.SidOf, paths.HttpUrlsFile, services.GetRequiredService<ILogger<HttpUrlReservations>>(),
            isServer: shortId => services.GetRequiredService<ServerStore>().LoadAll().Any(r => r.Spec.ShortId == shortId)));
        // Development and CI only: enables the pulsar-test manifest.
        builder.Services.AddSingleton(new InstallOptions
        {
            TestGameDirectory = builder.Configuration["Pulsar:TestGameDirectory"],
            CodeSignatures = new AuthenticodeSignatures(),
        });
        builder.Services.AddSingleton<IServerFiles, WindowsServerFiles>();
        // Like the process manager, only a development agent acts as itself for a server without an account of its own.
        builder.Services.AddSingleton<IServerImpersonation>(services => new ServerImpersonation(services.GetRequiredService<WindowsServerAccounts>(),
            services.GetRequiredService<TimeProvider>(), development: isDevelopment(services)));
        builder.Services.AddSingleton<IServerFileManager, WindowsServerFileManager>();
        // Backups of running games read a snapshot of their volume (docs/architecture/06, "Backup snapshots").
        builder.Services.AddSingleton<IVolumeSnapshots, WindowsVolumeSnapshots>();
        builder.Services.AddSingleton<ISteamCmdSource, SteamCmdBootstrapper>();
    }

    public IGameProcessManager HostProcesses(AgentPaths paths, ILoggerFactory loggers, bool development, Serilog.ILogger log)
    {
        // Before the game's console host starts with this process's token (docs/architecture/08): never stops a start.
        try
        {
            log.Information("Removed the privileges a host never uses: {Privileges}", string.Join(", ", HostPrivileges.RemoveFromProcess()));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Warning(ex, "The privileges a host never uses could not be removed");
        }

        // The agent's own lock files: its logons of the server's account and this host's never overlap.
        var accounts = new WindowsServerAccounts(loggers.CreateLogger<WindowsServerAccounts>(), paths.LogonLocksDirectory);
        return new WindowsGameProcessManager(accounts, TimeProvider.System, loggers.CreateLogger<WindowsGameProcessManager>(),
            allowCurrentUser: development, hostsOneGame: true); // only a development agent starts hosts with --dev
    }

    private static bool IsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }

    /// <summary>The PulsarAgent Windows service (<see cref="AgentService"/>).</summary>
    private sealed class WindowsService : IAgentService
    {
        public const string ServiceName = AgentService.Name;

        public string Name => AgentService.Name;

        public TimeSpan StopTimeout => AgentService.StopTimeout;

        public AgentServiceStatus QueryState() => Map(AgentService.QueryState());

        public void Install(string executable, IReadOnlyList<string> arguments) => AgentService.Install(executable, arguments);

        public bool Uninstall() => AgentService.Uninstall();

        public void Start() => AgentService.Start();

        public AgentServiceStatus StartAndWait(TimeSpan timeout) => Map(AgentService.StartAndWait(timeout));

        public void Stop(TimeSpan timeout) => AgentService.Stop(timeout);

        public string? CommandLine() => AgentService.CommandLine();

        private static AgentServiceStatus Map(AgentServiceState state) => state switch
        {
            AgentServiceState.NotInstalled => AgentServiceStatus.NotInstalled,
            AgentServiceState.Stopped => AgentServiceStatus.Stopped,
            AgentServiceState.StartPending => AgentServiceStatus.StartPending,
            AgentServiceState.StopPending => AgentServiceStatus.StopPending,
            AgentServiceState.Running => AgentServiceStatus.Running,
            _ => AgentServiceStatus.Other,
        };
    }
}
