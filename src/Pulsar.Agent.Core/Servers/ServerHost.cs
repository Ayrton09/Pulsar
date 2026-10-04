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
    /// <summary>How long servers get to stop gracefully when the agent stops (the service control manager waits ~30 s).</summary>
    public TimeSpan ShutdownBudget { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often the usage of running servers is sent to the panel.</summary>
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Characters of console output each server keeps in memory for browsers that start watching.</summary>
    public int ConsoleBufferCapacity { get; set; } = ConsoleBuffer.DefaultCapacity;

    /// <summary>The console history on disk; null keeps none.</summary>
    public ConsoleLogOptions? ConsoleLog { get; set; } = new();
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
    ILoggerFactory loggerFactory) : IHostedService, IServerEventSink, IDisposable
{
    // The panel waits for the ack: deleting a remote copy gets a bounded time.
    private static readonly TimeSpan s_remoteDeleteTimeout = TimeSpan.FromSeconds(20);

    private const string Platform = ManifestValidator.WindowsX64;

    private readonly ILogger _logger = loggerFactory.CreateLogger<ServerHost>();
    private readonly ConcurrentDictionary<Guid, Server> _servers = new();
    private readonly ConcurrentDictionary<Guid, RunningOperation> _operations = new(); // server → operation in flight
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new(); // operation → its cancellation
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _recordLock = new();
    private readonly ConsoleRelay _relay = new(panel, clock);
    private AgentConfig? _config;
    private Task _metrics = Task.CompletedTask;

    /// <summary>Completes once the stored servers are loaded (the Hello waits for it).</summary>
    public Task Loaded => _loaded.Task;

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
            if (installOptions.TestGameAvailable)
            {
                yield return AgentCapabilities.TestGame;
            }

            if (firewall.Enabled)
            {
                yield return AgentCapabilities.Firewall;
            }
        }
    }

    public void Dispose()
    {
        _relay.Dispose();
        _stopping.Dispose();
    }

    public IReadOnlyList<ServerSnapshot> Snapshots() => [.. _servers.Values.Select(s => s.Supervisor.Snapshot)];

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
    public IReadOnlyList<Guid> OperationsInFlight() => [.. _operations.Values.Select(o => o.Id), .. store.PendingResults().Select(r => r.OperationId)];

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

        var resume = new List<ServerSupervisor>();
        foreach (var record in store.LoadAll())
        {
            try
            {
                var supervisor = Add(record.ServerId, record.Spec, record.RconPassword);
                if (record.Spec.AutoStart || record.ResumeOnStart)
                {
                    resume.Add(supervisor);
                }
            }
            catch (Exception ex) when (ex is LaunchPlanException or GameProcessException or ArgumentException)
            {
                LogServerNotLoaded(ex, record.ServerId);
            }
        }

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

        _loaded.TrySetResult();
        _metrics = SendMetricsAsync(_stopping.Token);
        foreach (var supervisor in resume)
        {
            UpdateRecord(supervisor.ServerId, r => r with { ResumeOnStart = false });
            await supervisor.StartAsync(StateChangeReason.AgentStarted);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await fileCommands.StopAsync();
        var servers = _servers.Values.ToList();
        var results = await Task.WhenAll(servers.Select(s => s.Supervisor.ShutdownAsync(options.ShutdownBudget)));
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

        return await server.Supervisor.SendInputAsync(command.Line) == AckStatus.Accepted
            ? new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null)
            : Rejected(envelope, "server.not_running", "The server is not running.");
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

            if (manifest.Install?.Steps.OfType<TestGameServerStep>().Any() == true && !installOptions.TestGameAvailable)
            {
                return Rejected(envelope, "install.test_game_unavailable", "This agent was not started with the test game server.");
            }
        }
        catch (LaunchPlanException ex)
        {
            return Rejected(envelope, "server.invalid_spec", ex.Message);
        }

        // A new RCON password at every provisioning (a reinstall rotates it): generated here, never chosen by anyone.
        var rconPassword = manifest.Runtime.GetValueOrDefault(Platform)?.Console.Rcon is null ? null : NewRconPassword();

        return RunOperation(envelope, command.ServerId, operationId, async _ignored =>
        {
            var root = _config!.ServersRootOrDefault(configs.Paths);
            await Task.Run(() => provisioner.Provision(root, spec.ShortId));
            var desired = false;
            lock (_recordLock)
            {
                desired = store.Load(command.ServerId)?.DesiredRunning ?? false;
                store.Save(new ServerRecord(command.ServerId, spec, desired, ResumeOnStart: false, rconPassword));
            }

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
        RunSteps(envelope, command.ServerId, s => s.Manifest.Install?.Steps ?? [], validate: null);

    private CommandAck Update(CommandEnvelope envelope, UpdateServerCommand command) =>
        _servers.TryGetValue(command.ServerId, out var server) && server.Manifest.Update is null
            ? Rejected(envelope, "server.update_unsupported", "This game has no update steps.")
            : RunSteps(envelope, command.ServerId, s => s.Manifest.Update!.Steps, command.Validate ? true : null);

    /// <summary>Install or update: the manifest's steps, SteamCMD ones as the server's account, the others by the agent.</summary>
    private CommandAck RunSteps(CommandEnvelope envelope, Guid serverId, Func<Server, IReadOnlyList<InstallStep>> steps, bool? validate)
    {
        if (envelope.OperationId is not { } operationId)
        {
            return Rejected(envelope, "command.operation_required", "Installing is an operation.");
        }

        if (!_servers.TryGetValue(serverId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        if (server.Supervisor.Snapshot.State is not (RuntimeState.Offline or RuntimeState.Crashed))
        {
            return Rejected(envelope, "server.running", "Stop the server before installing it.");
        }

        return RunOperation(envelope, serverId, operationId, async cancellationToken =>
        {
            var provisioned = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), server.ShortId);
            TerminateStrays(serverId, provisioned.Identity);
            foreach (var step in steps(server))
            {
                if (step is SteamCmdStep steam)
                {
                    var betaPassword = steam.BetaPasswordVar is { } key ? server.Spec.Variables.GetValueOrDefault(key) : null;
                    var job = new SteamCmdJob(serverId, provisioned.Directory, provisioned.Identity, steam.AppId, steam.Beta, betaPassword,
                        validate ?? steam.Validate);
                    await steamCmd.RunAsync(job, (stage, percent) => SendProgressAsync(operationId, stage, percent), cancellationToken);
                }
                else
                {
                    await SendProgressAsync(operationId, InstallStages.Installing, null);
                    await Task.Run(() => installSteps.Run(step, provisioned.Directory), cancellationToken);
                }
            }

            LogInstalled(serverId);
        });
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
            // Already gone (a retried delete): report success so the panel can finish.
            _ = CompleteAsync(new OperationCompleted(operationId, command.ServerId, OperationOutcome.Succeeded, null, null));
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
            DeleteConsoleLogs(server.ShortId);
            var root = _config!.ServersRootOrDefault(configs.Paths);
            // Before the account goes: a process of a deleted account would keep running unnoticed.
            TerminateStrays(command.ServerId, provisioner.Describe(root, server.ShortId).Identity);
            firewall.Remove(server.ShortId, [.. server.Manifest.PortList.Select(p => p.Name)]);
            await Task.Run(() => provisioner.Deprovision(root, server.ShortId), CancellationToken.None); // never left half done
            DeleteBackups(server.ShortId);
            lock (_recordLock)
            {
                store.Delete(command.ServerId);
            }

            LogDeleted(command.ServerId, server.ShortId);
        });
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
            var made = await backups.CreateAsync(location, provisioned, server.Manifest.Backup, progress, cancellationToken);
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

        if (server.Supervisor.Snapshot.State is not (RuntimeState.Offline or RuntimeState.Crashed))
        {
            return Rejected(envelope, "server.running", "Stop the server before restoring a backup.");
        }

        var location = Backup(server, command.BackupId);
        return RunOperation(envelope, command.ServerId, operationId, async cancellationToken =>
        {
            var provisioned = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), server.ShortId);
            TerminateStrays(command.ServerId, provisioned.Identity);
            BackupProgress progress = (stage, percent) => SendProgressAsync(operationId, stage, percent);
            if (command.Remote is { } remote)
            {
                await backups.FetchAsync(location, remote, command.Sha256, progress, cancellationToken);
            }

            await backups.RestoreAsync(location, provisioned, command.Sha256, progress, cancellationToken);
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
            return fileCommands.Send(envelope, command.Ticket, $"backup {command.BackupId}", () => opened);
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

        var status = await action(server.Supervisor);
        return new CommandAck(envelope.CommandId, status, null, null);
    }

    private CommandAck RunOperation(CommandEnvelope envelope, Guid serverId, Guid operationId, Func<CancellationToken, Task> work) =>
        RunOperation(envelope, serverId, operationId, exclusive: true, async cancellationToken =>
        {
            await work(cancellationToken);
            return null;
        });

    /// <param name="exclusive">Nothing else may happen to the server meanwhile (everything but a backup).</param>
    /// <param name="work">Returns what the operation produced, for its result.</param>
    private CommandAck RunOperation(CommandEnvelope envelope, Guid serverId, Guid operationId, bool exclusive,
        Func<CancellationToken, Task<CommandResult?>> work)
    {
        var running = new RunningOperation(operationId, exclusive);
        if (!_operations.TryAdd(serverId, running))
        {
            return new CommandAck(envelope.CommandId, AckStatus.Busy, "server.busy", "Another operation is running on this server.");
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
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
                    _stopping.IsCancellationRequested ? "The agent stopped." : "Cancelled.");
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
                _operations.TryRemove(new KeyValuePair<Guid, RunningOperation>(serverId, running));
            }

            await CompleteAsync(result);
        });
        return new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null);
    }

    private async Task CompleteAsync(OperationCompleted result)
    {
        store.SaveResult(result);
        if (await panel.DeliverAsync(nameof(IAgentHub.OperationCompleted), result, _stopping.Token))
        {
            store.DeleteResult(result.OperationId);
        }
    }

    private async Task DeliverPendingResultsAsync(CancellationToken cancellationToken)
    {
        foreach (var result in store.PendingResults())
        {
            if (!await panel.DeliverAsync(nameof(IAgentHub.OperationCompleted), result, cancellationToken))
            {
                return;
            }

            store.DeleteResult(result.OperationId);
        }
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
        var supervisor = new ServerSupervisor(definition, processes, this, clock, supervisorOptions,
            loggerFactory.CreateLogger<ServerSupervisor>(), files, rcon, firewall, console);
        _servers[serverId] = new Server(spec.ShortId, Parse(spec), spec, rconPassword, supervisor);
        return supervisor;
    }

    private string ConsoleLogDirectory(string shortId)
    {
        ServerShortId.Validate(shortId);
        return Path.Combine(configs.Paths.ConsoleLogsDirectory, shortId);
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
        var plan = LaunchPlanRenderer.Render(Parse(spec), Platform, Binding(spec, rconPassword));
        return new ServerDefinition(serverId, spec.ShortId, provisioned.Directory, provisioned.Identity, plan, spec.AutoStart, spec.RestartOnCrash);
    }

    private static ServerBinding Binding(ServerRuntimeSpec spec, string? rconPassword) => new(spec.ShortId, spec.Variables,
        spec.Ports?.ToDictionary(p => p.Key, p => new PortAssignment(p.Value.IpAddress, p.Value.Port)), spec.ExtraArguments, rconPassword);

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

    private void UpdateRecord(Guid serverId, Func<ServerRecord, ServerRecord> change)
    {
        lock (_recordLock)
        {
            if (store.Load(serverId) is { } record)
            {
                store.Save(change(record));
            }
        }
    }

    private static CommandAck Rejected(CommandEnvelope envelope, string code, string message) =>
        new(envelope.CommandId, AckStatus.Rejected, code, message);

    private sealed record RunningOperation(Guid Id, bool Exclusive);

    private sealed record Server(string ShortId, GameManifest Manifest, ServerRuntimeSpec Spec, string? RconPassword, ServerSupervisor Supervisor)
    {
        // Holds secrets: never print them.
        public override string ToString() => $"Server {{ ShortId = {ShortId} }}";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Servers loaded: {Count} (servers root {ServersRoot})")]
    private partial void LogLoaded(int count, string serversRoot);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server {ServerId} could not be loaded from its stored spec")]
    private partial void LogServerNotLoaded(Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId} provisioned ({ShortId})")]
    private partial void LogProvisioned(Guid serverId, string shortId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId} installed")]
    private partial void LogInstalled(Guid serverId);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Operation {OperationId} on server {ServerId} cancelled")]
    private partial void LogOperationCancelled(Guid operationId, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} on server {ServerId} failed")]
    private partial void LogOperationFailed(Exception exception, Guid operationId, Guid serverId);
}
