using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Windows;
using Pulsar.Agent.Windows.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent;

/// <summary>The administrator-facing commands: enroll, status, service.</summary>
internal static class AgentCommands
{
    public static IReadOnlySet<string> Flags { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dev", "no-service", "check", "stop-servers" };

    /// <summary>Where <c>enroll</c> also takes its token from (the process list shows command lines, not environments).</summary>
    public const string TokenVariable = "PULSAR_ENROLLMENT_TOKEN";

    public static int Help()
    {
        Console.WriteLine($"""
            Pulsar Agent {AgentInfo.Version} (protocol v{AgentProtocol.CurrentVersion})

            Usage:
              PulsarAgent enroll --panel <url> --token <token> --panel-key <fingerprint>
                                 [--servers-root <dir>] [--backups-root <dir>] [--no-service] [--dev] [--data-dir <dir>]
                  Pairs this machine with a panel (copy the command the panel shows), then installs and
                  starts the PulsarAgent service. Run it from an elevated prompt. To keep the token out of
                  the process list, pass --token - and paste it, or set {TokenVariable} instead.
              PulsarAgent status [--check] [--data-dir <dir>]
                  Shows the enrollment, the node key fingerprint and the service state; --check also
                  authenticates against the panel.
              PulsarAgent run [--data-dir <dir>]
                  Runs the agent in this console (the service runs this command).
              PulsarAgent service install|uninstall|start|stop [--stop-servers] [--data-dir <dir>]
                  Game servers keep running while the service stops, restarts or is updated (each runs in a
                  host process of its own); --stop-servers stops them with it. Windows shutting down stops them.
              PulsarAgent unenroll [--data-dir <dir>]
                  Removes this machine from Pulsar: stops its game servers, removes the service, the node key
                  and the configuration.

            Options:
              --dev        Development install: key in the current user's store, no administrator rights,
                           no service. Never use it on a real node.
              --data-dir   State directory (default: {AgentPaths.DefaultRoot}).
              --backups-root
                           Where backups go (default: the backups folder of the state directory); only
                           SYSTEM and Administrators can open it.
            """);
        return 0;
    }

    public static async Task<int> EnrollAsync(CommandLine line)
    {
        line.Allow("panel", "token", "panel-key", "servers-root", "backups-root", "no-service", "dev", "data-dir");
        var development = line.Has("dev");
        var installService = !development && !line.Has("no-service");
        if (line.Value("token") == "-" && !Console.IsInputRedirected)
        {
            await Console.Error.WriteAsync("Enrollment token: ");
        }

        var request = new EnrollmentRequest(line.Required("panel"),
            EnrollmentToken(line, Environment.GetEnvironmentVariable(TokenVariable), Console.In), line.Required("panel-key"),
            line.Value("servers-root") is { } root ? Path.GetFullPath(root) : null, development,
            line.Value("backups-root") is { } backups ? Path.GetFullPath(backups) : null);
        if (!development && !WindowsDirectorySecurity.IsElevated())
        {
            await Console.Error.WriteLineAsync("Enrollment creates a machine key and a protected state directory: run it from an elevated prompt (Run as administrator).");
            return 1;
        }

        // Checked before enrolling, so a refusal does not consume the single-use token.
        if (installService && !await CanHostServiceAsync())
        {
            return 1;
        }

        var paths = Paths(line);
        var restartService = false;
        if (installService && AgentService.QueryState() is AgentServiceState.Running or AgentServiceState.StartPending)
        {
            // Re-enrollment: the running agent would keep using the credential the panel is about to revoke.
            Console.WriteLine("Stopping the running PulsarAgent service...");
            AgentService.Stop(TimeSpan.FromSeconds(30));
            restartService = true;
        }

        var keys = new CngNodeKeyStore(NullLogger<CngNodeKeyStore>.Instance);
        var enrollment = new AgentEnrollment(new AgentConfigStore(paths), keys, new WindowsNodeSystem(TimeProvider.System),
            new WindowsDirectorySecurity(), new PanelTransport(), TimeProvider.System);
        AgentConfig config;
        try
        {
            config = await enrollment.EnrollAsync(request, CancellationToken.None);
        }
        catch (Exception ex) when (ex is AgentException or HttpRequestException or TaskCanceledException or CryptographicException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"Enrollment failed: {Describe(ex)}");
            if (restartService)
            {
                AgentService.Start();
            }

            return 1;
        }

        using (var key = keys.Open(config.Key))
        {
            Console.WriteLine($"""
                Enrolled with {PanelAddress.Origin(config.Panel)} as node {config.NodeId}.
                  Node key:   {ProtocolCrypto.Fingerprint(key.PublicKey)} ({Storage(config.Key.Storage)})
                  Panel key:  {request.PanelKeyFingerprint} (pinned)
                  State:      {paths.Root}
                """);
        }

        if (development)
        {
            Console.WriteLine($"Development install: start the agent with  PulsarAgent run --data-dir \"{paths.Root}\"");
        }
        else if (installService)
        {
            AgentService.Install(Environment.ProcessPath!, ServiceArguments(paths));
            AgentService.Start();
            Console.WriteLine($"The {AgentService.Name} service is installed and running.");
        }

        return 0;
    }

