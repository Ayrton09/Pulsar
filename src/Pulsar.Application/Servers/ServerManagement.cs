using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
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
    DateTimeOffset? CompletedAt);

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

public sealed record ServerDetails(ServerSummary Server, string? Description, bool AutoStart, bool RestartOnCrash, IReadOnlyList<OperationView> RecentOperations);

/// <summary>Servers for the REST API (docs/architecture/05): queries, lifecycle commands and their operations.</summary>
public sealed partial class ServerManagement(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    TemplateCatalog templates,
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
    public async Task<Result<ServerDetails>> CreateAsync(NodeId nodeId, Guid templateId, string name, string? description, CancellationToken cancellationToken)
    {
        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
        {
            return NodeErrors.NotFound;
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
        db.Servers.Add(server);
        audit.Record(AuditActions.Server.Created, metadata: new { server.Name, template = $"{template.Key}@{template.Version}" })
            .WithServer(server.Id, nodeId);
        await db.SaveChangesAsync(cancellationToken);

        await RunOperationAsync(server, OperationType.ServerProvision, new ProvisionServerCommand(server.Id.Value, Spec(server, template)),
            request.UserId, OperationRequester.User, cancellationToken);
        return await DetailsAsync(server, cancellationToken);
    }

    /// <summary>Provisions and installs again (repairs a failed install; Phase 4 adds game updates).</summary>
    public async Task<Result<ServerDetails>> ReinstallAsync(ServerId id, CancellationToken cancellationToken)
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

        if (server.RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var result = await RunOperationAsync(server, OperationType.ServerProvision, new ProvisionServerCommand(server.Id.Value, Spec(server, template)),
            request.UserId, OperationRequester.User, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        audit.Record(AuditActions.Server.InstallRequested).WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailsAsync(server, cancellationToken);
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

    public Task<Result<AgentCommandResult>> StartAsync(ServerId id, CancellationToken cancellationToken) =>
        ControlAsync(id, AuditActions.Server.Started, s => s.RequestStart(clock.GetUtcNow()), new StartServerCommand(id.Value), cancellationToken);

    public Task<Result<AgentCommandResult>> RestartAsync(ServerId id, CancellationToken cancellationToken) =>
        ControlAsync(id, AuditActions.Server.Restarted, s => s.RequestRestart(clock.GetUtcNow()), new RestartServerCommand(id.Value), cancellationToken);

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

    /// <summary>
    /// Creates the operation, takes the server for it and sends its command. A refused or unanswered command fails the
    /// operation at once (and releases the server), so nothing waits for a result that will never come.
    /// </summary>
    private async Task<Result> RunOperationAsync(Server server, OperationType type, AgentCommand command, UserId? requestedBy,
        OperationRequester requestedVia, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var operation = Operation.Create(type, server.NodeId, server.Id, requestedBy, requestedVia, now);
        var taken = server.BeginOperation(operation, now);
        if (!taken.IsSuccess)
        {
            return taken;
        }

        db.Operations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);

        var ack = await connections.SendAsync(server.NodeId, command, requestedBy, cancellationToken, operation.Id.Value);
        now = clock.GetUtcNow();
        if (ack?.Status is AckStatus.Accepted or AckStatus.AlreadyInState)
        {
            operation.Started(now);
        }
        else
        {
            var code = ack?.ErrorCode ?? (ack is null ? NodeErrors.NotConnected.Code : ServerErrors.CommandRejected.Code);
            operation.Complete(OperationStatus.Failed, code, ack?.Message, now);
            server.OperationCompleted(operation, now);
            LogOperationRefused(operation.Id, type, server.Id, code);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The agent already reported the result (a quick operation): it wins over "started". Both rows are read
            // again (a deleted server becomes detached).
            LogOperationAlreadyReported(operation.Id);
            await db.Entry(operation).ReloadAsync(cancellationToken);
            await db.Entry(server).ReloadAsync(cancellationToken);
        }

        return Result.Success();
    }

    private static ServerRuntimeSpec Spec(Server server, GameTemplate template) =>
        // Phase 3: every variable keeps its manifest default (per-server values arrive with the startup settings).
        new(server.ShortId, template.Content, new Dictionary<string, string?>(), [], server.AutoStart, server.RestartOnCrash);

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
        return new ServerDetails(summary, server.Description, server.AutoStart, server.RestartOnCrash, [.. operations.Select(View)]);
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

    internal static OperationView View(Operation o) =>
        new(o.Id.Value, o.Type, o.Status, o.ProgressStage, o.ProgressPercent, o.ErrorCode, o.ErrorMessage, o.CreatedAt, o.CompletedAt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Operation {OperationId} finished before its start was saved")]
    private partial void LogOperationAlreadyReported(OperationId operationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} ({Type}) on server {ServerId} not accepted by the node: {Code}")]
    private partial void LogOperationRefused(OperationId operationId, OperationType type, ServerId serverId, string code);
}
