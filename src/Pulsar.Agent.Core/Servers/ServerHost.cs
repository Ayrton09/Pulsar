using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Processes;
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
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _recordLock = new();
    private readonly ConcurrentDictionary<Task, bool> _sending = new();
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
            if (installOptions.TestGameAvailable)
            {
                yield return AgentCapabilities.TestGame;
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
                var supervisor = Add(record.ServerId, record.Spec);
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

        // The panel should see the servers stop: let the last state changes go out before the connection closes.
        await Task.WhenAll(_sending.Keys).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
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
            StartServerCommand start => await Control(envelope, start.ServerId, s => s.StartAsync(), allowedDuringOperation: false),
            StopServerCommand stop => await Control(envelope, stop.ServerId, s => s.StopAsync(), allowedDuringOperation: true),
            RestartServerCommand restart => await Control(envelope, restart.ServerId, s => s.RestartAsync(), allowedDuringOperation: false),
            KillServerCommand kill => await Control(envelope, kill.ServerId, s => s.KillAsync(), allowedDuringOperation: true),
            _ => new CommandAck(envelope.CommandId, AckStatus.UnsupportedCommand, "command.unsupported", null),
        };
    }

    void IServerEventSink.StateChanged(ServerStateChanged change)
    {
        var sending = panel.SendAsync(nameof(IAgentHub.ServerStateChanged), change, _stopping.Token);
        _sending.TryAdd(sending, true);
        _ = sending.ContinueWith(t => _sending.TryRemove(t, out _), TaskScheduler.Default);
    }

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

        try
        {
            var manifest = Parse(spec);
            _ = Render(manifest, spec);
            if (manifest.Install?.Steps.OfType<TestGameServerStep>().Any() == true && !installOptions.TestGameAvailable)
            {
                return Rejected(envelope, "install.test_game_unavailable", "This agent was not started with the test game server.");
            }
        }
        catch (LaunchPlanException ex)
        {
            return Rejected(envelope, "server.invalid_spec", ex.Message);
        }

        return RunOperation(envelope, command.ServerId, operationId, async () =>
        {
            var root = _config!.ServersRootOrDefault(configs.Paths);
            await Task.Run(() => provisioner.Provision(root, spec.ShortId));
            var desired = false;
            lock (_recordLock)
            {
                desired = store.Load(command.ServerId)?.DesiredRunning ?? false;
                store.Save(new ServerRecord(command.ServerId, spec, desired, ResumeOnStart: false));
            }

            if (_servers.TryGetValue(command.ServerId, out var server))
            {
                await server.Supervisor.UpdateAsync(Definition(command.ServerId, spec));
                _servers[command.ServerId] = server with { Manifest = Parse(spec) };
            }
            else
            {
                Add(command.ServerId, spec);
            }

            LogProvisioned(command.ServerId, spec.ShortId);
        });
    }

    private CommandAck Install(CommandEnvelope envelope, InstallServerCommand command)
    {
        if (envelope.OperationId is not { } operationId)
        {
            return Rejected(envelope, "command.operation_required", "Installing is an operation.");
        }

        if (!_servers.TryGetValue(command.ServerId, out var server))
        {
            return Rejected(envelope, "server.unknown", "This node does not have that server.");
        }

        if (server.Supervisor.Snapshot.State is not (RuntimeState.Offline or RuntimeState.Crashed))
        {
            return Rejected(envelope, "server.running", "Stop the server before installing it.");
        }

        return RunOperation(envelope, command.ServerId, operationId, async () =>
        {
            var directory = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), server.ShortId).Directory;
            await SendProgressAsync(operationId, "installing", null);
            await Task.Run(() => installSteps.Run(server.Manifest, directory));
            LogInstalled(command.ServerId);
        });
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

        return RunOperation(envelope, command.ServerId, operationId, async () =>
        {
            await server.Supervisor.KillAsync();
            await server.Supervisor.WaitUntilSettledAsync();
            await server.Supervisor.DisposeAsync();
            _servers.TryRemove(command.ServerId, out _);
            await Task.Run(() => provisioner.Deprovision(_config!.ServersRootOrDefault(configs.Paths), server.ShortId));
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

    private CommandAck RunOperation(CommandEnvelope envelope, Guid serverId, Guid operationId, Func<Task> work)
    {
        if (!_operations.TryAdd(serverId, operationId))
        {
            return new CommandAck(envelope.CommandId, AckStatus.Busy, "server.busy", "Another operation is running on this server.");
        }

        _ = Task.Run(async () =>
        {
            OperationCompleted result;
            try
            {
                await work();
                result = new OperationCompleted(operationId, serverId, OperationOutcome.Succeeded, null, null);
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

    private Task<bool> SendProgressAsync(Guid operationId, string stage, double? percent) =>
        panel.SendAsync(nameof(IAgentHub.OperationProgress), new OperationProgress(operationId, stage, percent, null), _stopping.Token);

    private ServerSupervisor Add(Guid serverId, ServerRuntimeSpec spec)
    {
        var supervisor = new ServerSupervisor(Definition(serverId, spec), processes, this, clock, supervisorOptions,
            loggerFactory.CreateLogger<ServerSupervisor>());
        _servers[serverId] = new Server(spec.ShortId, Parse(spec), supervisor);
        return supervisor;
    }

    private ServerDefinition Definition(Guid serverId, ServerRuntimeSpec spec)
    {
        var provisioned = provisioner.Describe(_config!.ServersRootOrDefault(configs.Paths), spec.ShortId);
        return new ServerDefinition(serverId, spec.ShortId, provisioned.Directory, provisioned.Identity, Render(Parse(spec), spec),
            spec.AutoStart, spec.RestartOnCrash);
    }

    private static GameManifest Parse(ServerRuntimeSpec spec)
    {
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(spec.Manifest));
        return result.IsValid
            ? result.Manifest!
            : throw new LaunchPlanException("Invalid manifest: " + string.Join("; ", result.Errors));
    }

    private static LaunchPlan Render(GameManifest manifest, ServerRuntimeSpec spec) =>
        LaunchPlanRenderer.Render(manifest, Platform, spec.Variables, spec.ShortId, spec.ExtraArguments);

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

    private sealed record Server(string ShortId, GameManifest Manifest, ServerSupervisor Supervisor);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} on server {ServerId} failed")]
    private partial void LogOperationFailed(Exception exception, Guid operationId, Guid serverId);
}
