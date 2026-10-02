using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
}

/// <summary>
/// The servers of this node: their stored specs, their supervisors, and the commands and operations the panel sends
/// for them (docs/architecture/05). Operations (provision, install, delete) run in the background, one at a time per
/// server, and their results are kept until the panel has received them. Started before the panel connection, so the
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
    InstallOptions installOptions,
    PanelChannel panel,
    SupervisorOptions supervisorOptions,
    ServerHostOptions options,
    TimeProvider clock,
    ILoggerFactory loggerFactory) : IHostedService, IServerEventSink, IDisposable
{
    private const string Platform = ManifestValidator.WindowsX64;

    private readonly ILogger _logger = loggerFactory.CreateLogger<ServerHost>();
    private readonly ConcurrentDictionary<Guid, Server> _servers = new();
    private readonly ConcurrentDictionary<Guid, Guid> _operations = new(); // server → operation in flight
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new(); // operation → its cancellation
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _recordLock = new();
    private AgentConfig? _config;

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

    public void Dispose() => _stopping.Dispose();

    public IReadOnlyList<ServerSnapshot> Snapshots() => [.. _servers.Values.Select(s => s.Supervisor.Snapshot)];

    /// <summary>
    /// Operations still running, and finished ones whose result waits in the outbox: the panel must not mark those as
    /// interrupted, since the result follows right after the Hello.
    /// </summary>
    public IReadOnlyList<Guid> OperationsInFlight() => [.. _operations.Values, .. store.PendingResults().Select(r => r.OperationId)];

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
        _loaded.TrySetResult();
        foreach (var supervisor in resume)
        {
            UpdateRecord(supervisor.ServerId, r => r with { ResumeOnStart = false });
            await supervisor.StartAsync(StateChangeReason.AgentStarted);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
            StartServerCommand start => await Control(envelope, start.ServerId, s => s.StartAsync(), allowedDuringOperation: false),
            StopServerCommand stop => await Control(envelope, stop.ServerId, s => s.StopAsync(), allowedDuringOperation: true),
            RestartServerCommand restart => await Control(envelope, restart.ServerId, s => s.RestartAsync(), allowedDuringOperation: false),
            KillServerCommand kill => await Control(envelope, kill.ServerId, s => s.KillAsync(), allowedDuringOperation: true),
            _ => new CommandAck(envelope.CommandId, AckStatus.UnsupportedCommand, "command.unsupported", null),
        };
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
            var root = _config!.ServersRootOrDefault(configs.Paths);
            // Before the account goes: a process of a deleted account would keep running unnoticed.
            TerminateStrays(command.ServerId, provisioner.Describe(root, server.ShortId).Identity);
            firewall.Remove(server.ShortId, [.. server.Manifest.PortList.Select(p => p.Name)]);
            await Task.Run(() => provisioner.Deprovision(root, server.ShortId), CancellationToken.None); // never left half done
            lock (_recordLock)
            {
                store.Delete(command.ServerId);
            }

            LogDeleted(command.ServerId, server.ShortId);
        });
    }

    private async Task<CommandAck> Control(CommandEnvelope envelope, Guid serverId, Func<ServerSupervisor, Task<AckStatus>> action,
        bool allowedDuringOperation)
    {
        if (!_servers.TryGetValue(serverId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        if (!allowedDuringOperation && _operations.ContainsKey(serverId))
        {
            return new CommandAck(envelope.CommandId, AckStatus.Busy, "server.busy", "An operation is running on this server.");
        }

        var status = await action(server.Supervisor);
        return new CommandAck(envelope.CommandId, status, null, null);
    }

    private CommandAck RunOperation(CommandEnvelope envelope, Guid serverId, Guid operationId, Func<CancellationToken, Task> work)
    {
        if (!_operations.TryAdd(serverId, operationId))
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
                await work(cancellation.Token);
                result = new OperationCompleted(operationId, serverId, OperationOutcome.Succeeded, null, null);
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
                var code = ex is GameProcessException process ? process.Code : "operation.failed";
                result = new OperationCompleted(operationId, serverId, OperationOutcome.Failed, code, ex.Message);
            }
            finally
            {
                _cancellations.TryRemove(operationId, out _);
                cancellation.Dispose();
                _operations.TryRemove(new KeyValuePair<Guid, Guid>(serverId, operationId));
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
        var supervisor = new ServerSupervisor(Definition(serverId, spec, rconPassword), processes, this, clock, supervisorOptions,
            loggerFactory.CreateLogger<ServerSupervisor>(), files, rcon, firewall);
        _servers[serverId] = new Server(spec.ShortId, Parse(spec), spec, rconPassword, supervisor);
        return supervisor;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Operation {OperationId} on server {ServerId} cancelled")]
    private partial void LogOperationCancelled(Guid operationId, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} on server {ServerId} failed")]
    private partial void LogOperationFailed(Exception exception, Guid operationId, Guid serverId);
}
