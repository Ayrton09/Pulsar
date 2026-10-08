using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

public sealed record OperationView(
    Guid Id,
    OperationType Type,
    OperationStatus Status,
    string? Stage,
    decimal? Percent,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    bool Cancellable,
    bool CancelRequested);

public sealed record ServerSummary(
    Guid Id,
    string ShortId,
    string Name,
    Guid NodeId,
    string NodeName,
    bool NodeConnected,
    Guid TemplateId,
    string TemplateName,
    InstallState InstallState,
    ServerRuntimeState RuntimeState,
    DesiredState DesiredState,
    ServerStateReason? LastStateReason,
    int? LastExitCode,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastStoppedAt,
    OperationView? ActiveOperation,
    DateTimeOffset CreatedAt);

/// <summary>A manifest port of the server and the allocation it has (none for an unassigned optional port).</summary>
/// <param name="Follows">The port this one comes with: it is not chosen on its own.</param>
public sealed record ServerPortView(string Name, string Label, bool Required, Guid? AllocationId, string? IpAddress, int? Port, string? Follows = null);

public sealed record ServerDetails(
    ServerSummary Server,
    string? Description,
    bool AutoStart,
    bool RestartOnCrash,
    string TemplateKey,
    string TemplateVersion,
    bool CanUpdate,
    IReadOnlyList<ServerPortView> Ports,
    IReadOnlyList<OperationView> RecentOperations,
    ServerLimitsView Limits,
    TemplateUpgradeView? TemplateUpgrade = null,
    bool HasAddons = false);

/// <summary>The newest version of the server's game template, when it is newer than the one the server runs.</summary>
public sealed record TemplateUpgradeView(Guid TemplateId, string Version);

/// <summary>Caps on the server's CPU (share of its node, %) and memory (MiB); null fields are not capped.</summary>
public sealed record ServerLimitsView(int? CpuPercent, int? MemoryMb);

/// <summary>A variable of the server: its definition and value. Secret values are never returned, only whether one is set.</summary>
public sealed record ServerVariableView(TemplateVariable Definition, string? Value, bool HasValue);

/// <summary>The server's startup settings (docs/architecture/05): variables, extra arguments and restart policy.</summary>
public sealed record ServerSettings(
    IReadOnlyList<ServerVariableView> Variables,
    IReadOnlyList<string> ExtraArguments,
    bool AllowExtraArguments,
    bool AutoStart,
    bool RestartOnCrash);

