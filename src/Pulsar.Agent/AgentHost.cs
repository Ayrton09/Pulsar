using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Core.Steam;
using Pulsar.Agent.Core.Updates;
using Pulsar.Agent.Windows;
using Pulsar.Agent.Windows.Backups;
using Pulsar.Agent.Windows.Processes;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Contracts;
using Serilog;
using Serilog.Events;

namespace Pulsar.Agent;

/// <summary><c>PulsarAgent run</c>: the agent itself, as a Windows service (how it is installed) or in a console.</summary>
internal static class AgentHost
{
    public static async Task<int> RunAsync(CommandLine line)
    {
        line.Allow("data-dir");
        var paths = AgentCommands.Paths(line);

        // Console-only check; under the service control manager the worker logs the same problem and stops.
        if (Environment.UserInteractive && !File.Exists(paths.ConfigFile))
        {
            await Console.Error.WriteLineAsync($"This agent is not enrolled ({paths.ConfigFile} not found). Run 'PulsarAgent enroll' first.");
            return 1;
        }

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();
        try
        {
            // The service starts in System32; settings and paths are relative to the executable.
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
            builder.Services.AddWindowsService(options => options.ServiceName = AgentService.Name);
            builder.Services.AddSingleton<AgentShutdown>();
            if (WindowsServiceHelpers.IsWindowsService())
            {
                // Tells a Windows shutdown from a service stop: only the first stops the game servers.
                builder.Services.AddSingleton<IHostLifetime, AgentServiceLifetime>();
            }

            builder.Services.AddSerilog((services, configuration) => configuration
                // A lost panel connection is routine (panel restart, network cut) and the agent logs it in one line; the
                // SignalR client would add errors with full stack traces for the same event.
                .MinimumLevel.Override("Microsoft.AspNetCore.SignalR.Client", LogEventLevel.Fatal)
                .MinimumLevel.Override("Microsoft.AspNetCore.Http.Connections.Client", LogEventLevel.Fatal)
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .WriteTo.File(Path.Combine(paths.LogsDirectory, "agent-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    formatProvider: CultureInfo.InvariantCulture));

            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<AgentConfigStore>();
            builder.Services.AddSingleton<INodeKeyStore, CngNodeKeyStore>();
            builder.Services.AddSingleton<INodeSystem, WindowsNodeSystem>();
            builder.Services.AddSingleton<PanelTransport>();
            builder.Services.AddSingleton<PanelAuthenticator>();
            builder.Services.AddSingleton<CredentialRotationClient>();
            builder.Services.AddSingleton<CommandDispatcher>();
            // Updates of Pulsar on this machine from the project's releases, when the panel asks (Phase 19).
            builder.Services.AddSingleton<IUpdatePlatform>(services => new WindowsUpdatePlatform(() => IsDevelopment(services)));
            builder.Services.AddSingleton<AgentUpdater>();
            builder.Services.AddSingleton(new AgentConnectionOptions());
            builder.Services.AddSingleton<PanelChannel>();

            // Game servers (docs/architecture/05 and 06).
            builder.Services.AddSingleton(services => new WindowsServerAccounts(services.GetRequiredService<ILogger<WindowsServerAccounts>>(),
                paths.LogonLocksDirectory));
            // Only an agent enrolled with --dev starts servers as its own account (the service is SYSTEM): the default refuses it.
            static bool IsDevelopment(IServiceProvider services) => services.GetRequiredService<AgentConfigStore>().Load()?.Development ?? false;
            builder.Services.AddSingleton<IGameProcessManager>(services => new WindowsGameProcessManager(services.GetRequiredService<WindowsServerAccounts>(),
                services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<WindowsGameProcessManager>>(),
                allowCurrentUser: IsDevelopment(services)));
            // Each game in a host process of its own, so games outlive the agent (docs/architecture/06, "Server hosts").
            // Pulsar:ServerHosts false runs them inside the agent, as before Phase 14 (they then stop with it); the hosts
            // an earlier run left are still ended.
            var serverHosts = builder.Configuration.GetValue("Pulsar:ServerHosts", true);
            // The agent's own executable, also when it was started as "dotnet PulsarAgent.dll".
            var executable = typeof(AgentHost).Assembly.GetName().Name + ".exe";
            builder.Services.AddSingleton<IGameHostLauncher>(services => new WindowsGameHostLauncher(
                () => serverHosts
                    ? GameHostProgram.Prepare(AppContext.BaseDirectory, executable, paths.HostProgramsDirectory)
                    : Path.Combine(AppContext.BaseDirectory, executable),
                paths.Root, paths.HostProgramsDirectory, services.GetRequiredService<ILogger<WindowsGameHostLauncher>>(),
                IsDevelopment(services)));
            builder.Services.AddSingleton(services => new GameHostManager(services.GetRequiredService<IGameProcessManager>(),
                services.GetRequiredService<IGameHostLauncher>(), services.GetRequiredService<ILogger<GameHostManager>>()));

            builder.Services.AddSingleton<IServerProvisioner>(services => new WindowsServerProvisioner(
                services.GetRequiredService<WindowsServerAccounts>(),
                development: services.GetRequiredService<AgentConfigStore>().Load()?.Development ?? false,
                services.GetRequiredService<ILogger<WindowsServerProvisioner>>()));
            builder.Services.AddSingleton<IStateProtector, DpapiStateProtector>();
            builder.Services.AddSingleton<ServerStore>();
            // The RCON password goes only to a process running as the server's account (docs/architecture/08, T18).
            builder.Services.AddSingleton<IRconConnector>(new RconConnectors(peers: new WindowsLocalPeers()));
            builder.Services.AddSingleton<IServerQuery>(services => new ServerQuery(services.GetRequiredService<TimeProvider>()));
            // Off unless the machine's administrator turns it on: the firewall is theirs (docs/architecture/06).
            builder.Services.AddSingleton<IServerFirewall>(services => new WindowsServerFirewall(
                builder.Configuration.GetValue("Pulsar:ManageFirewall", false), services.GetRequiredService<ILogger<WindowsServerFirewall>>()));
            // With it, each server's account is also kept from the panel's addresses: the panel's and its fallback as the
            // connection uses them now (its configuration in memory), refreshed when they or this machine's addresses change.
            builder.Services.AddSingleton(services =>
            {
                var configs = services.GetRequiredService<AgentConfigStore>();
                var keeper = new PanelBlockKeeper(services.GetRequiredService<IServerFirewall>(),
                    () => (services.GetRequiredService<AgentConnection>().Config ?? configs.Load()) is { } config
                        ? config.FallbackPanel is { } fallback ? [config.Panel, fallback] : [config.Panel]
                        : [],
                    new PanelAddressBook(paths.PanelAddressesFile, services.GetRequiredService<TimeProvider>(),
                        services.GetRequiredService<ILogger<PanelAddressBook>>()),
                    new PanelBlockOptions(), services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<PanelBlockKeeper>>());
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => keeper.Request();
                return keeper;
            });
            // Development and CI only: enables the pulsar-test manifest.
            builder.Services.AddSingleton(new InstallOptions { TestGameDirectory = builder.Configuration["Pulsar:TestGameDirectory"] });
            builder.Services.AddSingleton<IServerFiles, WindowsServerFiles>();
            // Like the process manager, only a development agent acts as itself for a server without an account of its own.
            builder.Services.AddSingleton<IServerImpersonation>(services => new ServerImpersonation(services.GetRequiredService<WindowsServerAccounts>(),
                services.GetRequiredService<TimeProvider>(), development: IsDevelopment(services)));
            builder.Services.AddSingleton<IServerFileManager, WindowsServerFileManager>();
            builder.Services.AddSingleton(new FileCommandOptions());
            builder.Services.AddSingleton<FileCommands>();
            builder.Services.AddSingleton<IRemoteBackupStore, S3RemoteBackupStore>();
            builder.Services.AddSingleton<BackupStore>();
            // Backups of running games read a snapshot of their volume (docs/architecture/06, "Backup snapshots");
            // Pulsar:BackupSnapshots false reads them live, as before Phase 15.
            var backupSnapshots = builder.Configuration.GetValue("Pulsar:BackupSnapshots", true);
            builder.Services.AddSingleton<IVolumeSnapshots, WindowsVolumeSnapshots>();
            builder.Services.AddSingleton<InstallSteps>();
            builder.Services.AddSingleton<ISteamCmdSource, SteamCmdBootstrapper>();
            builder.Services.AddSingleton(new SteamCmdOptions());
            builder.Services.AddSingleton<SteamLoginStore>();
            builder.Services.AddSingleton<SteamCmdRunner>();
            builder.Services.AddSingleton(new SupervisorOptions());
            builder.Services.AddSingleton(new ServerHostOptions { GamesInHosts = serverHosts, BackupSnapshots = backupSnapshots });
            builder.Services.AddSingleton<ServerHost>();

            // Started in this order and stopped in reverse: the connection waits for the servers to load before its Hello,
            // and the servers stop while the panel can still see it.
            builder.Services.AddSingleton<AgentConnection>();
            builder.Services.AddHostedService(services => services.GetRequiredService<AgentConnection>());
            builder.Services.AddHostedService(services => services.GetRequiredService<ServerHost>());
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

            using var host = builder.Build();
            if (ServiceChecks.RunsAsService()
                && (ServiceChecks.DevelopmentRefusal(paths) ?? ServiceChecks.ExecutableRefusal(Environment.ProcessPath!)) is { } refusal)
            {
                // As SYSTEM, a development enrollment would run game servers as SYSTEM, and a replaceable executable
                // would hand SYSTEM to whoever replaces it. (The logging services first: the reason goes to the log file.)
                _ = host.Services.GetRequiredService<ILoggerFactory>();
                Log.Fatal("Pulsar Agent refuses to run as the service: {Reason}", refusal);
                return 1;
            }

            Log.Information("Pulsar Agent {Version} starting (protocol v{ProtocolVersion}, state in {DataDirectory})",
                AgentInfo.Version, AgentProtocol.CurrentVersion, paths.Root);
            await host.RunAsync();
            return Environment.ExitCode;
        }
        catch (Exception ex) when (ex is not HostAbortedException)
        {
            Log.Fatal(ex, "Pulsar Agent terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
