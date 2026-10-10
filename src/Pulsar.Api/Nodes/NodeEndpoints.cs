using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Metrics;
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

/// <param name="Ports">"27015", "27015-27030" or "27015,27020".</param>
public sealed record CreateAllocationsRequest(
    [property: Required, StringLength(15)] string IpAddress,
    [property: Required, StringLength(256)] string Ports,
    [property: StringLength(NodeAllocation.MaxNotesLength)] string? Notes);

public sealed record RevokeCredentialRequest([property: StringLength(NodeCredential.MaxRevokeReasonLength)] string? Reason);

/// <param name="LifetimeHours">1 hour to 90 days.</param>
public sealed record CreateEnrollmentTokenRequest(
    [property: Required, StringLength(ReusableEnrollmentToken.MaxNameLength)] string Name,
    [property: Range(1, ReusableEnrollmentToken.MaxUsesLimit)] int MaxUses,
    [property: Range(1, ReusableEnrollmentToken.MaxLifetimeDays * 24)] int LifetimeHours,
    bool RequireApproval);

public sealed record ApproveNodesRequest([property: Required, MinLength(1), MaxLength(NodeManagement.MaxApprovals)] IReadOnlyList<Guid> NodeIds);

public static class NodeEndpoints
{
    public static RouteGroupBuilder MapNodeEndpoints(this RouteGroupBuilder api)
    {
        var nodes = api.MapGroup("/nodes").WithTags("Nodes");
        nodes.MapGet("/", ListAsync).RequirePermissionAnywhere(Permissions.Node.View, Permissions.Node.Manage, Permissions.Server.Create);
        nodes.MapGet("/{id:guid}", GetAsync).RequirePermission(Permissions.Node.View, Permissions.Node.Manage);
        nodes.MapGet("/{id:guid}/metrics", async Task<Results<Ok<NodeMetricHistory>, ProblemHttpResult>> (Guid id, IPulsarDbContext db,
                MetricHistory history, CancellationToken ct, string range = "hour") =>
                MetricHistory.ParseRange(range) is not { } span ? ApiProblems.Create(StatusCodes.Status400BadRequest, "metrics.invalid_range", "The range must be hour, day, week or month.")
                : await db.Nodes.AnyAsync(n => n.Id == new NodeId(id), ct) ? TypedResults.Ok(await history.ReadNodeAsync(new NodeId(id), span, ct))
                : ApiProblems.FromDomainError(NodeErrors.NotFound))
            .RequirePermission(Permissions.Node.View, Permissions.Node.Manage);
        nodes.MapPost("/", CreateAsync).RequirePermission(Permissions.Node.Manage);
        nodes.MapPut("/{id:guid}", UpdateAsync).RequirePermission(Permissions.Node.Manage);
        nodes.MapPost("/{id:guid}/disable", (Guid id, NodeManagement m, CancellationToken ct) => StatusAsync(m.DisableAsync(new NodeId(id), ct)))
            .RequirePermission(Permissions.Node.Manage);
        nodes.MapPost("/{id:guid}/enable", (Guid id, NodeManagement m, CancellationToken ct) => StatusAsync(m.EnableAsync(new NodeId(id), ct)))
            .RequirePermission(Permissions.Node.Manage);
        nodes.MapPost("/{id:guid}/ping", PingAsync).RequirePermission(Permissions.Node.Manage);
        nodes.MapPost("/{id:guid}/approve", async Task<Results<Ok<NodeSummary>, ProblemHttpResult>> (Guid id, NodeManagement m, CancellationToken ct) =>
                await m.ApproveAsync([new NodeId(id)], ct) is { IsSuccess: true } approved
                    ? TypedResults.Ok(approved.Value[0])
                    : ApiProblems.FromDomainError(NodeErrors.NotFound))
            .RequirePermission(Permissions.Node.Manage);
        // Many at once (machines enrolled together): node.manage held globally.
        nodes.MapPost("/approve", async (ApproveNodesRequest request, NodeManagement m, CancellationToken ct) =>
                (await m.ApproveAsync([.. request.NodeIds.Select(i => new NodeId(i))], ct)).ToHttp())
            .RequirePermission(Permissions.Node.Manage);
        nodes.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(Permissions.Node.Manage);
        nodes.MapPost("/{id:guid}/enrollment-tokens", CreateEnrollmentTokenAsync).RequirePermission(Permissions.Node.CredentialsManage);
        nodes.MapPost("/{id:guid}/credentials/{credentialId:guid}/revoke", RevokeCredentialAsync).RequirePermission(Permissions.Node.CredentialsManage);
        nodes.MapPost("/{id:guid}/credentials/rotate", RotateCredentialAsync).RequirePermission(Permissions.Node.CredentialsManage);
        nodes.MapGet("/{id:guid}/allocations", async (Guid id, NodeAllocations allocations, CancellationToken ct) =>
                (await allocations.ListAsync(new NodeId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Node.View, Permissions.Node.Manage, Permissions.Server.Create);
        nodes.MapPost("/{id:guid}/allocations", async (Guid id, CreateAllocationsRequest request, NodeAllocations allocations, CancellationToken ct) =>
                (await allocations.CreateAsync(new NodeId(id), request.IpAddress, request.Ports, request.Notes, ct)).ToHttp())
            .RequirePermission(Permissions.Node.Manage);
        nodes.MapDelete("/{id:guid}/allocations/{allocationId:guid}", async (Guid id, Guid allocationId, NodeAllocations allocations,
                CancellationToken ct) => (await allocations.DeleteAsync(new NodeId(id), new AllocationId(allocationId), ct)).ToHttp())
            .RequirePermission(Permissions.Node.Manage);

        // Reusable enrollment tokens (Phase 14): they create nodes and their credentials, so they take
        // node.credentials.manage held globally.
        var tokens = api.MapGroup("/enrollment-tokens").WithTags("Nodes").RequirePermission(Permissions.Node.CredentialsManage);
        tokens.MapGet("/", async (EnrollmentTokens t, CancellationToken ct) => TypedResults.Ok(await t.ListAsync(ct)));
        tokens.MapPost("/", async Task<Results<Created<CreatedEnrollmentToken>, ProblemHttpResult>> (CreateEnrollmentTokenRequest request,
            EnrollmentTokens t, CancellationToken ct) =>
        {
            var result = await t.CreateAsync(request.Name, request.MaxUses, TimeSpan.FromHours(request.LifetimeHours), request.RequireApproval, ct);
            return result.IsSuccess
                ? TypedResults.Created($"/api/v1/enrollment-tokens/{result.Value.Token.Id}", result.Value)
                : ApiProblems.FromDomainError(result.Error!);
        });
        tokens.MapPost("/{id:guid}/revoke", async (Guid id, EnrollmentTokens t, CancellationToken ct) => (await t.RevokeAsync(id, ct)).ToHttp());

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
