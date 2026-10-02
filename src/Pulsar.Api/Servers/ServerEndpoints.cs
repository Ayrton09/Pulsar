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

public sealed record CreateServerRequest(
    Guid NodeId,
    Guid TemplateId,
    [property: Required, StringLength(Server.MaxNameLength)] string Name,
    [property: StringLength(Server.MaxDescriptionLength)] string? Description);

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

        api.MapGet("/templates", async (TemplateCatalog catalog, CancellationToken ct) => TypedResults.Ok(await catalog.ListAsync(ct)))
            .WithTags("Templates")
            .RequirePermission(Permissions.Template.View, Permissions.Server.Create);
        return api;
    }

    private static async Task<Ok<IReadOnlyList<ServerSummary>>> ListAsync(Guid? nodeId, ServerManagement servers, CancellationToken ct) =>
        TypedResults.Ok(await servers.ListAsync(nodeId, ct));

    private static async Task<Results<Ok<ServerDetails>, ProblemHttpResult>> GetAsync(Guid id, ServerManagement servers, CancellationToken ct) =>
        (await servers.GetAsync(new ServerId(id), ct)).ToHttp();

    private static async Task<Results<Created<ServerDetails>, ProblemHttpResult>> CreateAsync(
        CreateServerRequest request, ServerManagement servers, CancellationToken ct)
    {
        var result = await servers.CreateAsync(new NodeId(request.NodeId), request.TemplateId, request.Name, request.Description, ct);
        if (!result.IsSuccess)
        {
            return ApiProblems.FromDomainError(result.Error!);
        }

        return TypedResults.Created($"/api/v1/servers/{result.Value.Server.Id}", result.Value);
    }

    private static async Task<Results<Ok<AgentCommandResult>, ProblemHttpResult>> CommandAsync(Task<Result<AgentCommandResult>> command) =>
        (await command).ToHttp();

    private static async Task<Results<Ok<ServerDetails>, ProblemHttpResult>> DetailsAsync(Task<Result<ServerDetails>> change) =>
        (await change).ToHttp();
}