    /// <summary>
    /// The single-use enrollment token: <c>--token &lt;token&gt;</c>, <c>--token -</c> (read from standard input) or the
    /// <see cref="TokenVariable"/> environment variable. The last two keep it out of the process list.
    /// </summary>
    internal static string EnrollmentToken(CommandLine line, string? environment, TextReader input)
    {
        var token = line.Value("token") switch
        {
            "-" => input.ReadLine()?.Trim(),
            { } value => value,
            null => environment?.Trim(),
        };
        return token is { Length: > 0 }
            ? token
            : throw new UsageException($"--token is required (or pass '--token -' and type it, or set {TokenVariable}).");
    }

    public static async Task<int> StatusAsync(CommandLine line)
    {
        line.Allow("check", "data-dir");
        var paths = Paths(line);
        var configs = new AgentConfigStore(paths);
        AgentConfig? config;
        try
        {
            config = configs.Load();
        }
        catch (UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"{paths.Root} is readable only by administrators: run this from an elevated prompt.");
            return 1;
        }

        Console.WriteLine($"Pulsar Agent {AgentInfo.Version} (protocol v{AgentProtocol.CurrentVersion})");
        Console.WriteLine($"  Service:    {DescribeService()}");
        if (config is null)
        {
            Console.WriteLine($"  Not enrolled ({paths.ConfigFile} not found).");
            return 1;
        }

        var keys = new CngNodeKeyStore(NullLogger<CngNodeKeyStore>.Instance);
        string fingerprint;
        try
        {
            using var key = keys.Open(config.Key);
            fingerprint = ProtocolCrypto.Fingerprint(key.PublicKey);
        }
        catch (CryptographicException ex)
        {
            fingerprint = $"UNAVAILABLE ({ex.Message.Trim()}); enroll this node again";
        }

        Console.WriteLine($"""
              Panel:      {PanelAddress.Origin(config.Panel)}
              Node:       {config.NodeId} (credential {config.CredentialId}, enrolled {config.EnrolledAt:u})
              Node key:   {fingerprint} ({Storage(config.Key.Storage)}{(config.Development ? ", development" : string.Empty)})
              Key age:    {(DateTimeOffset.UtcNow - config.CurrentKeyCreatedAt).Days} days (replaced automatically after {new AgentConnectionOptions().KeyRotationInterval.Days}){(config.PendingRotation is null ? string.Empty : "; a rotation is pending: the agent completes it when it next connects")}
              Panel keys: {string.Join(", ", config.PanelKeys.Select(k => k.KeyId))}
              Servers:    {config.ServersRoot ?? "(not set)"}
              Backups:    {config.BackupsRootOrDefault(paths)}
              State:      {paths.Root}
            """);

        if (!line.Has("check"))
        {
            return 0;
        }

