using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

public sealed class ServerHostOptions
{
    /// <summary>
    /// How long the agent's stop may take (the service control manager waits ~30 s): operations in flight are cancelled
    /// and get up to a quarter of it to end, then servers get the rest to stop gracefully.
    /// </summary>
    public TimeSpan ShutdownBudget { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often the usage of running servers is sent to the panel.</summary>
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Characters of console output each server keeps in memory for browsers that start watching.</summary>
    public int ConsoleBufferCapacity { get; set; } = ConsoleBuffer.DefaultCapacity;

    /// <summary>The console history on disk; null keeps none.</summary>
    public ConsoleLogOptions? ConsoleLog { get; set; } = new();

    /// <summary>
    /// How often the agent notes how far it read the consoles of games in host processes: after a crash of the agent,
    /// its next run shows again at most this much of their output (an orderly stop notes it exactly). The console log on
    /// disk is written about as often, in the background, so it can miss up to that much from right before a crash.
    /// </summary>
    public TimeSpan HostCheckpointInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether games run in host processes (<c>Pulsar:ServerHosts</c>), given a <see cref="GameHostManager"/>. When off,
    /// the manager only ends the hosts an earlier run of the agent left: a game started here would run beside them.
    /// </summary>
    public bool GamesInHosts { get; set; } = true;

    /// <summary>
    /// Whether backups of running games read a snapshot of their volume (<c>Pulsar:BackupSnapshots</c>); when off, they
    /// read the files live, as before Phase 15, and say so.
    /// </summary>
    public bool BackupSnapshots { get; set; } = true;
}

/// <summary>
/// Whether the agent's game servers stop when it stops (docs/architecture/06, "Server hosts"). Games in host processes
/// keep running through a restart or an update of the agent; they stop when Windows shuts down (the service is told)
/// and when the node is unenrolled (<see cref="AgentPaths.StopServersMarker"/>).
/// </summary>
public sealed class AgentShutdown
{
    public bool StopServers { get; set; }
}

/// <summary>
/// The servers of this node: their stored specs, their supervisors, and the commands and operations the panel sends
/// for them (docs/architecture/05). Operations (provision, install, update, delete, backups) run in the background, one
/// at a time per server, and their results are kept until the panel has received them. All but a backup are exclusive:
/// while one runs, the server cannot start and its files cannot change. Started before the panel connection, so the
/// first Hello already reports every server; stopped before it, so the panel sees the servers stop.
/// </summary>
public sealed partial class ServerHost(
    AgentConfigStore configs,
    ServerStore store,
    IServerProvisioner provisioner,
    IGameProcessManager processes,
    InstallSteps installSteps,
    SteamCmdRunner steamCmd,
    IServerFiles files,
    IRconConnector rcon,
    IServerFirewall firewall,
    FileCommands fileCommands,
    BackupStore backups,
    InstallOptions installOptions,
    PanelChannel panel,
    SupervisorOptions supervisorOptions,
    ServerHostOptions options,
    TimeProvider clock,
    ILoggerFactory loggerFactory,
    IServerQuery? query = null,
    GameHostManager? hosts = null,
    AgentShutdown? shutdown = null,
    IVolumeSnapshots? snapshots = null,
    PanelBlockKeeper? panelBlock = null) : IHostedService, IServerEventSink, IGameHostRecords, IDisposable
{
    // The panel waits for the ack: deleting a remote copy gets a bounded time.
    private static readonly TimeSpan s_remoteDeleteTimeout = TimeSpan.FromSeconds(20);

    private static readonly IVolumeSnapshots s_noSnapshots = new NoVolumeSnapshots();

    private const string Platform = ManifestValidator.WindowsX64;

    private readonly ILogger _logger = loggerFactory.CreateLogger<ServerHost>();
    private readonly ConcurrentDictionary<Guid, Server> _servers = new();
    private readonly ConcurrentDictionary<Guid, RunningOperation> _operations = new(); // server → operation in flight
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new(); // operation → its cancellation
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _endingOperations = new(); // the agent stops: operations end first
    private readonly Lock _recordLock = new();
    private readonly ConcurrentDictionary<Guid, Func<ServerRecord, ServerRecord>> _unsavedRecords = new(); // changes a failed write left
    private readonly ConcurrentDictionary<Guid, OperationCompleted> _unsavedResults = new(); // operation → result a failed write left in memory
    private readonly Lock _hostLock = new();
    private readonly ConcurrentDictionary<Guid, GameHostRecord> _savedHosts = new(); // host records as last written
    private readonly ConcurrentDictionary<Guid, StrandedHost> _strandedHosts = new(); // hosts left running that this agent cannot drive
    private readonly ConsoleRelay _relay = new(panel, clock);
    private AgentConfig? _config;
    private Task _metrics = Task.CompletedTask;
    private Task _checkpoints = Task.CompletedTask;

    /// <summary>Completes once the stored servers are loaded (the Hello waits for it).</summary>
    public Task Loaded => _loaded.Task;

    /// <summary>What starts and takes over games in host processes; null when games run in the agent.</summary>
    private GameHostManager? Hosts => options.GamesInHosts ? hosts : null;

    /// <summary>Capabilities this host adds to the agent's Hello.</summary>
    public IEnumerable<string> Capabilities
    {
        get
        {
            yield return AgentCapabilities.ProvisionServer;
            yield return AgentCapabilities.InstallServer;
            yield return AgentCapabilities.StartServer;
            yield return AgentCapabilities.StopServer;
            yield return AgentCapabilities.RestartServer;
            yield return AgentCapabilities.KillServer;
            yield return AgentCapabilities.DeleteServer;
            yield return AgentCapabilities.UpdateServer;
            yield return AgentCapabilities.ApplyServerSpec;
            yield return AgentCapabilities.CancelOperation;
            yield return AgentCapabilities.ConsoleInput;
            yield return AgentCapabilities.ConsoleSubscribe;
            yield return AgentCapabilities.ConsoleUnsubscribe;
            foreach (var capability in FileCommands.Capabilities)
            {
                yield return capability;
            }

            yield return AgentCapabilities.CreateBackup;
            yield return AgentCapabilities.RestoreBackup;
            yield return AgentCapabilities.DeleteBackup;
            yield return AgentCapabilities.SendBackup;
            yield return AgentCapabilities.RemoteBackups;
            yield return AgentCapabilities.CleanRestore;
            yield return AgentCapabilities.Limits;
            yield return AgentCapabilities.SteamAccounts;
            yield return AgentCapabilities.InstallAddon;
            yield return AgentCapabilities.RemoveAddon;
            yield return AgentCapabilities.GameSettings;
            if (installOptions.TestGameAvailable)
            {
                yield return AgentCapabilities.TestGame;
            }

            if (firewall.Enabled)
            {
                yield return AgentCapabilities.Firewall;
            }

            if (Hosts is not null)
            {
                yield return AgentCapabilities.ServerHosts;
            }
        }
    }

    public void Dispose()
    {
        _relay.Dispose();
        _stopping.Dispose();
        _endingOperations.Dispose();
    }

    public IReadOnlyList<ServerSnapshot> Snapshots() => [.. _servers.Values.Select(s => s.Supervisor.Snapshot)];

    /// <summary>A server's console (tests).</summary>
    internal ConsoleBuffer? ConsoleOf(Guid serverId) => _servers.TryGetValue(serverId, out var server) ? server.Supervisor.Console.Buffer : null;

    /// <summary>The consoles the panel wants after a Hello (browsers were watching them); unknown servers are skipped.</summary>
    public void SubscribeConsoles(IReadOnlyList<ConsoleSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        foreach (var subscription in subscriptions)
        {
            if (_servers.TryGetValue(subscription.ServerId, out var server))
            {
                _relay.Subscribe(subscription.ServerId, server.Supervisor.Console.Buffer, subscription.FromSequence);
            }
        }
    }

    /// <summary>
    /// Operations still running, and finished ones whose result waits in the outbox: the panel must not mark those as
    /// interrupted, since the result follows right after the Hello.
    /// </summary>
    public IReadOnlyList<Guid> OperationsInFlight() =>
        [.. _operations.Values.Select(o => o.Id).Concat(_unsavedResults.Keys).Concat(store.PendingResults().Select(r => r.OperationId)).Distinct()];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        panel.OnConnected(DeliverPendingResultsAsync);
        panel.OnConnected(DeliverLatestStatesAsync);
        _config = configs.Load();
        if (_config is null)
        {
            _loaded.TrySetResult();
            return;
        }

        DeleteStaleStopServersMarker();
        var resume = new List<ServerSupervisor>();
        try
        {
            // Games a previous run of the agent left running in their hosts are taken over before the first Hello, which
            // then reports them running.
            var running = hosts is null ? [] : store.LoadHosts().ToDictionary(h => h.ServerId);
            if (Hosts is { } manager)
            {
                manager.Records = this;
            }
            else
            {
                // Games run in the agent now (Pulsar:ServerHosts is off): one left in its host would run beside them.
                foreach (var left in running.Values)
                {
                    LogLeftHostEnded(left.ServerId, left.Host.ProcessId);
                    if (hosts!.End(left.Host))
                    {
                        ForgetHost(left.ServerId); // a host that could not be ended keeps its record, for the next run
                    }
                }

                running.Clear();
            }

            // An unreadable record is set aside by the store; one that cannot be loaded is skipped: neither may keep
            // the other servers from loading, or the Hello from being sent.
            var loaded = new List<(ServerRecord Record, ServerSupervisor Supervisor)>();
            foreach (var record in store.LoadAll())
            {
                try
                {
                    loaded.Add((record, Add(record.ServerId, record.Spec, record.RconPassword)));
                }
                catch (Exception ex) when (ex is LaunchPlanException or GameProcessException or ArgumentException)
                {
                    LogServerNotLoaded(ex, record.ServerId);
                    continue;
                }

                DeleteLentSteamLogin(record.ServerId, record.Spec.ShortId);
            }

            // Every account is kept from the panel before its game starts or is taken over, then kept so (doc 06).
            panelBlock?.Start(ServerAccounts, _stopping.Token);

            // All at once: a host slow to answer (a machine busy starting) does not hold up the others.
            var occupied = await Task.WhenAll(loaded.Select(l => running.Remove(l.Record.ServerId, out var host)
                ? ReattachAsync(l.Supervisor, l.Record, host, cancellationToken)
                : Task.FromResult(false)));
            for (var i = 0; i < loaded.Count; i++)
            {
                if (!occupied[i] && (loaded[i].Record.Spec.AutoStart || loaded[i].Record.ResumeOnStart))
                {
                    resume.Add(loaded[i].Supervisor);
                }
            }

            foreach (var orphan in running.Values)
            {
                if (store.Exists(orphan.ServerId))
                {
                    // The server is still the node's, but its record could not be loaded (or read, and was set aside):
                    // its game is left running, and its host record kept, for whoever repairs it.
                    LogHostOfUnloadedServer(orphan.ServerId, orphan.Host.ProcessId);
                    _savedHosts[orphan.ServerId] = orphan;
                    continue;
                }

                // Hosts of servers this node no longer has: nothing may keep running unwatched.
                LogOrphanHost(orphan.ServerId, orphan.Host.ProcessId);
                if (hosts!.End(orphan.Host))
                {
                    ForgetHost(orphan.ServerId); // kept when it could not be ended: the next run tries again
                }
            }

            hosts?.CleanUpPrograms([.. _savedHosts.Values.Select(h => h.Host)]);

            var serversRoot = _config.ServersRootOrDefault(configs.Paths);
            LogLoaded(_servers.Count, serversRoot);
            try
            {
                backups.CleanUp(BackupsRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogBackupsNotCleaned(ex.Message);
            }

            // A backup the previous run did not finish may have left a taken-over game not saving (Minecraft's save-off).
            await BackupSource.ResumePausedAsync(store, id => _servers.TryGetValue(id, out var server) ? server.Supervisor : null, _logger);
        }
        finally
        {
            _loaded.TrySetResult();
        }

        _metrics = SendMetricsAsync(_stopping.Token);
        if (Hosts is not null)
        {
            _checkpoints = SaveHostCheckpointsAsync(_stopping.Token);
        }

        foreach (var supervisor in resume)
        {
            UpdateRecord(supervisor.ServerId, r => r with { ResumeOnStart = false });
            await supervisor.StartAsync(StateChangeReason.AgentStarted);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await fileCommands.StopAsync();
        // Operations end while the servers are still attached (a backup that paused its game's saving resumes it), in at
        // most a quarter of the shutdown budget; the servers get what is left.
        var ending = System.Diagnostics.Stopwatch.StartNew();
        await EndOperationsAsync(options.ShutdownBudget / 4);
        var remaining = options.ShutdownBudget > ending.Elapsed ? options.ShutdownBudget - ending.Elapsed : TimeSpan.Zero;
        var servers = _servers.Values.ToList();
        var marker = configs.Paths.StopServersMarker;
        var keepRunning = Hosts is not null && shutdown?.StopServers != true && !File.Exists(marker);
        var detached = keepRunning ? await Task.WhenAll(servers.Select(s => s.Supervisor.DetachAsync())) : new bool[servers.Count];
        for (var i = 0; i < servers.Count; i++)
        {
            if (detached[i])
            {
                PersistHost(servers[i].Supervisor.ServerId);
            }
        }

        var left = detached.Count(d => d);
        if (left > 0)
        {
            LogLeftRunning(left);
        }

        var results = await Task.WhenAll(servers.Select((s, i) => detached[i] ? Task.FromResult(false) : s.Supervisor.ShutdownAsync(remaining)));
        for (var i = 0; i < servers.Count; i++)
        {
            if (results[i])
            {
                // Running before an orderly stop: started again when the agent starts (docs/architecture/05).
                UpdateRecord(servers[i].Supervisor.ServerId, r => r with { ResumeOnStart = true });
            }

            await servers[i].Supervisor.DisposeAsync();
            await servers[i].Supervisor.Console.DisposeAsync(); // the end of its history reaches the disk
        }

        RetryUnsavedRecords();

        // The panel should see the servers stop. State changes are sent without waiting and the connection closes right
        // after this, so the last state of each server is delivered again, waiting until the panel has processed it (an
        // already received change carries the same sequence and changes nothing).
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var change in servers.Select(s => s.Supervisor.LastChange).OfType<ServerStateChanged>())
        {
            if (!await panel.DeliverAsync(nameof(IAgentHub.ServerStateChanged), change, deadline.Token))
            {
                break;
            }
        }

        await _stopping.CancelAsync();
        _relay.Clear();
        await _metrics;
        await _checkpoints;
        if (File.Exists(marker))
        {
            try
            {
                File.Delete(marker);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted when the agent starts next (it is older than that run): only this stop was asked to stop them.
            }
        }
    }

    /// <summary>
    /// A stop-servers marker older than this run of the agent was left by an earlier one (which could not delete it):
    /// it was meant for that run's stop. A marker written while this run goes on is for this run's stop.
    /// </summary>
    private void DeleteStaleStopServersMarker()
    {
        var marker = configs.Paths.StopServersMarker;
        try
        {
            if (File.Exists(marker) && File.GetLastWriteTimeUtc(marker) < System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime())
            {
                File.Delete(marker);
                LogStaleStopServersMarker();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStopServersMarkerNotDeleted(ex.Message);
        }
    }

    /// <summary>
    /// Takes over a game the previous run of the agent left in its host. False when the host is gone, so the server may
    /// start; true when its game runs, taken over or in a host of another protocol version this agent leaves alone.
    /// </summary>
    private async Task<bool> ReattachAsync(ServerSupervisor supervisor, ServerRecord record, GameHostRecord host, CancellationToken cancellationToken)
    {
        IHostedGameProcess? process;
        try
        {
            process = await Hosts!.ReattachAsync(host.Host, host.ConsoleOffset, cancellationToken);
        }
        catch (GameProcessException ex) when (ex.Code is "host.incompatible" or GameHostManager.HostUnverifiable)
        {
            // Its record stays for an agent that speaks its protocol (or can check it); the server must not start beside it
            // meanwhile. A host that could not be checked is handled the same way: it may well be running.
            _savedHosts[record.ServerId] = host;
            _strandedHosts[record.ServerId] = new StrandedHost(host, unverifiable: ex.Code == GameHostManager.HostUnverifiable);
            return true;
        }

        if (process is not null)
        {
            _savedHosts[record.ServerId] = host;
            var state = new GameHostState(host.Host, host.ConsoleOffset, host.LogPosition, host.Ready, host.Stopping);
            if (await supervisor.AttachAsync(process, state, record.DesiredRunning))
            {
                return true;
            }

            await process.DisposeAsync();
        }

        // The host ended (the machine restarted, say) and its game with it: the server is as after an agent crash.
        LogHostGone(record.ServerId);
        ForgetHost(record.ServerId);
        return false;
    }

    /// <summary>Keeps where the server's game runs (its host) and how far its console was read, when that changed.</summary>
    private void PersistHost(Guid serverId)
    {
        if (!_servers.TryGetValue(serverId, out var server))
        {
            return;
        }

        // Read under the lock: a checkpoint must not write what it read before the host was released (or replaced).
        lock (_hostLock)
        {
            if (server.Supervisor.HostState is not { } state)
            {
                ForgetHost(serverId);
                return;
            }

            SaveHost(new GameHostRecord(serverId, state.Host, state.ConsoleOffset, state.LogPosition, state.Ready, state.Stopping));
        }
    }

    private void SaveHost(GameHostRecord record)
    {
        lock (_hostLock)
        {
            if (_savedHosts.TryGetValue(record.ServerId, out var saved) && saved == record)
            {
                return;
            }

            try
            {
                store.SaveHost(record);
                _savedHosts[record.ServerId] = record;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogHostNotSaved(record.ServerId, ex.Message);
            }
        }
    }

    void IGameHostRecords.HostLaunched(Guid serverId, GameHostReference host) =>
        SaveHost(new GameHostRecord(serverId, host, ConsoleOffset: 0, LogPosition: 0, Ready: false));

    void IGameHostRecords.HostAbandoned(Guid serverId, GameHostReference host)
    {
        lock (_hostLock)
        {
            if (_savedHosts.TryGetValue(serverId, out var saved) && saved.Host == host)
            {
                ForgetHost(serverId);
            }
        }
    }

    /// <summary>
    /// Ends the host a server's game was left in that this agent cannot drive (another protocol version, or a host that could
    /// not be checked): a stop, kill or delete of the server. The entry stays until the host is really gone, and one command
    /// at a time ends it (ending can take seconds): a start that arrives meanwhile waits, and finds it gone or still there.
    /// </summary>
    private bool EndStrandedHost(Guid serverId) => EndStrandedHost(serverId, out _);

    private bool EndStrandedHost(Guid serverId, out StrandedHost? ended)
    {
        ended = null;
        if (!_strandedHosts.TryGetValue(serverId, out var stranded))
        {
            return false;
        }

        lock (stranded)
        {
            if (!_strandedHosts.TryGetValue(serverId, out var current) || current != stranded)
            {
                return false; // another command ended it while this one waited
            }

            LogStrandedHostEnded(serverId, stranded.Record.Host.ProcessId);
            if (!hosts!.End(stranded.Record.Host))
            {
                return false; // still there, or it cannot be checked: its record stays, and the server does not start beside it
            }

            _strandedHosts.TryRemove(serverId, out _);
            ForgetHost(serverId);
            ended = stranded;
            return true;
        }
    }

    /// <summary>
    /// A host that could not be checked is looked at again, whatever the command: gone (its pipe is, or its process is), its
    /// record is dropped and the server is as after any agent restart. Nothing is killed here.
    /// </summary>
    private void RecheckUnverifiableHost(Guid serverId)
    {
        if (!_strandedHosts.TryGetValue(serverId, out var stranded) || !stranded.Unverifiable)
        {
            return;
        }

        lock (stranded)
        {
            if (_strandedHosts.TryGetValue(serverId, out var current) && current == stranded && hosts!.IsGone(stranded.Record.Host))
            {
                _strandedHosts.TryRemove(serverId, out _);
                ForgetHost(serverId);
            }
        }
    }

    /// <summary>As above, for a server that stays: its console tells the user what became of the game they could not see.</summary>
    private bool EndStrandedHost(Server server)
    {
        if (!EndStrandedHost(server.Supervisor.ServerId, out var ended))
        {
            return false;
        }

        server.Supervisor.Console.Note(ended!.Unverifiable
            ? "The agent had lost track of this server's game, left running by an earlier run, and it could not be checked: it is ended now " +
              "(killed, without saving, if it still ran)."
            : "An earlier version of the agent had left this server's game running, which this version cannot manage: " +
              "ended the game (killed, without saving).", clock.GetUtcNow());
        return true;
    }

    private void ForgetHost(Guid serverId)
    {
        lock (_hostLock)
        {
            try
            {
                store.DeleteHost(serverId);
                _savedHosts.TryRemove(serverId, out _);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogHostNotSaved(serverId, ex.Message);
            }
        }
    }

    /// <summary>Handles a verified server command; the ack says whether it was accepted, not that it finished.</summary>
    public async Task<CommandAck> ExecuteAsync(CommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (_config is null)
        {
            return Rejected(envelope, "agent.not_enrolled", "The agent has no configuration.");
        }

        return envelope.Command switch
        {
            ProvisionServerCommand provision => Provision(envelope, provision),
            InstallServerCommand install => Install(envelope, install),
            DeleteServerCommand delete => Delete(envelope, delete),
            UpdateServerCommand update => Update(envelope, update),
            InstallAddonCommand install => InstallAddon(envelope, install),
            RemoveAddonCommand remove => RemoveAddon(envelope, remove),
            ApplyServerSpecCommand apply => await ApplySpecAsync(envelope, apply),
            CancelOperationCommand cancel => Cancel(envelope, cancel),
            CreateBackupCommand create => CreateBackup(envelope, create),
            RestoreBackupCommand restore => RestoreBackup(envelope, restore),
            DeleteBackupCommand delete => await DeleteBackupAsync(envelope, delete),
            SendBackupCommand send => await SendBackupAsync(envelope, send),
            StartServerCommand start => await Control(envelope, start.ServerId, s => s.StartAsync(), allowedDuringOperation: false),
            StopServerCommand stop => await Control(envelope, stop.ServerId, s => s.StopAsync(), allowedDuringOperation: true),
            RestartServerCommand restart => await Control(envelope, restart.ServerId, s => s.RestartAsync(), allowedDuringOperation: false),
            KillServerCommand kill => await Control(envelope, kill.ServerId, s => s.KillAsync(), allowedDuringOperation: true),
            SendConsoleInputCommand input => await ConsoleInputAsync(envelope, input),
            SubscribeConsoleCommand subscribe => SubscribeConsole(envelope, subscribe),
            UnsubscribeConsoleCommand unsubscribe => new CommandAck(envelope.CommandId,
                _relay.Unsubscribe(unsubscribe.ServerId) ? AckStatus.Accepted : AckStatus.AlreadyInState, null, null),
            var command when FileCommands.ServerOf(command) is { } serverId => await FilesAsync(envelope, serverId),
            _ => new CommandAck(envelope.CommandId, AckStatus.UnsupportedCommand, "command.unsupported", null),
        };
    }

    /// <summary>File manager commands: off the connection's thread (a large deletion takes a while).</summary>
    private async Task<CommandAck> FilesAsync(CommandEnvelope envelope, Guid serverId)
    {
        if (!_servers.TryGetValue(serverId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        var provisioned = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), server.ShortId);
        var busy = Exclusive(serverId);
        return await Task.Run(() => fileCommands.Execute(envelope, provisioned, busy));
    }

    private async Task<CommandAck> ConsoleInputAsync(CommandEnvelope envelope, SendConsoleInputCommand command)
    {
        if (!_servers.TryGetValue(command.ServerId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        if (!ConsoleInput.IsValid(command.Line))
        {
            return Rejected(envelope, "console.invalid_input", $"A command is one line of up to {ConsoleInput.MaxLength} characters.");
        }

        return await server.Supervisor.SendInputAsync(command.Line) switch
        {
            AckStatus.Accepted => new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null),
            AckStatus.Busy => Rejected(envelope, "console.not_reading", "The game is not reading its console input (it may be frozen)."),
            _ => Rejected(envelope, "server.not_running", "The server is not running."),
        };
    }

    private CommandAck SubscribeConsole(CommandEnvelope envelope, SubscribeConsoleCommand command)
    {
        if (!_servers.TryGetValue(command.ServerId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        _relay.Subscribe(command.ServerId, server.Supervisor.Console.Buffer, command.FromSequence);
        return new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null);
    }

    /// <summary>The usage of every running server, every few seconds (nothing while none runs).</summary>
    private async Task SendMetricsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.MetricsInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RetryUnsavedRecords();
                var samples = await Task.WhenAll(_servers.Values.Select(s => SampleAsync(s.Supervisor)));
                var running = samples.OfType<ServerMetrics>().ToList();
                if (running.Count > 0)
                {
                    _ = await panel.SendAsync(nameof(IAgentHub.Metrics), new MetricsBatch(clock.GetUtcNow(), running), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The agent is stopping.
        }
    }

    /// <summary>How far the consoles of games in hosts were read, noted when it changed (<see cref="ServerHostOptions.HostCheckpointInterval"/>).</summary>
    private async Task SaveHostCheckpointsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.HostCheckpointInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var server in _servers.Values.Where(s => s.Supervisor.HostState is not null))
                {
                    PersistHost(server.Supervisor.ServerId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The agent is stopping (it notes them once more, exactly).
        }
    }

    private static async Task<ServerMetrics?> SampleAsync(ServerSupervisor supervisor)
    {
        try
        {
            // A supervisor busy starting a process answers late: that sample is skipped, not waited for.
            return await supervisor.SampleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException)
        {
            return null;
        }
    }

    void IServerEventSink.StateChanged(ServerStateChanged change) =>
        _ = panel.SendAsync(nameof(IAgentHub.ServerStateChanged), change, _stopping.Token);

    void IServerEventSink.DesiredChanged(Guid serverId, bool running) => UpdateRecord(serverId, r => r with { DesiredRunning = running });

    void IServerEventSink.HostChanged(Guid serverId) => PersistHost(serverId);

    private CommandAck Provision(CommandEnvelope envelope, ProvisionServerCommand command)
    {
        if (envelope.OperationId is not { } operationId)
        {
            return Rejected(envelope, "command.operation_required", "Provisioning is an operation.");
        }

        var spec = command.Spec;
        if (!ServerShortId.IsValid(spec.ShortId)
            || _servers.Values.Any(s => s.Supervisor.ServerId != command.ServerId && s.ShortId == spec.ShortId)
            || (_servers.TryGetValue(command.ServerId, out var existing) && existing.ShortId != spec.ShortId))
        {
            return Rejected(envelope, "server.invalid_short_id", "The short id is invalid or belongs to another server.");
        }

        GameManifest manifest;
        try
        {
            manifest = Parse(spec);
            if (LaunchPlanRenderer.Check(manifest, Platform, Binding(spec, rconPassword: null)) is { } error)
            {
                return Rejected(envelope, "server.invalid_spec", error);
            }

            if (spec.Limits is { IsValid: false })
            {
                return Rejected(envelope, "server.invalid_spec", "The server's limits are out of range.");
            }

            if (manifest.Install?.Steps.OfType<TestGameServerStep>().Any() == true && !installOptions.TestGameAvailable)
            {
                return Rejected(envelope, "install.test_game_unavailable", "This agent was not started with the test game server.");
            }
        }
        catch (LaunchPlanException ex)
        {
            return Rejected(envelope, "server.invalid_spec", ex.Message);
        }

        // A new RCON password at every provisioning (a reinstall rotates it): generated here, never chosen by anyone. So the
        // server must be stopped, as for an install: a running game (or one taken over after the agent restarts) keeps
        // the password it was started with.
        var rconPassword = manifest.Runtime.GetValueOrDefault(Platform)?.Console.Rcon is null ? null : NewRconPassword();
        const string stopFirst = "Stop the server before provisioning it again.";
        if (_servers.TryGetValue(command.ServerId, out var current) && current.Supervisor.Snapshot.State is not (RuntimeState.Offline or RuntimeState.Crashed))
        {
            return Rejected(envelope, "server.running", stopFirst);
        }

        return RunOperation(envelope, command.ServerId, operationId, stoppedMessage: stopFirst, work: async _ignored =>
        {
            var root = _config!.ServersRootOrDefault(configs.Paths);
            // The record first: should creating the folder or the account fail midway, a delete still finds what to remove.
            lock (_recordLock)
            {
                var desired = store.Load(command.ServerId)?.DesiredRunning ?? false;
                store.Save(new ServerRecord(command.ServerId, spec, desired, ResumeOnStart: false, rconPassword));
            }

            await Task.Run(() => provisioner.Provision(root, spec.ShortId));
            // Before anything runs as the new account (SteamCMD installs as it).
            panelBlock?.Ensure(new ServerAccount(spec.ShortId, provisioner.Describe(root, spec.ShortId).Identity));
            if (_servers.TryGetValue(command.ServerId, out var server))
            {
                await server.Supervisor.UpdateAsync(Definition(command.ServerId, spec, rconPassword));
                _servers[command.ServerId] = server with { Manifest = manifest, Spec = spec, RconPassword = rconPassword };
            }
            else
            {
                Add(command.ServerId, spec, rconPassword);
            }

            LogProvisioned(command.ServerId, spec.ShortId);
        });
    }

    private CommandAck Install(CommandEnvelope envelope, InstallServerCommand command) =>
        RunSteps(envelope, command.ServerId, s => s.Manifest.Install?.Steps ?? [], validate: null, update: false, command.Login);

    private CommandAck Update(CommandEnvelope envelope, UpdateServerCommand command) =>
        _servers.TryGetValue(command.ServerId, out var server) && server.Manifest.Update is null
            ? Rejected(envelope, "server.update_unsupported", "This game has no update steps.")
            : RunSteps(envelope, command.ServerId, s => s.Manifest.Update!.Steps, command.Validate ? true : null, update: true, command.Login);

    private AddonInstaller Addons => new(files, installOptions.HttpHandler);

    /// <summary>Installs one of the manifest's addons, or brings it to its latest release (docs/architecture/04, "Addons").</summary>
    private CommandAck InstallAddon(CommandEnvelope envelope, InstallAddonCommand command) =>
        AddonOperation(envelope, command.ServerId, command.AddonKey, async (server, addon, directory, operationId, cancellationToken) =>
        {
            var record = await Addons.InstallAsync(directory, addon, force: false, (stage, percent) => SendProgressAsync(operationId, stage, percent),
                cancellationToken);
            LogAddonInstalled(command.ServerId, addon.Key, record.Release);
            return new AddonsResult([new AddonRelease(addon.Key, record.Release)]);
        });

    /// <summary>Removes an addon; game files it had replaced come back with a validating update.</summary>
    private CommandAck RemoveAddon(CommandEnvelope envelope, RemoveAddonCommand command) =>
        AddonOperation(envelope, command.ServerId, command.AddonKey, async (server, addon, directory, operationId, cancellationToken) =>
        {
            var replaced = Addons.Remove(directory, addon);
            if (replaced.Count > 0 && server.Manifest.Update is { } update)
            {
                await RunStepsAsync(server, operationId, update.Steps, validate: true, command.Login, cancellationToken);
            }

            LogAddonRemoved(command.ServerId, addon.Key, replaced.Count);
            return new AddonsResult([new AddonRelease(addon.Key, null)]);
        });

    private CommandAck AddonOperation(CommandEnvelope envelope, Guid serverId, string key,
        Func<Server, AddonDefinition, string, Guid, CancellationToken, Task<CommandResult?>> work)
    {
        if (envelope.OperationId is not { } operationId)
        {
            return Rejected(envelope, "command.operation_required", "Changing addons is an operation.");
        }

        if (!_servers.TryGetValue(serverId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        if (server.Manifest.AddonList.FirstOrDefault(a => a.Key == key) is not { } addon)
        {
            return Rejected(envelope, "addon.unknown", "The server's game has no such addon.");
        }

        const string stopFirst = "Stop the server before changing its addons.";
        if (server.Supervisor.Snapshot.State is not (RuntimeState.Offline or RuntimeState.Crashed))
        {
            return Rejected(envelope, "server.running", stopFirst);
        }

        return RunOperation(envelope, serverId, operationId, exclusive: true, async cancellationToken =>
        {
            var provisioned = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), server.ShortId);
            TerminateStrays(serverId, provisioned.Identity);
            return await work(server, addon, provisioned.Directory, operationId, cancellationToken);
        }, stopFirst);
    }

    /// <summary>Install or update: the manifest's steps, SteamCMD ones as the server's account, the others by the agent.</summary>
    /// <param name="update">An update: the installed addons the game's update overwrites are installed again afterwards.</param>
    private CommandAck RunSteps(CommandEnvelope envelope, Guid serverId, Func<Server, IReadOnlyList<InstallStep>> steps, bool? validate, bool update,
        SteamLogin? login)
    {
        if (envelope.OperationId is not { } operationId)
        {
            return Rejected(envelope, "command.operation_required", "Installing is an operation.");
        }

        if (!_servers.TryGetValue(serverId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        const string stopFirst = "Stop the server before installing it.";
        if (server.Supervisor.Snapshot.State is not (RuntimeState.Offline or RuntimeState.Crashed))
        {
            return Rejected(envelope, "server.running", stopFirst);
        }

        return RunOperation(envelope, serverId, operationId, exclusive: true, stoppedMessage: stopFirst, work: async cancellationToken =>
        {
            var directory = await RunStepsAsync(server, operationId, steps(server), validate, login, cancellationToken);
            LogInstalled(serverId);
            if (!update)
            {
                return null;
            }

            // Oxide and the like: the game's update put its own files back. What is installed is what this node recorded
            // (the spec catches up only at the next start).
            var reinstalled = new List<AddonRelease>();
            foreach (var addon in server.Manifest.AddonList.Where(a => a.ReinstallAfterUpdate && Addons.Read(directory, a.Key) is not null))
            {
                var record = await Addons.InstallAsync(directory, addon, force: true, (stage, percent) => SendProgressAsync(operationId, stage, percent),
                    cancellationToken);
                LogAddonInstalled(serverId, addon.Key, record.Release);
                reinstalled.Add(new AddonRelease(addon.Key, record.Release));
            }

            return reinstalled.Count > 0 ? new AddonsResult(reinstalled) : null;
        });
    }

    /// <summary>Runs install or update steps; returns the server's folder.</summary>
    /// <param name="login">The Steam account SteamCMD signs in with; null: anonymous.</param>
    private async Task<string> RunStepsAsync(Server server, Guid operationId, IReadOnlyList<InstallStep> steps, bool? validate, SteamLogin? login,
        CancellationToken cancellationToken)
    {
        var serverId = server.Supervisor.ServerId;
        var provisioned = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), server.ShortId);
        TerminateStrays(serverId, provisioned.Identity);
        foreach (var step in steps)
        {
            if (step is SteamCmdStep steam)
            {
                if (steam.NeedsAccount && login is null)
                {
                    throw new GameProcessException("steamcmd.account_required", "Only a Steam account that owns this game's dedicated server may download it.");
                }

                var betaPassword = steam.BetaPasswordVar is { } key ? server.Spec.Variables.GetValueOrDefault(key) : null;
                var job = new SteamCmdJob(serverId, provisioned.Directory, provisioned.Identity, steam.AppId, steam.Beta, betaPassword,
                    validate ?? steam.Validate, login);
                await steamCmd.RunAsync(job, (stage, percent) => SendProgressAsync(operationId, stage, percent), cancellationToken);
            }
            else
            {
                await installSteps.RunAsync(step, provisioned.Directory, VariableValues.Resolve(server.Manifest, server.Spec.Variables),
                    (stage, percent) => SendProgressAsync(operationId, stage, percent), cancellationToken);
            }
        }

        return provisioned.Directory;
    }

    /// <summary>New variables, ports, arguments or restart policy for the next start; never a different manifest.</summary>
    private async Task<CommandAck> ApplySpecAsync(CommandEnvelope envelope, ApplyServerSpecCommand command)
    {
        if (!_servers.TryGetValue(command.ServerId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        var spec = command.Spec;
        if (spec.ShortId != server.ShortId || spec.Manifest != server.Spec.Manifest)
        {
            return Rejected(envelope, "server.spec_mismatch", "A spec can change values, not the server's short id or manifest.");
        }

        ServerDefinition definition;
        try
        {
            definition = Definition(command.ServerId, spec, server.RconPassword);
        }
        catch (LaunchPlanException ex)
        {
            return Rejected(envelope, "server.invalid_spec", ex.Message);
        }

        lock (_recordLock)
        {
            var record = store.Load(command.ServerId);
            store.Save(new ServerRecord(command.ServerId, spec, record?.DesiredRunning ?? false, record?.ResumeOnStart ?? false, server.RconPassword));
        }

        await server.Supervisor.UpdateAsync(definition);
        _servers[command.ServerId] = server with { Spec = spec };
        LogSpecApplied(command.ServerId);
        return new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null);
    }

    private CommandAck Cancel(CommandEnvelope envelope, CancelOperationCommand command)
    {
        if (!_cancellations.TryGetValue(command.OperationId, out var cancellation))
        {
            return new CommandAck(envelope.CommandId, AckStatus.AlreadyInState, null, null);
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished meanwhile.
        }

        return new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null);
    }

    private void TerminateStrays(Guid serverId, ProcessIdentity identity)
    {
        if (processes.TerminateStrayProcesses(identity) is > 0 and var killed)
        {
            LogStraysKilled(serverId, killed);
        }
    }

    private CommandAck Delete(CommandEnvelope envelope, DeleteServerCommand command)
    {
        if (envelope.OperationId is not { } operationId)
        {
            return Rejected(envelope, "command.operation_required", "Deleting is an operation.");
        }

        if (!_servers.TryGetValue(command.ServerId, out var server))
        {
            ServerRecord? record;
            lock (_recordLock)
            {
                record = store.Load(command.ServerId);
            }

            if (record is not null)
            {
                // An earlier delete stopped the server but failed to clean up (the account could not be deleted, say):
                // its record is still here, so this retry finishes the job.
                return RunOperation(envelope, command.ServerId, operationId, _ignored =>
                    CleanUpDeletedAsync(command.ServerId, record.Spec.ShortId));
            }

            if (store.SetAsideRecord(command.ServerId) is { } setAside)
            {
                // Its record could not be read and was set aside: the short id naming its directory and account is inside
                // it, so nothing can be cleaned up safely. Said as a failure rather than "gone", with what is left.
                return RunOperation(envelope, command.ServerId, operationId, _ignored => Task.FromException(new GameProcessException(
                    "server.record_unreadable",
                    $"The node could not read this server's record ({setAside}: corrupt, or protected on another machine), so it cannot tell which " +
                    "folder and account were the server's: they are still on the node. Remove the server's folder (named after its short id, in the " +
                    "servers root) and its local account by hand, delete that file, and delete the server again.")));
            }

            // Already gone (a retried delete): report success so the panel can finish.
            var gone = new OperationCompleted(operationId, command.ServerId, OperationOutcome.Succeeded, null, null);
            KeepResult(gone);
            _ = DeliverResultAsync(gone);
            return new CommandAck(envelope.CommandId, AckStatus.AlreadyInState, null, null);
        }

        return RunOperation(envelope, command.ServerId, operationId, async _ignored =>
        {
            await server.Supervisor.KillAsync();
            await server.Supervisor.WaitUntilSettledAsync();
            await server.Supervisor.DisposeAsync();
            _servers.TryRemove(command.ServerId, out _);
            _relay.Unsubscribe(command.ServerId);
            await server.Supervisor.Console.DisposeAsync();
            await CleanUpDeletedAsync(command.ServerId, server.ShortId);
        });
    }

    /// <summary>
    /// What a deleted server leaves on disk, its record last: until everything else is gone, the record lets a retry of
    /// the delete (or the next start of the agent) find the server again.
    /// </summary>
    private async Task CleanUpDeletedAsync(Guid serverId, string shortId)
    {
        EndStrandedHost(serverId);
        DeleteConsoleLogs(shortId);
        var root = _config!.ServersRootOrDefault(configs.Paths);
        // Before the account goes: a process of a deleted account would keep running unnoticed.
        TerminateStrays(serverId, provisioner.Describe(root, shortId).Identity);
        firewall.Remove(shortId);
        await Task.Run(() => provisioner.Deprovision(root, shortId), CancellationToken.None); // never left half done
        DeleteBackups(shortId);
        ForgetHost(serverId);
        lock (_recordLock)
        {
            store.Delete(serverId);
        }

        LogDeleted(serverId, shortId);
    }

    /// <summary>A backup while the server keeps running (docs/architecture/05: starting, stopping and editing files stay allowed).</summary>
    private CommandAck CreateBackup(CommandEnvelope envelope, CreateBackupCommand command)
    {
        if (envelope.OperationId is not { } operationId)
        {
            return Rejected(envelope, "command.operation_required", "A backup is an operation.");
        }

        if (!_servers.TryGetValue(command.ServerId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        var location = Backup(server, command.BackupId);
        return RunOperation(envelope, command.ServerId, operationId, exclusive: false, async cancellationToken =>
        {
            var provisioned = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), server.ShortId);
            BackupProgress progress = (stage, percent) => SendProgressAsync(operationId, stage, percent);
            BackupResult made;
            await using (var source = await BackupSource.OpenAsync(server.Supervisor, provisioned, server.Manifest.Backup?.Save,
                options.BackupSnapshots ? snapshots ?? s_noSnapshots : null, store, progress, clock, _logger, cancellationToken))
            {
                made = await backups.CreateAsync(location, source.Files, server.Manifest.Backup, progress, cancellationToken) with
                {
                    Capture = source.CaptureAfterReading(),
                };
            }

            return command.Remote is { } remote ? await backups.CopyToRemoteAsync(location, made, remote, progress, cancellationToken) : made;
        });
    }

    /// <summary>A restore needs the server stopped, and nothing else happens to it until the restore ends.</summary>
    private CommandAck RestoreBackup(CommandEnvelope envelope, RestoreBackupCommand command)
    {
        if (envelope.OperationId is not { } operationId)
        {
            return Rejected(envelope, "command.operation_required", "A restore is an operation.");
        }

        if (!_servers.TryGetValue(command.ServerId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        const string stopFirst = "Stop the server before restoring a backup.";
        if (server.Supervisor.Snapshot.State is not (RuntimeState.Offline or RuntimeState.Crashed))
        {
            return Rejected(envelope, "server.running", stopFirst);
        }

        var location = Backup(server, command.BackupId);
        return RunOperation(envelope, command.ServerId, operationId, stoppedMessage: stopFirst, work: async cancellationToken =>
        {
            var provisioned = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), server.ShortId);
            TerminateStrays(command.ServerId, provisioned.Identity);
            BackupProgress progress = (stage, percent) => SendProgressAsync(operationId, stage, percent);
            if (command.Remote is { } remote)
            {
                await backups.FetchAsync(location, remote, command.Sha256, command.SizeBytes, progress, cancellationToken);
            }

            await backups.RestoreAsync(location, provisioned, command.Sha256, command.Clean ? new CleanRestore(server.Manifest.Backup) : null,
                progress, cancellationToken);
        });
    }

    /// <summary>The archive, then its remote copy: the panel forgets the backup only when both are gone.</summary>
    private async Task<CommandAck> DeleteBackupAsync(CommandEnvelope envelope, DeleteBackupCommand command)
    {
        if (!_servers.TryGetValue(command.ServerId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        try
        {
            var deleted = await Task.Run(() => backups.Delete(Backup(server, command.BackupId)));
            if (command.Remote is { } remote)
            {
                using var timeout = new CancellationTokenSource(s_remoteDeleteTimeout);
                await backups.DeleteRemoteAsync(remote, timeout.Token);
                deleted = true;
            }

            return new CommandAck(envelope.CommandId, deleted ? AckStatus.Accepted : AckStatus.AlreadyInState, null, null);
        }
        catch (BackupException ex)
        {
            return Rejected(envelope, ex.Code, ex.Message);
        }
    }

    private async Task<CommandAck> SendBackupAsync(CommandEnvelope envelope, SendBackupCommand command)
    {
        if (!_servers.TryGetValue(command.ServerId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        var location = Backup(server, command.BackupId);
        try
        {
            var opened = await backups.OpenAsync(location, command.Remote, CancellationToken.None);
            CommandAck ack;
            try
            {
                ack = fileCommands.Send(envelope, command.Ticket, $"backup {command.BackupId}", () => opened);
            }
            catch
            {
                opened.Dispose();
                throw;
            }

            if (ack.Status != AckStatus.Accepted)
            {
                opened.Dispose(); // refused before the transfer took it: the archive must not stay locked
            }

            return ack;
        }
        catch (BackupException ex)
        {
            return Rejected(envelope, ex.Code, ex.Message);
        }
    }

    private string BackupsRoot => _config!.BackupsRootOrDefault(configs.Paths);

    private BackupLocation Backup(Server server, Guid backupId) => new(BackupsRoot, server.ShortId, backupId);

    /// <summary>A deleted server's backups go with it (the panel forgets them too).</summary>
    private void DeleteBackups(string shortId)
    {
        try
        {
            backups.DeleteAll(BackupsRoot, shortId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBackupsNotDeleted(shortId, ex.Message);
        }
    }

    /// <summary>An operation that keeps the server from starting and its files from changing is running.</summary>
    private bool Exclusive(Guid serverId) => _operations.TryGetValue(serverId, out var operation) && operation.Exclusive;

    private async Task<CommandAck> Control(CommandEnvelope envelope, Guid serverId, Func<ServerSupervisor, Task<AckStatus>> action,
        bool allowedDuringOperation)
    {
        if (!_servers.TryGetValue(serverId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        if (!allowedDuringOperation && Exclusive(serverId))
        {
            return new CommandAck(envelope.CommandId, AckStatus.Busy, "server.busy", "An operation is running on this server.");
        }

        RecheckUnverifiableHost(serverId);
        if (_strandedHosts.TryGetValue(serverId, out var stranded))
        {
            // Its game still runs in a host this agent cannot drive, and the panel shows the server offline (this agent
            // reports no game): a stop or kill ends that host (and the game); a start, the one action the panel offers
            // for an offline server, ends it first instead of running a second copy beside it. Ending it is a kill: the
            // game's console and Ctrl+C belong to that host, which does not speak this agent's protocol.
            if (envelope.Command is StopServerCommand or KillServerCommand)
            {
                var ended = EndStrandedHost(server);
                return _strandedHosts.ContainsKey(serverId)
                    ? HostNotEnded(envelope)
                    : new CommandAck(envelope.CommandId, ended ? AckStatus.Accepted : AckStatus.AlreadyInState, null, null);
            }

            if (envelope.Command is not StartServerCommand)
            {
                return stranded.Unverifiable
                    ? HostNotEnded(envelope)
                    : Rejected(envelope, "server.host_incompatible",
                        "The server's game still runs in a host process of another version of the agent, which this agent cannot manage. Stop or " +
                        "start the server to end it.");
            }

            EndStrandedHost(server);
            if (_strandedHosts.ContainsKey(serverId))
            {
                return HostNotEnded(envelope);
            }
        }

        // The supervisor refuses a start once an exclusive operation has begun, even one that began after the check above.
        var status = await action(server.Supervisor);
        return status == AckStatus.Busy
            ? new CommandAck(envelope.CommandId, AckStatus.Busy, "server.busy", "An operation is running on this server.")
            : new CommandAck(envelope.CommandId, status, null, null);
    }

    private CommandAck RunOperation(CommandEnvelope envelope, Guid serverId, Guid operationId, Func<CancellationToken, Task> work,
        string? stoppedMessage = null) =>
        RunOperation(envelope, serverId, operationId, exclusive: true, async cancellationToken =>
        {
            await work(cancellationToken);
            return null;
        }, stoppedMessage);

    /// <param name="exclusive">
    /// Nothing else may happen to the server meanwhile (everything but a backup): its supervisor refuses to start it until
    /// the operation ends (<see cref="ServerSupervisor.TryBeginExclusive"/>).
    /// </param>
    /// <param name="work">Returns what the operation produced, for its result.</param>
    /// <param name="stoppedMessage">
    /// The server must be stopped (the refusal's message, <c>server.running</c>). Checked with the supervisor's own
    /// state, so a start that was accepted but not handled yet is never missed.
    /// </param>
    private CommandAck RunOperation(CommandEnvelope envelope, Guid serverId, Guid operationId, bool exclusive,
        Func<CancellationToken, Task<CommandResult?>> work, string? stoppedMessage = null)
    {
        var running = new RunningOperation(operationId, exclusive);
        if (!_operations.TryAdd(serverId, running))
        {
            return new CommandAck(envelope.CommandId, AckStatus.Busy, "server.busy", "Another operation is running on this server.");
        }

        var gate = exclusive && _servers.TryGetValue(serverId, out var server) ? server.Supervisor : null;
        if (gate is not null && !gate.TryBeginExclusive(requireStopped: stoppedMessage is not null))
        {
            _operations.TryRemove(new KeyValuePair<Guid, RunningOperation>(serverId, running));
            return Rejected(envelope, "server.running", stoppedMessage ?? "The server is busy.");
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_endingOperations.Token);
        _cancellations[operationId] = cancellation;
        _ = Task.Run(async () =>
        {
            OperationCompleted result;
            try
            {
                var produced = await work(cancellation.Token);
                result = new OperationCompleted(operationId, serverId, OperationOutcome.Succeeded, null, null, produced);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                LogOperationCancelled(operationId, serverId);
                result = new OperationCompleted(operationId, serverId, OperationOutcome.Cancelled, "operation.cancelled",
                    _endingOperations.IsCancellationRequested ? "The agent stopped." : "Cancelled.");
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the panel must learn that the operation ended.
                LogOperationFailed(ex, operationId, serverId);
                var code = ex switch
                {
                    GameProcessException process => process.Code,
                    FileManagerException file => file.Code,
                    BackupException backup => backup.Code,
                    _ => "operation.failed",
                };
                result = new OperationCompleted(operationId, serverId, OperationOutcome.Failed, code, ex.Message);
            }
            finally
            {
                _cancellations.TryRemove(operationId, out _);
                cancellation.Dispose();
                gate?.EndExclusive();
            }

            // In the outbox before the operation counts as ended: an agent that stops now still reports it.
            KeepResult(result);
            _operations.TryRemove(new KeyValuePair<Guid, RunningOperation>(serverId, running));
            running.Ended.TrySetResult();
            await DeliverResultAsync(result);
        });
        return new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null);
    }

    /// <summary>The agent is stopping: cancels the operations in flight and waits for them to end, at most <paramref name="limit"/>.</summary>
    private async Task EndOperationsAsync(TimeSpan limit)
    {
        await _endingOperations.CancelAsync();
        var ending = _operations.Values.Select(o => o.Ended.Task).ToList();
        try
        {
            await Task.WhenAll(ending).WaitAsync(limit, clock);
        }
        catch (TimeoutException)
        {
            LogOperationsLeft(ending.Count(t => !t.IsCompleted));
        }
    }

    /// <summary>
    /// Puts an operation's result in the outbox. One that cannot be written (a full disk) is logged and kept in memory:
    /// it is still delivered, counts as in flight for the Hello, and its write is retried with the records'
    /// (<see cref="RetryUnsavedResults"/>).
    /// </summary>
    private void KeepResult(OperationCompleted result)
    {
        try
        {
            store.SaveResult(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _unsavedResults[result.OperationId] = result;
            LogResultNotSaved(result.OperationId, ex.Message);
        }
    }

    private void RetryUnsavedResults()
    {
        foreach (var (operationId, result) in _unsavedResults)
        {
            try
            {
                store.SaveResult(result);
                if (!_unsavedResults.TryRemove(new KeyValuePair<Guid, OperationCompleted>(operationId, result)))
                {
                    store.DeleteResult(operationId); // delivered meanwhile
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogResultNotSaved(operationId, ex.Message);
            }
        }
    }

    /// <summary>Sends a result kept in the outbox (or in memory), and removes it from there once the panel has it.</summary>
    private async Task DeliverResultAsync(OperationCompleted result)
    {
        if (await panel.DeliverAsync(nameof(IAgentHub.OperationCompleted), result, _stopping.Token))
        {
            Delivered(result.OperationId);
        }
    }

    private async Task DeliverPendingResultsAsync(CancellationToken cancellationToken)
    {
        foreach (var result in store.PendingResults().Concat(_unsavedResults.Values).DistinctBy(r => r.OperationId))
        {
            if (!await panel.DeliverAsync(nameof(IAgentHub.OperationCompleted), result, cancellationToken))
            {
                return;
            }

            Delivered(result.OperationId);
        }
    }

    private void Delivered(Guid operationId)
    {
        _unsavedResults.TryRemove(operationId, out _);
        store.DeleteResult(operationId);
    }

    /// <summary>
    /// The Hello carries a snapshot of every server, but the channel opens only once the panel accepted it: a server
    /// that changed state in between (one the agent resumed as it started, say) would stay wrong in the panel. Each
    /// server's latest change is delivered again; the panel ignores the ones it already has (same sequence).
    /// </summary>
    private async Task DeliverLatestStatesAsync(CancellationToken cancellationToken)
    {
        foreach (var change in _servers.Values.Select(s => s.Supervisor.LastChange).OfType<ServerStateChanged>())
        {
            if (!await panel.DeliverAsync(nameof(IAgentHub.ServerStateChanged), change, cancellationToken))
            {
                return;
            }
        }
    }

    private async Task SendProgressAsync(Guid operationId, string stage, double? percent) =>
        _ = await panel.SendAsync(nameof(IAgentHub.OperationProgress), new OperationProgress(operationId, stage, percent, null), _stopping.Token);

    private ServerSupervisor Add(Guid serverId, ServerRuntimeSpec spec, string? rconPassword)
    {
        var definition = Definition(serverId, spec, rconPassword);
        var log = options.ConsoleLog is { } logOptions
            ? new ConsoleLog(ConsoleLogDirectory(spec.ShortId), logOptions, clock, loggerFactory.CreateLogger<ConsoleLog>())
            : null;
        var console = new ServerConsole(new ConsoleBuffer(clock, options.ConsoleBufferCapacity), log);
        // Games run in host processes when the agent has them; installs keep running SteamCMD itself (doc 06).
        var supervisor = new ServerSupervisor(definition, (IGameProcessManager?)Hosts ?? processes, this, clock, supervisorOptions,
            loggerFactory.CreateLogger<ServerSupervisor>(), files, rcon, firewall, console, query);
        _servers[serverId] = new Server(spec.ShortId, Parse(spec), spec, rconPassword, supervisor);
        return supervisor;
    }

    /// <summary>The servers and the identities their games run as, for their accounts' outbound blocks.</summary>
    private IReadOnlyCollection<ServerAccount> ServerAccounts()
    {
        var root = _config!.ServersRootOrDefault(configs.Paths);
        return [.. _servers.Values.Select(s => new ServerAccount(s.ShortId, provisioner.Describe(root, s.ShortId).Identity))];
    }

    private string ConsoleLogDirectory(string shortId)
    {
        ServerShortId.Validate(shortId);
        return Path.Combine(configs.Paths.ConsoleLogsDirectory, shortId);
    }

    /// <summary>
    /// A Steam login lent to the server's SteamCMD is taken back when the run ends; should the agent have stopped during
    /// one, it is still in the server's folder. No SteamCMD runs while the agent starts.
    /// </summary>
    private void DeleteLentSteamLogin(Guid serverId, string shortId)
    {
        try
        {
            files.Delete(provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), shortId).Directory, SteamCmdRunner.LentLogin);
        }
        catch (Exception ex) when (ex is GameProcessException or IOException or UnauthorizedAccessException)
        {
            LogSteamLoginNotDeleted(serverId, ex.Message);
        }
    }

    private void DeleteConsoleLogs(string shortId)
    {
        try
        {
            var directory = ConsoleLogDirectory(shortId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogConsoleLogsNotDeleted(shortId, ex.Message);
        }
    }

    private ServerDefinition Definition(Guid serverId, ServerRuntimeSpec spec, string? rconPassword)
    {
        var provisioned = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), spec.ShortId);
        if (spec.Limits is { IsValid: false })
        {
            throw new LaunchPlanException("The server's limits are out of range.");
        }

        var plan = LaunchPlanRenderer.Render(Parse(spec), Platform, Binding(spec, rconPassword) with { Directory = provisioned.Directory });
        return new ServerDefinition(serverId, spec.ShortId, provisioned.Directory, provisioned.Identity, plan, spec.AutoStart, spec.RestartOnCrash,
            ProcessLimits.From(spec.Limits));
    }

    private static ServerBinding Binding(ServerRuntimeSpec spec, string? rconPassword) => new(spec.ShortId, spec.Variables,
        spec.Ports?.ToDictionary(p => p.Key, p => new PortAssignment(p.Value.IpAddress, p.Value.Port)), spec.ExtraArguments, rconPassword,
        Addons: spec.Addons);

    /// <summary>32 random letters and digits (about 190 bits): RCON is exposed on the game's TCP port (docs/architecture/08, T18).</summary>
    private static string NewRconPassword() =>
        System.Security.Cryptography.RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 32);

    private static GameManifest Parse(ServerRuntimeSpec spec)
    {
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(spec.Manifest));
        return result.IsValid
            ? result.Manifest!
            : throw new LaunchPlanException("Invalid manifest: " + string.Join("; ", result.Errors));
    }

    /// <summary>
    /// Changes a server's record; never fails the caller (a state change of the server, the agent's start or stop). A
    /// record that cannot be written (a full disk, a file another program holds) is logged and its change kept: it is
    /// written with the record's next change, or retried with the metrics (<see cref="RetryUnsavedRecords"/>).
    /// </summary>
    private void UpdateRecord(Guid serverId, Func<ServerRecord, ServerRecord> change)
    {
        lock (_recordLock)
        {
            if (_unsavedRecords.TryRemove(serverId, out var earlier))
            {
                var later = change;
                change = r => later(earlier(r));
            }

            try
            {
                if (store.Load(serverId) is { } record)
                {
                    store.Save(change(record));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _unsavedRecords[serverId] = change;
                LogRecordNotSaved(serverId, ex.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Unreadable (set aside at the agent's next start): there is nothing to change.
                LogRecordNotChanged(ex, serverId);
            }
        }
    }

    private void RetryUnsavedRecords()
    {
        foreach (var serverId in _unsavedRecords.Keys)
        {
            UpdateRecord(serverId, r => r);
        }

        RetryUnsavedResults();
    }

    private static CommandAck Rejected(CommandEnvelope envelope, string code, string message) =>
        new(envelope.CommandId, AckStatus.Rejected, code, message);

    private static CommandAck HostNotEnded(CommandEnvelope envelope) =>
        Rejected(envelope, "server.host_not_ended", "The server's game still runs in a host process that could not be ended or checked " +
            "(see the agent's log); try again. It is never started a second time beside it.");

    /// <summary>
    /// A game left running in a host this agent cannot drive: one of another protocol version, or one that could not be
    /// checked (its process cannot be opened). It is also the lock of the commands that end it.
    /// </summary>
    private sealed class StrandedHost(GameHostRecord record, bool unverifiable)
    {
        public GameHostRecord Record { get; } = record;

        public bool Unverifiable { get; } = unverifiable;
    }

    private sealed record RunningOperation(Guid Id, bool Exclusive)
    {
        /// <summary>Completes once the operation's work has ended, whatever its outcome.</summary>
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Server(string ShortId, GameManifest Manifest, ServerRuntimeSpec Spec, string? RconPassword, ServerSupervisor Supervisor)
    {
        // Holds secrets: never print them.
        public override string ToString() => $"Server {{ ShortId = {ShortId} }}";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent stopping: {Count} game servers keep running in their host processes")]
    private partial void LogLeftRunning(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: its host process is gone, and its game with it")]
    private partial void LogHostGone(Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} is not on this node anymore: killing its host process {HostProcessId}")]
    private partial void LogOrphanHost(Guid serverId, int hostProcessId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Server {ServerId} is not loaded, but its game still runs in its host process {HostProcessId}: left running, and its host record kept")]
    private partial void LogHostOfUnloadedServer(Guid serverId, int hostProcessId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Server {ServerId}: games run in the agent now (Pulsar:ServerHosts is off); ending the host process {HostProcessId} an earlier run left")]
    private partial void LogLeftHostEnded(Guid serverId, int hostProcessId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: ending its host process {HostProcessId} of another version of the agent, and its game")]
    private partial void LogStrandedHostEnded(Guid serverId, int hostProcessId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted a request to stop the game servers left by an earlier run of the agent")]
    private partial void LogStaleStopServersMarker();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete an old request to stop the game servers: {Reason}")]
    private partial void LogStopServersMarkerNotDeleted(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: where its game runs could not be saved: {Reason}")]
    private partial void LogHostNotSaved(Guid serverId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId}: its record could not be saved ({Reason}); trying again in a few seconds")]
    private partial void LogRecordNotSaved(Guid serverId, string reason);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The result of operation {OperationId} could not be saved ({Reason}); it is delivered from memory, and saving it is tried again in a few seconds")]
    private partial void LogResultNotSaved(Guid operationId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId}: its record could not be read to change it")]
    private partial void LogRecordNotChanged(Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Servers loaded: {Count} (servers root {ServersRoot})")]
    private partial void LogLoaded(int count, string serversRoot);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId} could not be loaded from its stored spec")]
    private partial void LogServerNotLoaded(Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId} provisioned ({ShortId})")]
    private partial void LogProvisioned(Guid serverId, string shortId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId} installed")]
    private partial void LogInstalled(Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: addon {Addon} installed ({Release})")]
    private partial void LogAddonInstalled(Guid serverId, string addon, string release);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: addon {Addon} removed ({Replaced} game files to restore)")]
    private partial void LogAddonRemoved(Guid serverId, string addon, int replaced);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId} ({ShortId}) deleted from this node")]
    private partial void LogDeleted(Guid serverId, string shortId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: new runtime spec applied")]
    private partial void LogSpecApplied(Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: killed {Count} processes its account ran outside the server's job")]
    private partial void LogStraysKilled(Guid serverId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The console history of deleted server {ShortId} could not be removed: {Reason}")]
    private partial void LogConsoleLogsNotDeleted(string shortId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The backups of deleted server {ShortId} could not be removed: {Reason}")]
    private partial void LogBackupsNotDeleted(string shortId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unfinished backups could not be cleaned up: {Reason}")]
    private partial void LogBackupsNotCleaned(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: a Steam login left in its folder could not be removed: {Reason}")]
    private partial void LogSteamLoginNotDeleted(Guid serverId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Operation {OperationId} on server {ServerId} cancelled")]
    private partial void LogOperationCancelled(Guid operationId, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent stopping: {Count} operations had not ended when their time was up; the agent stops without waiting for them")]
    private partial void LogOperationsLeft(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} on server {ServerId} failed")]
    private partial void LogOperationFailed(Exception exception, Guid operationId, Guid serverId);
}
