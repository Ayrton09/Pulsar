using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Windows;
using Pulsar.Agent.Windows.Processes;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Contracts;

namespace Pulsar.Agent;

/// <summary>The administrator-facing commands: enroll, status, service.</summary>
internal static class AgentCommands
{
    public static IReadOnlySet<string> Flags { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dev", "no-service", "check", "stop-servers" };

    /// <summary>Where <c>enroll</c> also takes its token from (the process list shows command lines, not environments).</summary>
    public const string TokenVariable = "PULSAR_ENROLLMENT_TOKEN";

    /// <summary>How long unenroll waits for a service still stopping once the games it was waiting for are ended.</summary>
    private static readonly TimeSpan s_stoppedAfterEndingHosts = TimeSpan.FromSeconds(30);

    /// <summary>How long enroll waits for the service it installed to run (it says so when that takes longer).</summary>
    private static readonly TimeSpan s_serviceStartTimeout = TimeSpan.FromSeconds(60);

    public static int Help()
    {
        Console.WriteLine($"""
            Pulsar Agent {AgentInfo.Version} (protocol v{AgentProtocol.CurrentVersion})

            Usage:
              PulsarAgent enroll --panel <url> --token <token> --panel-key <fingerprint>
                                 [--servers-root <dir>] [--backups-root <dir>] [--no-service] [--dev] [--data-dir <dir>]
                                 [--log-file <file>] [--panel-certificate <sha256>]
                  Pairs this machine with a panel (copy the command the panel shows), then installs and
                  starts the PulsarAgent service. Run it from an elevated prompt. To keep the token out of
                  the process list, pass --token - and paste it, or set {TokenVariable} instead. A reusable
                  token (pulsar_enroll_...) enrolls each machine as a new node. --log-file also writes what it
                  prints to a file (the agent MSI does).
              PulsarAgent status [--check] [--data-dir <dir>]
                  Shows the enrollment, the node key fingerprint and the service state; --check also
                  authenticates against the panel.
              PulsarAgent run [--data-dir <dir>]
                  Runs the agent in this console (the service runs this command).
              PulsarAgent service install|uninstall|start|stop [--stop-servers] [--data-dir <dir>]
                  Game servers keep running while the service stops, restarts or is updated (each runs in a
                  host process of its own); --stop-servers stops them with it (or, when the service is not
                  running, ends those it left running). Windows shutting down stops them.
              PulsarAgent unenroll [--data-dir <dir>] [--log-file <file>]
                  Removes this machine from Pulsar: stops its game servers, removes the service, the node key,
                  the configuration, the servers' accounts, firewall rules and saved secrets, and the cached Steam
                  logins. Server folders, backups and logs are kept. --log-file also writes what it prints to a
                  file (the agent MSI does).

            Options:
              --dev        Development install: key in the current user's store, no administrator rights,
                           no service. Never use it on a real node.
              --data-dir   State directory (default: {AgentPaths.DefaultRoot}).
              --backups-root
                           Where backups go (default: the backups folder of the state directory); only
                           SYSTEM and Administrators can open it.
              --panel-certificate
                           The SHA-256 (64 lowercase hexadecimal digits) of the panel's own TLS certificate,
                           as a node code carries it: the agent accepts that certificate and no other,
                           without this machine trusting it (its stores are not changed). Left out, this
                           machine's trusted roots decide.
            """);
        return 0;
    }

    public static async Task<int> EnrollAsync(CommandLine line)
    {
        line.Allow("panel", "token", "panel-key", "servers-root", "backups-root", "no-service", "dev", "data-dir", "log-file", "panel-certificate");
        return await LoggedAsync(line, "enroll", () => EnrollCoreAsync(line));
    }

    private static async Task<int> EnrollCoreAsync(CommandLine line)
    {
        var development = line.Has("dev");
        var installService = !development && !line.Has("no-service");
        if (line.Value("token") == "-" && !Console.IsInputRedirected)
        {
            await Console.Error.WriteAsync("Enrollment token: ");
        }

        var request = new EnrollmentRequest(line.Required("panel"),
            EnrollmentToken(line, Environment.GetEnvironmentVariable(TokenVariable), Console.In), line.Required("panel-key"),
            DirectoryOption(line, "servers-root"), development, DirectoryOption(line, "backups-root"), line.Value("panel-certificate"));
        if (request.PanelCertificate is { } pin && !NodeCode.IsCertificateHash(pin))
        {
            await Console.Error.WriteLineAsync("--panel-certificate must be the SHA-256 of the panel's certificate in 64 lowercase hexadecimal digits, as the node code carries it.");
            return 1;
        }

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
            if (!await StopForReEnrollmentAsync(AgentService.Stop, Console.Error))
            {
                return 1;
            }

            restartService = true;
        }

        try
        {
            var (code, installed) = await EnrollStoppedAsync(request, paths, development, installService);
            restartService &= !installed;
            return code;
        }
        finally
        {
            // Whatever failed once the service was stopped: the machine goes back to running its previous enrollment.
            if (restartService)
            {
                RestartService();
            }
        }
    }

