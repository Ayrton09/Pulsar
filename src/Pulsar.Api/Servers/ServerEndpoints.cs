using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Servers;

/// <param name="Variables">Values of the template's variables (missing ones take their default).</param>
/// <param name="Ports">Allocation per template port (missing required ports get a free one of the node).</param>
public sealed record CreateServerRequest(
    Guid NodeId,
    Guid TemplateId,
    [property: Required, StringLength(Server.MaxNameLength)] string Name,
    [property: StringLength(Server.MaxDescriptionLength)] string? Description,
    Dictionary<string, string?>? Variables = null,
    Dictionary<string, Guid?>? Ports = null);

/// <param name="Variables">Only the variables to change; null or empty puts the default back (or clears a secret).</param>
public sealed record UpdateServerSettingsRequest(
    Dictionary<string, string?> Variables,
    [property: Required] List<string> ExtraArguments,
    bool AutoStart,
    bool RestartOnCrash);

/// <param name="Ports">Allocation per template port; null leaves an optional port without one.</param>
public sealed record UpdateServerPortsRequest([property: Required] Dictionary<string, Guid?> Ports);

public sealed record UpdateGameRequest(bool Validate);

public sealed record ConsoleCommandRequest([property: Required, StringLength(Pulsar.Contracts.ConsoleInput.MaxLength, MinimumLength = 1)] string Command);

public sealed record TemplateContentRequest([property: Required, StringLength(Pulsar.Manifests.ManifestParser.MaximumSize)] string Content);

public static class ServerEndpoints
{
    public static RouteGroupBuilder MapServerEndpoints(this RouteGroupBuilder api)
    {
        var servers = api.MapGroup("/servers").WithTags("Servers");
        servers.MapGet("/", ListAsync).RequirePermission(Permissions.Server.View);
        servers.MapGet("/{id:guid}", GetAsync).RequirePermission(Permissions.Server.View);
        servers.MapPost("/", CreateAsync).RequirePermission(Permissions.Server.Create);
        servers.MapPost("/{id:guid}/start", (Guid id, ServerManagement m, CancellationToken ct) => CommandAsync(m.StartAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Start);
        servers.MapPost("/{id:guid}/stop", (Guid id, ServerManagement m, CancellationToken ct) => CommandAsync(m.StopAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Stop);
        servers.MapPost("/{id:guid}/restart", (Guid id, ServerManagement m, CancellationToken ct) => CommandAsync(m.RestartAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Restart);
        servers.MapPost("/{id:guid}/kill", (Guid id, ServerManagement m, CancellationToken ct) => CommandAsync(m.KillAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Kill);
        servers.MapPost("/{id:guid}/install", (Guid id, ServerManagement m, CancellationToken ct) => DetailsAsync(m.ReinstallAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Install);
        servers.MapDelete("/{id:guid}", (Guid id, ServerManagement m, CancellationToken ct) => DetailsAsync(m.DeleteAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Delete);
        servers.MapGet("/{id:guid}/settings", async (Guid id, ServerManagement m, CancellationToken ct) => (await m.GetSettingsAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.View);
        servers.MapPut("/{id:guid}/settings", async (Guid id, UpdateServerSettingsRequest request, ServerManagement m, CancellationToken ct) =>
                (await m.UpdateSettingsAsync(new ServerId(id), request.Variables, request.ExtraArguments, request.AutoStart, request.RestartOnCrash, ct)).ToHttp())
            .RequirePermission(Permissions.Server.StartupEdit);
        servers.MapPut("/{id:guid}/ports", (Guid id, UpdateServerPortsRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.UpdatePortsAsync(new ServerId(id), request.Ports, ct)))
            .RequirePermission(Permissions.Server.SettingsEdit);
        servers.MapPost("/{id:guid}/update", (Guid id, UpdateGameRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.UpdateGameAsync(new ServerId(id), request.Validate, ct)))
            .RequirePermission(Permissions.Server.Install);
        servers.MapPost("/{id:guid}/console", (Guid id, ConsoleCommandRequest request, ConsoleCommands console, CancellationToken ct) =>
                CommandAsync(console.SendAsync(new ServerId(id), request.Command, ct)))
            .RequirePermission(Permissions.Server.ConsoleWrite);
        servers.MapPost("/{id:guid}/operations/{operationId:guid}/cancel", (Guid id, Guid operationId, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.CancelOperationAsync(new ServerId(id), operationId, ct)))
            .RequirePermission(Permissions.Server.Install);

        var templates = api.MapGroup("/templates").WithTags("Templates");
        templates.MapGet("/", async (TemplateCatalog catalog, CancellationToken ct) => TypedResults.Ok(await catalog.ListAsync(ct)))
            .RequirePermission(Permissions.Template.View, Permissions.Server.Create);
        templates.MapGet("/{id:guid}", async (Guid id, TemplateCatalog catalog, CancellationToken ct) => (await catalog.GetAsync(id, ct)).ToHttp())
            .RequirePermission(Permissions.Template.View, Permissions.Server.Create);
        templates.MapPost("/preview", async (TemplateContentRequest request, TemplateCatalog catalog, CancellationToken ct) =>
                TypedResults.Ok(await catalog.PreviewAsync(request.Content, ct)))
            .RequirePermission(Permissions.Template.Manage);
        templates.MapPost("/", ImportTemplateAsync).RequirePermission(Permissions.Template.Manage);
        return api;
    }

    private static async Task<Ok<IReadOnlyList<ServerSummary>>> ListAsync(Guid? nodeId, ServerManagement servers, CancellationToken ct) =>
        TypedResults.Ok(await servers.ListAsync(nodeId, ct));

    private static async Task<Results<Ok<ServerDetails>, ProblemHttpResult>> GetAsync(Guid id, ServerManagement servers, CancellationToken ct) =>
        (await servers.GetAsync(new ServerId(id), ct)).ToHttp();

    private static async Task<Results<Created<ServerDetails>, ProblemHttpResult>> CreateAsync(
        CreateServerRequest request, ServerManagement servers, CancellationToken ct)
    {
        var result = await servers.CreateAsync(new NodeId(request.NodeId), request.TemplateId, request.Name, request.Description, request.Variables,
            request.Ports, ct);
        if (!result.IsSuccess)
        {
            return ApiProblems.FromDomainError(result.Error!);
        }

        return TypedResults.Created($"/api/v1/servers/{result.Value.Server.Id}", result.Value);
    }

    private static async Task<Results<Created<TemplateDetails>, ProblemHttpResult>> ImportTemplateAsync(
        TemplateContentRequest request, TemplateCatalog catalog, CancellationToken ct)
    {
        var result = await catalog.ImportAsync(request.Content, ct);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/templates/{result.Value.Template.Id}", result.Value)
            : ApiProblems.FromDomainError(result.Error!);
    }

    private static async Task<Results<Ok<AgentCommandResult>, ProblemHttpResult>> CommandAsync(Task<Result<AgentCommandResult>> command) =>
        (await command).ToHttp();

    private static async Task<Results<Ok<ServerDetails>, ProblemHttpResult>> DetailsAsync(Task<Result<ServerDetails>> change) =>
        (await change).ToHttp();
}
