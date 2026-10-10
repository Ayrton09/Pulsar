using System.Globalization;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Processes;
using Pulsar.Agent.Windows.Servers;
using Serilog;
using Serilog.Extensions.Logging;

namespace Pulsar.Agent;

/// <summary>
/// <c>PulsarAgent host --server &lt;id&gt; --pipe &lt;name&gt; [--data-dir &lt;dir&gt;]</c>: the process one game server runs
/// under (docs/architecture/06, "Server hosts"). The agent starts it; it is not meant to be run by hand, so the help does
/// not list it. Its log goes to <c>logs\hosts</c> in the state directory.
/// </summary>
internal static class HostCommand
{
    public static async Task<int> RunAsync(CommandLine line)
    {
        line.Allow("server", "pipe", "data-dir");
        if (!Guid.TryParse(line.Required("server"), out var serverId))
        {
            throw new UsageException("--server must be a server id.");
        }

        var pipe = line.Required("pipe");
        if (!pipe.StartsWith("pulsar-host-", StringComparison.Ordinal))
        {
            throw new UsageException("--pipe must be a host pipe name.");
        }

        var paths = AgentCommands.Paths(line);
        var log = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "hosts", $"host-{serverId:N}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
        using var loggers = new SerilogLoggerFactory(log, dispose: true);
        try
        {
            // The agent's own lock files: its logons of the server's account and this host's never overlap.
            var accounts = new WindowsServerAccounts(loggers.CreateLogger<WindowsServerAccounts>(), paths.LogonLocksDirectory);
            var processes = new WindowsGameProcessManager(accounts, TimeProvider.System, loggers.CreateLogger<WindowsGameProcessManager>());
            using var service = new GameHostService(processes, serverId, pipe, new GameHostOptions(), TimeProvider.System,
                loggers.CreateLogger<GameHostService>());
            return await service.RunAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Host of server {ServerId} ended unexpectedly", serverId);
            return 1;
        }
    }
}