    /// <returns>The exit code, and whether the service was installed and started with the new enrollment.</returns>
    private static async Task<(int Code, bool Installed)> EnrollStoppedAsync(EnrollmentRequest request, AgentPaths paths, bool development, bool installService)
    {
        var keys = new CngNodeKeyStore(NullLogger<CngNodeKeyStore>.Instance);
        var enrollment = new AgentEnrollment(new AgentConfigStore(paths), keys, new WindowsNodeSystem(TimeProvider.System),
            new WindowsDirectorySecurity(), new PanelTransport(), TimeProvider.System)
        {
            Waiting = wait => Console.Error.WriteLine($"The panel takes no more enrollments from this address for now; trying again in {wait.TotalSeconds:0} s."),
            Warning = Console.Error.WriteLine,
        };
        AgentConfig config;
        try
        {
            config = await enrollment.EnrollAsync(request, CancellationToken.None);
        }
        catch (Exception ex) when (ex is AgentException or HttpRequestException or TaskCanceledException or CryptographicException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"Enrollment failed: {Describe(ex)}");
            return (1, false);
        }

        using (var key = keys.Open(config.Key))
        {
            Console.WriteLine($"""
                Enrolled with {PanelAddress.Origin(config.Panel)} as node {config.NodeId}.
                  Node key:   {ProtocolCrypto.Fingerprint(key.PublicKey)} ({Storage(config.Key.Storage)})
                  Panel key:  {request.PanelKeyFingerprint} (pinned)
                  Panel TLS:  {(config.PanelCertificate is { } pinned ? $"certificate SHA-256 {pinned} (pinned)" : "this machine's trusted roots")}
                  State:      {paths.Root}
                """);
        }

        if (enrollment.AwaitingApproval)
        {
            Console.WriteLine("The panel created a node for this machine that waits for an administrator's approval (Nodes, in the panel): the agent connects once it is approved.");
        }

        if (development)
        {
            Console.WriteLine($"Development install: start the agent with  PulsarAgent run --data-dir \"{paths.Root}\"");
        }
        else if (installService)
        {
            AgentService.Install(Environment.ProcessPath!, ServiceArguments(paths));
            switch (AgentService.StartAndWait(s_serviceStartTimeout))
            {
                case AgentServiceState.Running:
                    Console.WriteLine($"The {AgentService.Name} service is installed and running.");
                    break;
                case AgentServiceState.Stopped:
                    Console.Error.WriteLine($"The {AgentService.Name} service is installed but stopped as it started: its log in " +
                        $"{paths.LogsDirectory} says why; then start it with 'PulsarAgent service start'.");
                    return (1, true);
                default:
                    Console.WriteLine($"The {AgentService.Name} service is installed and still starting: 'PulsarAgent status' says when it runs.");
                    break;
            }

            return (0, true);
        }

