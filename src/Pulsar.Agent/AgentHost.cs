using System.Globalization;
using Microsoft.Extensions.Hosting;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Core.Steam;
using Pulsar.Agent.Windows;
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
            builder.Services.AddSingleton(new AgentConnectionOptions());
            builder.Services.AddSingleton<PanelChannel>();

            // Game servers (docs/architecture/05 and 06).
            builder.Services.AddSingleton<WindowsServerAccounts>();
            builder.Services.AddSingleton<IGameProcessManager, WindowsGameProcessManager>();
            builder.Services.AddSingleton<IServerProvisioner>(services => new WindowsServerProvisioner(
                services.GetRequiredService<WindowsServerAccounts>(),
                development: services.GetRequiredService<AgentConfigStore>().Load()?.Development ?? false,
                services.GetRequiredService<ILogger<WindowsServerProvisioner>>()));
            builder.Services.AddSingleton<IStateProtector, DpapiStateProtector>();
            builder.Services.AddSingleton<ServerStore>();
            builder.Services.AddSingleton<IRconConnector>(new SourceRconConnector());
            // Off unless the machine's administrator turns it on: the firewall is theirs (docs/architecture/06).
            builder.Services.AddSingleton<IServerFirewall>(services => new WindowsServerFirewall(
                builder.Configuration.GetValue("Pulsar:ManageFirewall", false), services.GetRequiredService<ILogger<WindowsServerFirewall>>()));
            // Development and CI only: enables the pulsar-test manifest.
            builder.Services.AddSingleton(new InstallOptions { TestGameDirectory = builder.Configuration["Pulsar:TestGameDirectory"] });
            builder.Services.AddSingleton<IServerFiles, WindowsServerFiles>();
            builder.Services.AddSingleton<IServerImpersonation, ServerImpersonation>();
            builder.Services.AddSingleton<IServerFileManager, WindowsServerFileManager>();
            builder.Services.AddSingleton(new FileCommandOptions());
            builder.Services.AddSingleton<FileCommands>();
            builder.Services.AddSingleton<IRemoteBackupStore, S3RemoteBackupStore>();
            builder.Services.AddSingleton<BackupStore>();
            builder.Services.AddSingleton<InstallSteps>();
            builder.Services.AddSingleton<ISteamCmdSource, SteamCmdBootstrapper>();
            builder.Services.AddSingleton(new SteamCmdOptions());
            builder.Services.AddSingleton<SteamCmdRunner>();
            builder.Services.AddSingleton(new SupervisorOptions());
            builder.Services.AddSingleton(new ServerHostOptions());
            builder.Services.AddSingleton<ServerHost>();

            // Started in this order and stopped in reverse: the connection waits for the servers to load before its Hello,
            // and the servers stop while the panel can still see it.
            builder.Services.AddHostedService<AgentConnection>();
            builder.Services.AddHostedService(services => services.GetRequiredService<ServerHost>());
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

            using var host = builder.Build();
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