        try
        {
            using var key = keys.Open(config.Key);
            var session = await new PanelAuthenticator(new PanelTransport()).AuthenticateAsync(config, key, CancellationToken.None);
            Console.WriteLine($"  Check:      OK, the panel accepted this node (session valid until {session.ExpiresAt:u}).");
            return 0;
        }
        catch (Exception ex) when (ex is AgentException or HttpRequestException or TaskCanceledException or CryptographicException)
        {
            Console.WriteLine($"  Check:      FAILED, {Describe(ex)}");
            return 1;
        }
    }

    /// <summary>Removes this machine from Pulsar: service, node key and configuration (logs are kept).</summary>
    public static async Task<int> UnenrollAsync(CommandLine line)
    {
        line.Allow("data-dir");
        var paths = Paths(line);
        var configs = new AgentConfigStore(paths);
        AgentConfig? config;
        try
        {
            config = configs.Load();
        }
        catch (UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"{paths.Root} is readable only by administrators: run this from an elevated prompt.");
            return 1;
        }

        if (config is null)
        {
            Console.WriteLine($"This agent is not enrolled ({paths.ConfigFile} not found).");
            return 0;
        }

        if (!config.Development)
        {
            if (!WindowsDirectorySecurity.IsElevated())
            {
                await Console.Error.WriteLineAsync("Removing the service and the machine key needs an elevated prompt (Run as administrator).");
                return 1;
            }

            // The service stops its game servers on the way out instead of leaving them running in their hosts.
            AskToStopServers(paths);
            AgentService.Uninstall();
        }

        var ended = EndHosts(paths);
        if (ended > 0)
        {
            Console.WriteLine($"Ended {ended} game server(s) the agent had left running (it was not running to stop them).");
        }

        File.Delete(paths.StopServersMarker);

        new CngNodeKeyStore(NullLogger<CngNodeKeyStore>.Instance).Delete(config.Key);
        File.Delete(paths.ConfigFile);
        Console.WriteLine($"""
            This machine is no longer enrolled: the node key and the configuration are deleted{(config.Development ? string.Empty : " and the service is removed")}.
            Delete node {config.NodeId} in the panel too (or re-enroll this machine with a new command).
            """);
        return 0;
    }

    public static async Task<int> ServiceAsync(CommandLine line)
    {
        line.Allow("data-dir", "stop-servers");
        if (!WindowsDirectorySecurity.IsElevated())
        {
            await Console.Error.WriteLineAsync("Managing the service needs an elevated prompt (Run as administrator).");
            return 1;
        }

        switch (line.Subcommand)
        {
            case "install":
                if (!await CanHostServiceAsync() || !await CanRunAsServiceAsync(Paths(line)))
                {
                    return 1;
                }

                AgentService.Install(Environment.ProcessPath!, ServiceArguments(Paths(line)));
                Console.WriteLine($"The {AgentService.Name} service is installed (automatic start). Start it with 'PulsarAgent service start'.");
                return 0;
            case "uninstall":
                AgentService.Uninstall();
                Console.WriteLine($"The {AgentService.Name} service is removed. The enrollment and the node key are kept.");
                return 0;
            case "start":
                // Checked again (rights change after an install); the service itself checks its executable when it runs.
                if (!await CanHostServiceAsync() || !await CanRunAsServiceAsync(Paths(line)))
                {
                    return 1;
                }

                AgentService.Start();
                Console.WriteLine($"The {AgentService.Name} service is starting.");
                return 0;
            case "stop":
                var stopServers = line.Has("stop-servers");
                if (stopServers)
                {
                    AskToStopServers(Paths(line));
                }

                AgentService.Stop(TimeSpan.FromSeconds(60));
                Console.WriteLine(stopServers
                    ? $"The {AgentService.Name} service and its game servers are stopped."
                    : $"The {AgentService.Name} service is stopped; its game servers keep running and it takes them over when it starts.");
                return 0;
            default:
                throw new UsageException("Use 'PulsarAgent service install|uninstall|start|stop'.");
        }
    }

    /// <summary>The next stop of the agent stops its game servers too (docs/architecture/06, "Server hosts").</summary>
    private static void AskToStopServers(AgentPaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.StopServersMarker)!);
        File.WriteAllText(paths.StopServersMarker, string.Empty);
    }

    /// <summary>Ends the host processes of games the agent is not there to stop; their games end with them.</summary>
    private static int EndHosts(AgentPaths paths)
    {
        var store = new ServerStore(paths, new DpapiStateProtector());
        var launcher = new WindowsGameHostLauncher(Environment.ProcessPath!, paths.Root, null, NullLogger<WindowsGameHostLauncher>.Instance);
        var ended = 0;
        foreach (var record in store.LoadHosts())
        {
            if (launcher.IsAlive(record.Host))
            {
                launcher.Kill(record.Host);
                ended++;
            }

            store.DeleteHost(record.ServerId);
        }

        return ended;
    }

    /// <summary>The service runs this executable as SYSTEM: refuse when anyone else could replace it.</summary>
    private static async Task<bool> CanHostServiceAsync()
    {
        if (ServiceChecks.ExecutableRefusal(Environment.ProcessPath!) is not { } refusal)
        {
            return true;
        }

        await Console.Error.WriteLineAsync(refusal);
        return false;
    }

    /// <summary>A development enrollment never runs as the service.</summary>
    private static async Task<bool> CanRunAsServiceAsync(AgentPaths paths)
    {
        if (ServiceChecks.DevelopmentRefusal(paths) is not { } refusal)
        {
            return true;
        }

        await Console.Error.WriteLineAsync(refusal);
        return false;
    }

    public static AgentPaths Paths(CommandLine line) => new(line.Value("data-dir") ?? AgentPaths.DefaultRoot);

    /// <summary>Extra service arguments: only a non-default state directory needs to be passed on.</summary>
    private static string? ServiceArguments(AgentPaths paths) =>
        string.Equals(paths.Root, Path.GetFullPath(AgentPaths.DefaultRoot), StringComparison.OrdinalIgnoreCase)
            ? null
            : $"--data-dir \"{paths.Root}\"";

    private static string DescribeService()
    {
        try
        {
            return AgentService.QueryState() switch
            {
                AgentServiceState.NotInstalled => "not installed",
                AgentServiceState.Running => "running",
                AgentServiceState.Stopped => "stopped",
                AgentServiceState.StartPending => "starting",
                AgentServiceState.StopPending => "stopping",
                _ => "unknown state",
            };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"unknown ({ex.Message})";
        }
    }

    private static string Storage(string storage) => storage == Core.Security.NodeKeyStorage.Tpm ? "TPM" : "software key storage";

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http => $"cannot reach the panel ({http.Message})",
        TaskCanceledException => "the panel did not answer in time",
        _ => ex.Message,
    };
}