        return (0, false);
    }

    /// <summary>
    /// Stops the running service before a re-enrollment; false, said on <paramref name="errors"/>, when it is still stopping
    /// after 30 s: nothing is enrolled then (the token is not used up), and the service goes on stopping.
    /// </summary>
    internal static async Task<bool> StopForReEnrollmentAsync(Action<TimeSpan> stop, TextWriter errors)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(errors);
        try
        {
            stop(TimeSpan.FromSeconds(30));
            return true;
        }
        catch (TimeoutException ex)
        {
            await errors.WriteLineAsync($"{ex.Message} Nothing was enrolled. It goes on stopping: run this again once it has stopped, or start it " +
                $"again with 'PulsarAgent service start' to keep the current enrollment.");
            return false;
        }
    }

    private static void RestartService()
    {
        try
        {
            Console.WriteLine(AgentService.StartAndWait(s_serviceStartTimeout) switch
            {
                AgentServiceState.Running => $"The {AgentService.Name} service is started again.",
                AgentServiceState.Stopped => $"The {AgentService.Name} service stopped as it started again: its log says why.",
                _ => $"The {AgentService.Name} service is starting again.",
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"The {AgentService.Name} service could not be started again ({ex.Message}): start it with 'PulsarAgent service start'.");
        }
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

    /// <summary>
    /// A folder option as a full path. A quoted folder that ends in a backslash ("D:\PulsarServers\", as an installer
    /// writes it) reaches the program with a quote at its end, since the backslash escapes the closing quote: dropped.
    /// </summary>
    internal static string? DirectoryOption(CommandLine line, string name)
    {
        if (line.Value(name) is not { } value)
        {
            return null;
        }

        var folder = value.TrimEnd('"');
        return folder.Length > 0 ? Path.GetFullPath(folder) : throw new UsageException($"--{name} must be a folder.");
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
              Panel TLS:  {(config.PanelCertificate is { } pinned ? $"certificate SHA-256 {pinned} (pinned)" : "this machine's trusted roots")}
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

    /// <summary>
    /// Removes this machine from Pulsar: service, node key, configuration, and what the servers leave besides their folders
    /// (see <see cref="RemoveServers"/>). Server folders, backups and logs are kept.
    /// </summary>
    public static async Task<int> UnenrollAsync(CommandLine line)
    {
        line.Allow("data-dir", "log-file");
        return await LoggedAsync(line, "unenroll", () => UnenrollCoreAsync(Paths(line)));
    }

    private static async Task<int> UnenrollCoreAsync(AgentPaths paths)
    {
        var configs = new AgentConfigStore(paths);
        AgentConfig? config;
        var unreadable = false;
        try
        {
            config = configs.Load();
        }
        catch (UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"{paths.Root} is readable only by administrators: run this from an elevated prompt.");
            return 1;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or NotSupportedException)
        {
            // Never fixed by a retry (the MSI's uninstall would fail for good): the rest is removed without it.
            await Console.Error.WriteLineAsync($"{paths.ConfigFile} cannot be read ({ex.Message}): the node key it names cannot be found, so it " +
                "stays in the machine's key store. Removing the service, the game servers and the configuration anyway.");
            config = null;
            unreadable = true;
        }

        // Without a configuration, the service is this agent's when it runs with this state directory (a development
        // agent never runs as the service, and its own state directory is never the service's).
        var service = config is null ? ServiceRunsFrom(paths) : !config.Development;
        var serviceStillStopping = false;
        if (config is null && !unreadable)
        {
            if (!service)
            {
                Console.WriteLine($"This agent is not enrolled ({paths.ConfigFile} not found).");
                return 0;
            }

            // Deleted by hand, say: the service and the games it left would otherwise outlive the program.
            Console.WriteLine($"This agent is not enrolled ({paths.ConfigFile} not found), but the {AgentService.Name} service runs with {paths.Root}: " +
                "removing it and the game servers it left.");
        }

        if (service)
        {
            if (!WindowsDirectorySecurity.IsElevated())
            {
                await Console.Error.WriteLineAsync("Removing the service and the machine key needs an elevated prompt (Run as administrator).");
                return 1;
            }

            // The service stops its game servers on the way out instead of leaving them running in their hosts.
            Console.WriteLine($"Stopping the {AgentService.Name} service and its game servers...");
            if (AgentService.QueryState() is AgentServiceState.Running or AgentServiceState.StartPending)
            {
                AskToStopServers(paths);
            }

            if (!AgentService.Uninstall())
            {
                // Removing the rest must not stop halfway: the service goes once it has stopped.
                await Console.Error.WriteLineAsync($"The {AgentService.Name} service did not stop within {AgentService.StopTimeout.TotalSeconds:0} s " +
                    "(its game servers were still stopping). It is removed once it stops; the game servers still running are ended now.");
                serviceStillStopping = true;
            }
        }

        var ended = EndHosts(paths);
        if (ended > 0)
        {
            Console.WriteLine($"Ended {ended} game server(s) the agent had left running.");
        }

        // With its games ended, a service that was waiting for them can finish: its state and accounts go only once it has.
        if (serviceStillStopping)
        {
            try
            {
                AgentService.Stop(s_stoppedAfterEndingHosts);
                serviceStillStopping = false;
            }
            catch (TimeoutException)
            {
            }
        }

        DeleteStopServersMarker(paths);

        // No host runs anymore: the copies of the program they ran from can go.
        try
        {
            if (Directory.Exists(paths.HostProgramsDirectory))
            {
                Directory.Delete(paths.HostProgramsDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"{paths.HostProgramsDirectory} could not be deleted ({ex.Message}); delete it by hand.");
        }

        // Server accounts exist outside development; deleting them needs the elevation a production agent's removal has.
        var accounts = config is { Development: true } || !WindowsDirectorySecurity.IsElevated()
            ? null
            : new WindowsServerAccounts(NullLogger<WindowsServerAccounts>.Instance, paths.LogonLocksDirectory);
        RemoveServers(paths, config?.ServersRootOrDefault(paths) ?? (service ? AgentConfig.DefaultServersRoot(paths, development: false) : null),
            config?.BackupsRootOrDefault(paths) ?? paths.BackupsDirectory, new DpapiStateProtector(), accounts is null ? null : accounts.Delete,
            WindowsServerFirewall.RemoveAll, Console.Out, Console.Error, serviceStillStopping);

        return FinishUnenrollment(paths, config, new CngNodeKeyStore(NullLogger<CngNodeKeyStore>.Instance).Delete, service, Console.Out, Console.Error);
    }

    /// <summary>
    /// The last step of unenroll: the node keys, then the configuration. A key that cannot be deleted fails the command and
    /// keeps the configuration, which names the key: running unenroll again (every earlier step can be repeated) deletes it.
    /// </summary>
    internal static int FinishUnenrollment(AgentPaths paths, AgentConfig? config, Action<NodeKeyReference> deleteKey, bool service, TextWriter output,
        TextWriter errors)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errors);
        if (config is not null && !DeleteNodeKeys(config, deleteKey, errors))
        {
            errors.WriteLine($"The configuration ({paths.ConfigFile}) is kept because it names the key: fix what refuses the deletion and run unenroll " +
                "again (what was removed before stays removed), or delete the key by hand and then the configuration.");
            return 1;
        }

        File.Delete(paths.ConfigFile);
        var removed = (config is not null ? "the node key and " : string.Empty) + "the configuration are deleted" +
            (service ? " and the service is removed" : string.Empty);
        output.WriteLine($"""
            This machine is no longer enrolled: {removed}.
            Delete {(config is not null ? $"node {config.NodeId}" : "its node")} in the panel too (or re-enroll this machine with a new command).
            """);
        return 0;
    }

    /// <summary>
    /// Deletes the node key, and the pending one of a rotation that never completed. A key that cannot be deleted is said
    /// (the others still go) and the result is false: the panel keeps accepting its credential until the node is deleted
    /// there, so the key must not be left behind unnoticed.
    /// </summary>
    internal static bool DeleteNodeKeys(AgentConfig config, Action<NodeKeyReference> delete, TextWriter errors)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(delete);
        ArgumentNullException.ThrowIfNull(errors);
        var all = true;
        foreach (var key in new[] { config.Key, config.PendingRotation?.Key }.OfType<NodeKeyReference>())
        {
            try
            {
                delete(key);
            }
            catch (CryptographicException ex)
            {
                all = false;
                errors.WriteLine($"The node key {key.Name} could not be deleted ({ex.Message.Trim()}); it stays in the machine's key store (delete it from there by hand if need be).");
            }
        }

        return all;
    }

    /// <summary>
    /// The rest of what the game servers leave on this machine once their hosts are ended (docs/architecture/03,
    /// "Command line"): their Windows accounts (with their logon right and profile), the firewall rules the agent made, and
    /// the agent's state, which holds the servers' secret variables and RCON passwords and the cached Steam logins. Server
    /// folders, backups and logs are kept, and said so.
    /// </summary>
    /// <param name="serversRoot">Null when it is not known (an unreadable configuration): the server records name the servers then.</param>
    /// <param name="deleteAccount">Deletes a server account (false: there was none); null when there are none to delete (a development agent's servers run as the current user).</param>
    /// <param name="removeFirewallRules">Removes the rules of the servers with these short ids; returns how many.</param>
    /// <param name="serviceStillStopping">The service has not stopped yet: the accounts and the state are kept, and said so.</param>
    internal static void RemoveServers(AgentPaths paths, string? serversRoot, string? backupsRoot, IStateProtector protector,
        Func<string, bool>? deleteAccount, Func<IReadOnlyCollection<string>, int> removeFirewallRules, TextWriter output, TextWriter errors,
        bool serviceStillStopping = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(removeFirewallRules);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errors);

        // A server is named by its record or, if that is gone or unreadable, by its folder.
        var shortIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var record in new ServerStore(paths, protector).LoadAll())
        {
            shortIds.Add(record.Spec.ShortId);
        }

        if (serversRoot is not null && Directory.Exists(serversRoot))
        {
            foreach (var name in Directory.EnumerateDirectories(serversRoot).Select(Path.GetFileName).OfType<string>().Where(ServerShortId.IsValid))
            {
                shortIds.Add(name);
            }
        }

        // A service still stopping may yet save a server record (secrets included) or log on as a server account: those
        // stay, said so, rather than come back after this said they were removed.
        var state = Path.GetDirectoryName(paths.ServersStateDirectory)!;
        var accountNames = deleteAccount is null ? [] : shortIds.Select(WindowsServerAccounts.AccountName).ToList();
        if (serviceStillStopping)
        {
            var rulesRemoved = RemoveFirewallRules(shortIds, removeFirewallRules, errors);
            output.WriteLine($"Removed {rulesRemoved} firewall rule(s).");
            var keptAccounts = accountNames.Count > 0 ? $"the server accounts ({string.Join(", ", accountNames)}) and " : string.Empty;
            errors.WriteLine($"The {AgentService.Name} service was still stopping, so {keptAccounts}{state} (the servers' passwords and the " +
                "cached Steam logins) are kept. Once the service no longer shows in services.msc, delete them by hand" +
                (accountNames.Count > 0 ? " (the accounts in Computer Management, Local Users and Groups)." : "."));
            WriteKept(paths, serversRoot, backupsRoot, output);
            return;
        }

        var accounts = 0;
        foreach (var account in accountNames)
        {
            try
            {
                accounts += deleteAccount!(account) ? 1 : 0;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                errors.WriteLine($"The account {account} could not be deleted ({ex.Message}); delete it in Computer Management (Local Users and Groups).");
            }
        }

        var rules = RemoveFirewallRules(shortIds, removeFirewallRules, errors);

        // Nothing reads the agent's state once the node is gone, and it holds secrets.
        try
        {
            if (Directory.Exists(state))
            {
                Directory.Delete(state, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.WriteLine($"{state} could not be deleted ({ex.Message}): it holds the servers' passwords and Steam logins, delete it by hand.");
        }

        output.WriteLine($"Removed {accounts} server account(s), {rules} firewall rule(s), the server records and the cached Steam logins.");
        WriteKept(paths, serversRoot, backupsRoot, output);
    }

    private static int RemoveFirewallRules(IReadOnlyCollection<string> shortIds, Func<IReadOnlyCollection<string>, int> removeFirewallRules, TextWriter errors)
    {
        try
        {
            return removeFirewallRules(shortIds);
        }
        catch (Exception ex) when (ex is GameProcessException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            errors.WriteLine($"The firewall rules of the game servers (group '{WindowsServerFirewall.Grouping}') could not be removed ({ex.Message}); " +
                "remove them in Windows Defender Firewall from an elevated prompt.");
            return 0;
        }
    }

    private static void WriteKept(AgentPaths paths, string? serversRoot, string? backupsRoot, TextWriter output)
    {
        var kept = new List<string>();
        if (serversRoot is not null)
        {
            kept.Add($"the game servers' folders in {serversRoot}");
        }

        if (backupsRoot is not null)
        {
            kept.Add($"the backups in {backupsRoot}");
        }

        kept.Add($"the logs in {paths.LogsDirectory} and {paths.ConsoleLogsDirectory}");
        output.WriteLine($"Kept {string.Join(", ", kept)}: delete them by hand once they are no longer needed.");
    }

    /// <summary>
    /// Runs a command whose output may go to a log file (all the MSI's custom actions keep of it): an unexpected exception
    /// is printed through that log too, where the runtime would print it past the log, and fails the command.
    /// </summary>
    private static async Task<int> LoggedAsync(CommandLine line, string command, Func<Task<int>> run)
    {
        using var log = CommandLog.Open(line.Value("log-file"), command);
        try
        {
            return await run();
        }
        catch (Exception ex) when (ex is not UsageException)
        {
            await Console.Error.WriteLineAsync($"PulsarAgent {command} failed: {ex}");
            return 1;
        }
    }

    /// <summary>Whether the PulsarAgent service runs with this state directory.</summary>
    private static bool ServiceRunsFrom(AgentPaths paths) => AgentService.CommandLine() is { } commandLine
        && string.Equals(Path.TrimEndingDirectorySeparator(ServiceDataDirectory(commandLine)), Path.TrimEndingDirectorySeparator(paths.Root),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The state directory a service command line (<c>"&lt;exe&gt;" run [--data-dir "&lt;dir&gt;"]</c>, see
    /// <see cref="ServiceArguments"/>) runs with: the default one unless it names another.
    /// </summary>
    internal static string ServiceDataDirectory(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var arguments = new List<string>();
        for (var i = 0; i < commandLine.Length;)
        {
            if (char.IsWhiteSpace(commandLine[i]))
            {
                i++;
                continue;
            }

            // A quoted argument runs to the next quote (paths hold no quotes), any other to the next space.
            var quoted = commandLine[i] == '"';
            var start = quoted ? i + 1 : i;
            var end = quoted ? commandLine.IndexOf('"', start) : commandLine.IndexOfAny([' ', '\t'], start);
            end = end < 0 ? commandLine.Length : end;
            arguments.Add(commandLine[start..end]);
            i = quoted ? end + 1 : end;
        }

        var index = arguments.FindIndex(a => string.Equals(a, "--data-dir", StringComparison.OrdinalIgnoreCase));
        return Path.GetFullPath(index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : AgentPaths.DefaultRoot);
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
                Console.WriteLine(AgentService.Uninstall()
                    ? $"The {AgentService.Name} service is removed. The enrollment and the node key are kept."
                    : $"The {AgentService.Name} service is still stopping: it is removed once it stops. The enrollment and the node key are kept.");
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
                return line.Has("stop-servers") ? await StopWithServersAsync(Paths(line)) : await StopAsync();
            default:
                throw new UsageException("Use 'PulsarAgent service install|uninstall|start|stop'.");
        }
    }

    private static async Task<int> StopAsync()
    {
        try
        {
            AgentService.Stop(AgentService.StopTimeout);
        }
        catch (TimeoutException ex)
        {
            await Console.Error.WriteLineAsync($"{ex.Message} It goes on stopping; check with 'PulsarAgent status'.");
            return 1;
        }

        Console.WriteLine($"The {AgentService.Name} service is stopped; its game servers keep running and it takes them over when it starts.");
        return 0;
    }

    /// <summary>
    /// <c>service stop --stop-servers</c>: a running service is asked to stop its game servers gracefully on its way out;
    /// when it is not running, nobody would read that request, so the games it left running in their hosts are ended.
    /// </summary>
    private static async Task<int> StopWithServersAsync(AgentPaths paths)
    {
        if (AgentService.QueryState() is not (AgentServiceState.Running or AgentServiceState.StartPending))
        {
            try
            {
                AgentService.Stop(AgentService.StopTimeout); // a stop already under way finishes first
            }
            catch (TimeoutException ex)
            {
                await Console.Error.WriteLineAsync($"{ex.Message} Run this again once it has stopped.");
                return 1;
            }

            var ended = EndHosts(paths);
            Console.WriteLine(ended > 0
                ? $"The {AgentService.Name} service was not running; ended the {ended} game server(s) it had left running."
                : $"The {AgentService.Name} service was not running, and no game server was left running.");
            return 0;
        }

        AskToStopServers(paths);
        try
        {
            AgentService.Stop(AgentService.StopTimeout);
        }
        catch (TimeoutException ex)
        {
            await Console.Error.WriteLineAsync($"{ex.Message} It goes on stopping its game servers; check with 'PulsarAgent status'.");
            return 1;
        }

        // A host the service let go of anyway (it was stopping already when asked) is ended too.
        var left = EndHosts(paths);
        DeleteStopServersMarker(paths);
        Console.WriteLine(left > 0
            ? $"The {AgentService.Name} service and its game servers are stopped ({left} ended after the service stopped)."
            : $"The {AgentService.Name} service and its game servers are stopped.");
        return 0;
    }

    /// <summary>The next stop of the agent stops its game servers too (docs/architecture/06, "Server hosts").</summary>
    private static void AskToStopServers(AgentPaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.StopServersMarker)!);
        File.WriteAllText(paths.StopServersMarker, string.Empty);
    }

    private static void DeleteStopServersMarker(AgentPaths paths)
    {
        // (File.Delete fails when the folder is missing: an agent that never had a server has no state folder.)
        if (File.Exists(paths.StopServersMarker))
        {
            File.Delete(paths.StopServersMarker);
        }
    }

    /// <summary>Ends the host processes of games the agent is not there to stop; their games end with them.</summary>
    internal static int EndHosts(AgentPaths paths)
    {
        var store = new ServerStore(paths, new DpapiStateProtector());
        var launcher = new WindowsGameHostLauncher(Environment.ProcessPath!, paths.Root, null, NullLogger<WindowsGameHostLauncher>.Instance);
        var ended = 0;
        foreach (var record in store.LoadHosts())
        {
            bool gone;
            try
            {
                gone = !launcher.IsAlive(record.Host);
                if (!gone && (gone = launcher.Kill(record.Host)))
                {
                    ended++;
                }
            }
            catch (GameProcessException ex) when (ex.Code == GameHostManager.HostUnverifiable)
            {
                Console.Error.WriteLine(ex.Message);
                gone = false;
            }

            if (gone)
            {
                store.DeleteHost(record.ServerId);
            }
            else
            {
                // Still running, or it cannot be told: its record stays so that a later run finds it.
                Console.Error.WriteLine($"The host process {record.Host.ProcessId} could not be ended; its record is kept.");
            }
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

    /// <summary>
    /// The state directory: <c>--data-dir</c> as a full path without a trailing separator, or the default one. A stray
    /// quote at its end is dropped as for the other folders (<see cref="DirectoryOption"/>): a service installed with
    /// <c>--data-dir "D:\Agent\"</c> gets <c>D:\Agent"</c>.
    /// </summary>
    public static AgentPaths Paths(CommandLine line) =>
        new(DirectoryOption(line, "data-dir") is { } directory ? Path.TrimEndingDirectorySeparator(directory) : AgentPaths.DefaultRoot);

    /// <summary>Extra service arguments: only a non-default state directory needs to be passed on.</summary>
    internal static IReadOnlyList<string> ServiceArguments(AgentPaths paths) =>
        string.Equals(paths.Root, Path.GetFullPath(AgentPaths.DefaultRoot), StringComparison.OrdinalIgnoreCase)
            ? []
            : ["--data-dir", paths.Root];

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
