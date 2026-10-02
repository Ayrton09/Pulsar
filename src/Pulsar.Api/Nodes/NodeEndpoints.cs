using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Api.Nodes;

public sealed record CreateNodeRequest(
    [property: Required, StringLength(Node.MaxNameLength)] string Name,
    [property: StringLength(Node.MaxDescriptionLength)] string? Description,
    [property: StringLength(Node.MaxPublicHostLength)] string? PublicHost);

public sealed record UpdateNodeRequest(
    [property: Required, StringLength(Node.MaxNameLength)] string Name,
    [property: StringLength(Node.MaxDescriptionLength)] string? Description,
    [property: StringLength(Node.MaxPublicHostLength)] string? PublicHost);

public sealed record RevokeCredentialRequest([property: StringLength(NodeCredential.MaxRevokeReasonLength)] string? Reason);

public static class NodeEndpoints
{
    public static RouteGroupBuilder MapNodeEndpoints(this RouteGroupBuilder api)
    {
        var nodes = api.MapGroup("/nodes").WithTags("Nodes");
        nodes.MapGet("/", ListAsync).RequirePermission(Permissions.Node.View, Permissions.Node.Manage);
        nodes.MapGet("/{id:guid}", GetAsync).RequirePermission(Permissions.Node.View, Permissions.Node.Manage);
        nodes.MapPost("/", CreateAsync).RequirePermission(Permissions.Node.Manage);
        nodes.MapPut("/{id:guid}", UpdateAsync).RequirePermission(Permissions.Node.Manage);
        nodes.MapPost("/{id:guid}/disable", (Guid id, NodeManagement m, CancellationToken ct) => StatusAsync(m.DisableAsync(new NodeId(id), ct)))
            .RequirePermission(Permissions.Node.Manage);
        nodes.MapPost("/{id:guid}/enable", (Guid id, NodeManagement m, CancellationToken ct) => StatusAsync(m.EnableAsync(new NodeId(id), ct)))
            .RequirePermission(Permissions.Node.Manage);
        nodes.MapPost("/{id:guid}/ping", PingAsync).RequirePermission(Permissions.Node.Manage);
        nodes.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(Permissions.Node.Manage);
        nodes.MapPost("/{id:guid}/enrollment-tokens", CreateEnrollmentTokenAsync).RequirePermission(Permissions.Node.CredentialsManage);
        nodes.MapPost("/{id:guid}/credentials/{credentialId:guid}/revoke", RevokeCredentialAsync).RequirePermission(Permissions.Node.CredentialsManage);
        nodes.MapPost("/{id:guid}/credentials/rotate", RotateCredentialAsync).RequirePermission(Permissions.Node.CredentialsManage);

        var panelKeys = api.MapGroup("/panel-keys").WithTags("Panel keys").RequirePermission(Permissions.Settings.Manage);
        panelKeys.MapGet("/", async (PanelKeyManagement keys, CancellationToken ct) => TypedResults.Ok(await keys.GetAsync(ct)));
        panelKeys.MapPost("/rotate", RotatePanelKeyAsync);
        panelKeys.MapPost("/{keyId}/retire", RetirePanelKeyAsync);
        return api;
    }

    private static async Task<Results<Ok<AgentCommandResult>, ProblemHttpResult>> RotateCredentialAsync(
        Guid id, NodeManagement nodes, CancellationToken ct) =>
        (await nodes.RotateCredentialAsync(new NodeId(id), ct)).ToHttp();

    private static async Task<Results<Ok<PanelKeysOverview>, ProblemHttpResult>> RotatePanelKeyAsync(PanelKeyManagement keys, CancellationToken ct) =>
        (await keys.RotateAsync(ct)).ToHttp();

    private static async Task<Results<Ok<PanelKeysOverview>, ProblemHttpResult>> RetirePanelKeyAsync(
        string keyId, PanelKeyManagement keys, CancellationToken ct) =>
        (await keys.RetireAsync(keyId, ct)).ToHttp();

    private static async Task<Ok<IReadOnlyList<NodeSummary>>> ListAsync(NodeManagement nodes, CancellationToken ct) =>
        TypedResults.Ok(await nodes.ListAsync(ct));

    private static async Task<Results<Ok<NodeDetails>, ProblemHttpResult>> GetAsync(Guid id, NodeManagement nodes, CancellationToken ct) =>
        (await nodes.GetAsync(new NodeId(id), ct)).ToHttp();

    private static async Task<Results<Created<CreatedNode>, ProblemHttpResult>> CreateAsync(
        CreateNodeRequest request, NodeManagement nodes, CancellationToken ct)
    {
        var result = await nodes.CreateAsync(request.Name, request.Description, request.PublicHost, ct);
        if (!result.IsSuccess)
        {
            return ApiProblems.FromDomainError(result.Error!);
        }

        return TypedResults.Created($"/api/v1/nodes/{result.Value.Node.Id}", result.Value);
    }

    private static async Task<Results<Ok<NodeSummary>, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateNodeRequest request, NodeManagement nodes, CancellationToken ct) =>
        (await nodes.UpdateAsync(new NodeId(id), request.Name, request.Description, request.PublicHost, ct)).ToHttp();

    private static async Task<Results<Ok<NodeSummary>, ProblemHttpResult>> StatusAsync(Task<Domain.Common.Result<NodeSummary>> change) =>
        (await change).ToHttp();

    private static async Task<Results<Ok<PingResult>, ProblemHttpResult>> PingAsync(Guid id, NodeManagement nodes, CancellationToken ct) =>
        (await nodes.PingAsync(new NodeId(id), ct)).ToHttp();

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, NodeManagement nodes, CancellationToken ct) =>
        (await nodes.DeleteAsync(new NodeId(id), ct)).ToHttp();

    private static async Task<Results<Ok<EnrollmentInstructions>, ProblemHttpResult>> CreateEnrollmentTokenAsync(
        Guid id, NodeManagement nodes, CancellationToken ct) =>
        (await nodes.CreateEnrollmentTokenAsync(new NodeId(id), ct)).ToHttp();

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeCredentialAsync(
        Guid id, Guid credentialId, RevokeCredentialRequest? request, NodeManagement nodes, CancellationToken ct) =>
        (await nodes.RevokeCredentialAsync(new NodeId(id), credentialId, request?.Reason, ct)).ToHttp();
}