/// <summary>Servers for the REST API (docs/architecture/05): queries, lifecycle commands and their operations.</summary>
public sealed partial class ServerManagement(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    TemplateCatalog templates,
    ServerSpecs specs,
    ServerOperations operations,
    OperationProgressCache progress,
    IPermissionEvaluator permissions,
    VisibleResources visible,
    IAuditLog audit,
    IRequestContext request,
    TimeProvider clock,
    ILogger<ServerManagement> logger)
{
    private const int RecentOperations = 10;

    public async Task<IReadOnlyList<ServerSummary>> ListAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var query = db.Servers.AsNoTracking();
        if (nodeId is { } node)
        {
            query = query.Where(s => s.NodeId == new NodeId(node));
        }

        var servers = await query.OrderBy(s => s.Name).ToListAsync(cancellationToken);
        if (await visible.ServersAsync(cancellationToken) is { } sees)
        {
            servers = [.. servers.Where(s => sees(s.Id, s.NodeId))];
        }

        return await SummariesAsync(servers, cancellationToken);
    }

    public async Task<Result<ServerDetails>> GetAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return server is null ? ServerErrors.NotFound : await DetailsAsync(server, cancellationToken);
    }

    /// <summary>
    /// Creates the server and starts provisioning it on its node (directory, Windows account), followed by its install.
    /// The node must be connected: there is nothing useful to create on a node that cannot hear about it.
    /// </summary>
    public async Task<Result<ServerDetails>> CreateAsync(NodeId nodeId, Guid templateId, string name, string? description,
        IReadOnlyDictionary<string, string?>? variables, IReadOnlyDictionary<string, Guid?>? ports, CancellationToken cancellationToken)
    {
        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        if (request.UserId is { } user)
        {
            var granted = await permissions.GetAsync(user, cancellationToken);
            if (!granted.Has(Permissions.Server.Create, AccessTarget.ForNode(nodeId)))
            {
                // A node the user cannot see is one that does not exist, for them.
                return ResourceVisibility.Node.Any(p => granted.Has(p, AccessTarget.ForNode(nodeId))) ? ServerErrors.CreateForbidden : NodeErrors.NotFound;
            }
        }

        var template = await templates.FindOfferedAsync(templateId, cancellationToken);
        if (template is null)
        {
            return ServerErrors.TemplateNotFound;
        }

        if (Connection(nodeId) is not { } connection)
        {
            return NodeErrors.NotConnected;
        }

        if (template.Key == ManifestValidator.TestGameKey && !connection.Capabilities.Contains(AgentCapabilities.TestGame, StringComparer.Ordinal))
        {
            return ServerErrors.TemplateUnsupported;
        }

        string shortId;
        do
        {
            shortId = Server.NewShortId();
        }
        while (await db.Servers.AnyAsync(s => s.ShortId == shortId, cancellationToken));

        var now = clock.GetUtcNow();
        var created = Server.Create(nodeId, template.Id, name, description, shortId, now);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var server = created.Value;
        var manifest = ServerSpecs.Parse(template);
        // Whoever can create a server sets its first values, protected variables included.
        var configured = specs.SetVariables(server, manifest, variables ?? new Dictionary<string, string?>(), canEditProtected: true, request.UserId, now);
        if (!configured.IsSuccess)
        {
            return configured.Error!;
        }

        db.Servers.Add(server);
        var assigned = await specs.AssignPortsAsync(server, manifest, ports, cancellationToken);
        if (!assigned.IsSuccess)
        {
            return assigned.Error!;
        }

        var valid = specs.Check(server, manifest, [.. db.NodeAllocations.Local.Where(a => a.ServerId == server.Id)]);
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        audit.Record(AuditActions.Server.Created, metadata: new { server.Name, template = $"{template.Key}@{template.Version}" })
            .WithServer(server.Id, nodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // An allocation was taken by another server meanwhile (version check or unique index).
            return ServerErrors.AllocationUnavailable;
        }

        await RunOperationAsync(server, OperationType.ServerProvision,
            new ProvisionServerCommand(server.Id.Value, await specs.BuildAsync(server, template, cancellationToken)), request.UserId, OperationRequester.User,
            cancellationToken);
        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>Provisions and installs again (repairs a failed install; Phase 4 adds game updates).</summary>
    public async Task<Result<ServerDetails>> ReinstallAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (Connection(server.NodeId) is null)
        {
            return NodeErrors.NotConnected;
        }

        if (server.RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var result = await RunOperationAsync(server, OperationType.ServerProvision,
            new ProvisionServerCommand(server.Id.Value, await specs.BuildAsync(server, template, cancellationToken)), request.UserId,
            OperationRequester.User, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        audit.Record(AuditActions.Server.InstallRequested).WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>
    /// Moves a stopped server to a newer version of its game's template (docs/architecture/02, "Template upgrades"):
    /// its values are checked against the new version (values of removed variables are dropped, new required ports get
    /// an allocation), then the server is provisioned and installed again with it, its files kept.
    /// </summary>
    public Task<Result<ServerDetails>> UpgradeTemplateAsync(ServerId id, Guid templateId, CancellationToken cancellationToken) =>
        UpgradeTemplateAsync(id, templateId, attempt: 0, cancellationToken);

    private async Task<Result<ServerDetails>> UpgradeTemplateAsync(ServerId id, Guid templateId, int attempt, CancellationToken cancellationToken)
    {
        var server = await db.Servers.Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (Connection(server.NodeId) is null)
        {
            return NodeErrors.NotConnected;
        }

        var current = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        if (await templates.FindOfferedAsync(templateId, cancellationToken) is not { } next)
        {
            return ServerErrors.TemplateNotFound;
        }

        var manifest = ServerSpecs.Parse(next);
        var now = clock.GetUtcNow();
        var changed = server.ChangeTemplate(current, next, [.. manifest.VariableList.Select(v => v.Key)], now);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        // No new values: the stored ones (and the new version's defaults) must make a valid whole.
        var values = specs.SetVariables(server, manifest, new Dictionary<string, string?>(), canEditProtected: true, request.UserId, now);
        if (!values.IsSuccess)
        {
            return values.Error!;
        }

        var ports = await specs.AssignPortsAsync(server, manifest, requested: null, cancellationToken);
        if (!ports.IsSuccess)
        {
            return ports.Error!;
        }

        var valid = specs.Check(server, manifest, [.. db.NodeAllocations.Local.Where(a => a.ServerId == server.Id)]);
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        audit.Record(AuditActions.Server.TemplateChanged, metadata: new { key = next.Key, from = current.Version, to = next.Version })
            .WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex) when (attempt < 2 && ex.Entries.Any(e => e.Entity is Server))
        {
            // The agent reported a state change in between: upgrade the fresh row (its checks run again).
            db.ChangeTracker.Clear();
            return await UpgradeTemplateAsync(id, templateId, attempt + 1, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // An allocation was taken by another server meanwhile (version check or unique index).
            return ServerErrors.AllocationUnavailable;
        }

        var result = await RunOperationAsync(server, OperationType.ServerProvision,
            new ProvisionServerCommand(server.Id.Value, await specs.BuildAsync(server, next, cancellationToken)), request.UserId,
            OperationRequester.User, cancellationToken);
        return result.IsSuccess ? await DetailsAsync(server, cancellationToken) : result.Error!;
    }

    /// <summary>The install that follows a successful provisioning (started by the panel itself).</summary>
    public async Task ContinueInstallAsync(ServerId id, UserId? requestedBy, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is not null)
        {
            await RunOperationAsync(server, OperationType.ServerInstall, new InstallServerCommand(server.Id.Value), requestedBy,
                OperationRequester.System, cancellationToken);
        }
    }

    public async Task<Result<AgentCommandResult>> StartAsync(ServerId id, CancellationToken cancellationToken) =>
        await SyncSpecAsync(id, cancellationToken) is { IsSuccess: false } sync
            ? sync.Error!
            : await ControlAsync(id, AuditActions.Server.Started, s => s.RequestStart(clock.GetUtcNow()), new StartServerCommand(id.Value), cancellationToken);

    public async Task<Result<AgentCommandResult>> RestartAsync(ServerId id, CancellationToken cancellationToken) =>
        await SyncSpecAsync(id, cancellationToken) is { IsSuccess: false } sync
            ? sync.Error!
            : await ControlAsync(id, AuditActions.Server.Restarted, s => s.RequestRestart(clock.GetUtcNow()), new RestartServerCommand(id.Value),
                cancellationToken);

    /// <summary>The server's startup settings, with the manifest's definitions (secret values withheld).</summary>
    public async Task<Result<ServerSettings>> GetSettingsAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var manifest = ServerSpecs.Parse(await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken));
        return Settings(server, manifest);
    }

    /// <summary>
    /// Changes variables (only the given keys; null or empty goes back to the default), extra arguments and the restart
    /// policy. They apply at the next start: the panel sends the spec to the agent before every start.
    /// </summary>
    public async Task<Result<ServerSettings>> UpdateSettingsAsync(ServerId id, IReadOnlyDictionary<string, string?> variables,
        IReadOnlyList<string> extraArguments, bool autoStart, bool restartOnCrash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var server = await db.Servers.Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var manifest = ServerSpecs.Parse(await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken));
        var now = clock.GetUtcNow();
        var canEditProtected = request.UserId is { } user
            && await permissions.HasAsync(user, Permissions.Server.SettingsEdit, AccessTarget.ForServer(server.Id, server.NodeId), cancellationToken);
        var changed = specs.SetVariables(server, manifest, variables, canEditProtected, request.UserId, now);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        var startup = server.SetStartup(extraArguments, autoStart, restartOnCrash, now);
        if (!startup.IsSuccess)
        {
            return startup.Error!;
        }

        var valid = specs.Check(server, manifest, await specs.AllocationsOf(server.Id, cancellationToken));
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        // Keys only: values may be secrets.
        audit.Record(AuditActions.Server.SettingsChanged, metadata: new { variables = variables.Keys.Order(StringComparer.Ordinal).ToList(), autoStart, restartOnCrash })
            .WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(cancellationToken);
        await SyncSpecAsync(id, cancellationToken); // best effort now; it is sent again before the next start anyway
        return Settings(server, manifest);
    }

    /// <summary>
    /// Sets the caps on the server's CPU and memory (null removes one). They reach a running game at once through its
    /// agent, and every later start; an agent that cannot enforce them refuses nothing silently: the change is refused.
    /// </summary>
    public async Task<Result<ServerDetails>> SetLimitsAsync(ServerId id, int? cpuPercent, int? memoryMb, CancellationToken cancellationToken)
    {
        var server = await db.Servers.Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if ((cpuPercent is not null || memoryMb is not null)
            && connections.Find(server.NodeId) is { } agent && !agent.Capabilities.Contains(AgentCapabilities.Limits))
        {
            return ServerErrors.LimitsUnsupported;
        }

        var changed = server.SetLimits(cpuPercent, memoryMb, clock.GetUtcNow());
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        audit.Record(AuditActions.Server.LimitsChanged, metadata: new { cpuPercent, memoryMb }).WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The agent reported a state change in between: set the limits again on the fresh row.
            await db.Entry(server).ReloadAsync(cancellationToken);
            server.SetLimits(cpuPercent, memoryMb, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        await SyncSpecAsync(id, cancellationToken); // a running game gets them now; the next start gets them anyway
        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>Gives the server other allocations of its node (stopped servers only: the game listens on them).</summary>
    public async Task<Result<ServerDetails>> UpdatePortsAsync(ServerId id, IReadOnlyDictionary<string, Guid?> ports, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var server = await db.Servers.Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (server.RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        var manifest = ServerSpecs.Parse(await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken));
        var assigned = await specs.AssignPortsAsync(server, manifest, ports, cancellationToken);
        if (!assigned.IsSuccess)
        {
            return assigned.Error!;
        }

        var valid = specs.Check(server, manifest, [.. db.NodeAllocations.Local.Where(a => a.ServerId == server.Id)]);
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        audit.Record(AuditActions.Server.PortsChanged, metadata: new
        {
            ports = db.NodeAllocations.Local.Where(a => a.ServerId == server.Id).ToDictionary(a => a.PortName!, a => $"{a.IpAddress}:{a.Port}"),
        }).WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ServerErrors.AllocationUnavailable;
        }

        await SyncSpecAsync(id, cancellationToken);
        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>Updates the game (the manifest's update steps: SteamCMD), optionally verifying every file.</summary>
    public async Task<Result<ServerDetails>> UpdateGameAsync(ServerId id, bool validate, CancellationToken cancellationToken)
    {
        var requested = await RequestUpdateAsync(id, validate, OperationRequester.User, cancellationToken);
        return requested.IsSuccess ? await DetailsAsync(requested.Value.Server, cancellationToken) : requested.Error!;
    }

    /// <summary>Starts the update operation; the scheduler follows the operation it returns (failed already when the node refused it).</summary>
    public async Task<Result<(Server Server, Operation Operation)>> RequestUpdateAsync(ServerId id, bool validate, OperationRequester via,
        CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (ServerSpecs.Parse(await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken)).Update is null)
        {
            return ServerErrors.UpdateUnsupported;
        }

        if (Connection(server.NodeId) is null)
        {
            return NodeErrors.NotConnected;
        }

        var operation = Operation.Create(OperationType.ServerUpdate, server.NodeId, server.Id, via == OperationRequester.User ? request.UserId : null,
            via, clock.GetUtcNow());
        var run = await operations.RunAsync(server, operation, new UpdateServerCommand(server.Id.Value, validate), beforeSave: null, cancellationToken);
        if (!run.IsSuccess)
        {
            return run.Error!;
        }

        audit.Record(AuditActions.Server.UpdateRequested, metadata: new { validate }).WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(cancellationToken);
        return (server, run.Value);
    }

    /// <summary>
    /// Asks the agent to cancel a running install, update or backup; it completes as cancelled. Installs and updates
    /// need <c>server.install</c>, backups <c>server.backups.create</c>.
    /// </summary>
    public async Task<Result<ServerDetails>> CancelOperationAsync(ServerId id, Guid operationId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var operation = await db.Operations.SingleOrDefaultAsync(o => o.Id == new OperationId(operationId) && o.ServerId == id, cancellationToken);
        if (server is null || operation is null)
        {
            return server is null ? ServerErrors.NotFound : ServerErrors.OperationNotFound;
        }

        if (!operation.IsCancellable)
        {
            return ServerErrors.OperationNotCancellable;
        }

        var needed = operation.Type == OperationType.BackupCreate ? Permissions.Server.BackupsCreate : Permissions.Server.Install;
        if (request.UserId is not { } user
            || !await permissions.HasAsync(user, needed, AccessTarget.ForServer(server.Id, server.NodeId), cancellationToken))
        {
            return ServerErrors.OperationForbidden;
        }

        if (Connection(server.NodeId) is null)
        {
            return NodeErrors.NotConnected;
        }

        operation.RequestCancel(clock.GetUtcNow());
        audit.Record(AuditActions.Server.OperationCancelRequested, metadata: new { operation = operationId, type = operation.Type.ToString() })
            .WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // It finished meanwhile.
            return ServerErrors.OperationNotCancellable;
        }

        var ack = await connections.SendAsync(server.NodeId, new CancelOperationCommand(operationId), request.UserId, cancellationToken);
        if (ack is null)
        {
            return NodeErrors.NotConnected;
        }

        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>
    /// Sends the server's current spec to its agent (values, ports, arguments): changes made in the panel apply at the
    /// next start, even when they were made while the node was disconnected.
    /// </summary>
    private async Task<Result> SyncSpecAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null || server.InstallState == InstallState.NotInstalled || Connection(server.NodeId) is null)
        {
            return Result.Success(); // the command that follows reports what is wrong
        }

        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var ack = await connections.SendAsync(server.NodeId,
            new ApplyServerSpecCommand(server.Id.Value, await specs.BuildAsync(server, template, cancellationToken)), request.UserId, cancellationToken);
        if (ack is null)
        {
            return NodeErrors.NotConnected;
        }

        if (ack.Status is AckStatus.Accepted or AckStatus.AlreadyInState)
        {
            return Result.Success();
        }

        LogSpecRefused(server.Id, ack.ErrorCode, ack.Message);
        return ServerErrors.CommandRejected;
    }

    private static ServerSettings Settings(Server server, GameManifest manifest)
    {
        var values = server.Variables.ToDictionary(v => v.Key);
        return new ServerSettings(
            [
                .. manifest.VariableList.Select(definition =>
                {
                    var stored = values.GetValueOrDefault(definition.Key);
                    return new ServerVariableView(TemplateCatalog.Variable(definition), definition.Secret ? null : stored?.Value, stored is not null);
                }),
            ],
            server.ExtraArguments, manifest.Runtime.GetValueOrDefault(ManifestValidator.WindowsX64)?.AllowExtraArguments == true, server.AutoStart,
            server.RestartOnCrash);
    }

    public Task<Result<AgentCommandResult>> StopAsync(ServerId id, CancellationToken cancellationToken) =>
        ControlAsync(id, AuditActions.Server.Stopped, s =>
        {
            s.RequestStop(clock.GetUtcNow());
            return Result.Success();
        }, new StopServerCommand(id.Value), cancellationToken);

    public Task<Result<AgentCommandResult>> KillAsync(ServerId id, CancellationToken cancellationToken) =>
        ControlAsync(id, AuditActions.Server.Killed, s =>
        {
            s.RequestStop(clock.GetUtcNow());
            return Result.Success();
        }, new KillServerCommand(id.Value), cancellationToken);

    /// <summary>Starts deleting the server on its node; the row goes once the agent reports it is gone.</summary>
    public async Task<Result<ServerDetails>> DeleteAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (Connection(server.NodeId) is null)
        {
            return NodeErrors.NotConnected;
        }

        var result = await RunOperationAsync(server, OperationType.ServerDelete, new DeleteServerCommand(server.Id.Value), request.UserId,
            OperationRequester.User, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        audit.Record(AuditActions.Server.DeleteRequested, metadata: new { server.Name }).WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailsAsync(server, cancellationToken);
    }

    private async Task<Result<AgentCommandResult>> ControlAsync(ServerId id, string action, Func<Server, Result> change, AgentCommand command,
        CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (Connection(server.NodeId) is null)
        {
            return NodeErrors.NotConnected;
        }

        var changed = change(server);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        audit.Record(action).WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The agent reported a state change in between: apply the request again on the fresh row.
            await db.Entry(server).ReloadAsync(cancellationToken);
            changed = change(server);
            if (!changed.IsSuccess)
            {
                return changed.Error!;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        var ack = await connections.SendAsync(server.NodeId, command, request.UserId, cancellationToken);
        if (ack is null)
        {
            return NodeErrors.NotConnected;
        }

        return new AgentCommandResult(ack.Status, ack.ErrorCode);
    }

    /// <summary>Creates the operation and runs it (<see cref="ServerOperations"/>).</summary>
    private async Task<Result> RunOperationAsync(Server server, OperationType type, AgentCommand command, UserId? requestedBy,
        OperationRequester requestedVia, CancellationToken cancellationToken)
    {
        var operation = Operation.Create(type, server.NodeId, server.Id, requestedBy, requestedVia, clock.GetUtcNow());
        var run = await operations.RunAsync(server, operation, command, beforeSave: null, cancellationToken);
        return run.IsSuccess ? Result.Success() : run.Error!;
    }

    private AgentConnectionInfo? Connection(NodeId node) =>
        connections.Find(node) is { } live && sessions.IsActive(live.SessionId) ? live : null;

    private async Task<ServerDetails> DetailsAsync(Server server, CancellationToken cancellationToken)
    {
        var summary = (await SummariesAsync([server], cancellationToken))[0];
        var operations = await db.Operations.AsNoTracking()
            .Where(o => o.ServerId == server.Id)
            .OrderByDescending(o => o.CreatedAt)
            .Take(RecentOperations)
            .ToListAsync(cancellationToken);
        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var manifest = ServerSpecs.Parse(template);
        var allocations = (await db.NodeAllocations.AsNoTracking().Where(a => a.ServerId == server.Id).ToListAsync(cancellationToken))
            .ToDictionary(a => a.PortName!);
        var ports = manifest.PortList.Select(p => allocations.GetValueOrDefault(p.Name) is { } a
            ? new ServerPortView(p.Name, p.Label, p.Required, a.Id.Value, a.IpAddress, a.Port, p.Follows?.Port)
            : new ServerPortView(p.Name, p.Label, p.Required, null, null, null, p.Follows?.Port)).ToList();
        var newer = (await db.GameTemplates.AsNoTracking().Where(t => t.Key == template.Key).Select(t => new { t.Id, t.Version }).ToListAsync(cancellationToken))
            .Where(t => GameTemplate.CompareVersions(t.Version, template.Version) > 0)
            .OrderByDescending(t => t.Version, Comparer<string>.Create(GameTemplate.CompareVersions))
            .FirstOrDefault();
        return new ServerDetails(summary, server.Description, server.AutoStart, server.RestartOnCrash, template.Key, template.Version,
            manifest.Update is not null, ports, [.. operations.Select(View)], new ServerLimitsView(server.CpuLimitPercent, server.MemoryLimitMb),
            newer is null ? null : new TemplateUpgradeView(newer.Id.Value, newer.Version), manifest.AddonList.Count > 0);
    }

    private async Task<IReadOnlyList<ServerSummary>> SummariesAsync(IReadOnlyList<Server> servers, CancellationToken cancellationToken)
    {
        var nodeIds = servers.Select(s => s.NodeId).Distinct().ToList();
        var templateIds = servers.Select(s => s.TemplateId).Distinct().ToList();
        var operationIds = servers.Where(s => s.ActiveOperationId is not null).Select(s => s.ActiveOperationId!.Value).ToList();
        var nodes = await db.Nodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);
        var templateNames = await db.GameTemplates.AsNoTracking().Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var operations = await db.Operations.AsNoTracking().Where(o => operationIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        return [.. servers.Select(s => new ServerSummary(s.Id.Value, s.ShortId, s.Name, s.NodeId.Value, nodes.GetValueOrDefault(s.NodeId) ?? "",
            Connection(s.NodeId) is not null, s.TemplateId.Value, templateNames.GetValueOrDefault(s.TemplateId) ?? "", s.InstallState, s.RuntimeState,
            s.DesiredState, s.LastStateReason, s.LastExitCode, s.LastStartedAt, s.LastStoppedAt,
            s.ActiveOperationId is { } op && operations.TryGetValue(op, out var operation) ? View(operation) : null, s.CreatedAt))];
    }

    /// <summary>The operation with its live progress (the database only gets it now and then).</summary>
    internal OperationView View(Operation o)
    {
        var live = o.IsActive ? progress.Get(o.Id.Value) : null;
        return new(o.Id.Value, o.Type, o.Status, live?.Stage ?? o.ProgressStage, live is { } l ? l.Percent : o.ProgressPercent, o.ErrorCode,
            o.ErrorMessage, o.CreatedAt, o.CompletedAt, o.IsCancellable, o.CancelRequestedAt is not null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: the agent refused its spec: {Code} ({Reason})")]
    private partial void LogSpecRefused(ServerId serverId, string? code, string? reason);
}
