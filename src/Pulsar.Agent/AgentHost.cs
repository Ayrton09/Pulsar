using System.Globalization;
using Microsoft.Extensions.Hosting;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Windows;
using Pulsar.Contracts;
using Serilog;

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
            builder.Services.AddSingleton<CommandDispatcher>();
            builder.Services.AddSingleton(new AgentConnectionOptions());
            builder.Services.AddHostedService<AgentConnection>();

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
