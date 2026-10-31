using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Servers;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Servers;

/// <summary>A server the signed-in user creates within their quota (1.0.24): its caps are required, its ports chosen for it.</summary>
public sealed record CreateOwnedServerRequest(
    Guid NodeId,
    Guid TemplateId,
    [property: Required, StringLength(Server.MaxNameLength)] string Name,
    [property: Range(Server.MinMemoryLimitMb, Server.MaxMemoryLimitMb)] int MemoryMb,
    [property: Range(1, 100)] int CpuPercent,
    Dictionary<string, string?>? Variables = null);

/// <summary>
/// The signed-in user's quota (1.0.24, docs/architecture/02 "Quotas"): what it allows and uses, and creating a server
/// within it. Their servers are then reached as any other, under <c>/servers/{id}</c>.
/// </summary>
public static class QuotaEndpoints
{
    public static RouteGroupBuilder MapQuotaEndpoints(this RouteGroupBuilder api)
    {
        var account = api.MapGroup("/account").WithTags("Account");
        account.MapGet("/quota", async (HttpContext http, ServerQuotas quotas, CancellationToken ct) =>
                (await quotas.GetAsync(http.User.GetRequiredUserId(), ct)).ToHttp())
            .RequireSignedInUser();
        account.MapPost("/servers", CreateAsync).RequireSignedInUser();
        return api;
    }

    private static async Task<Results<Created<ServerDetails>, ProblemHttpResult>> CreateAsync(CreateOwnedServerRequest request, HttpContext http,
        ServerQuotas quotas, CancellationToken ct)
    {
        var created = await quotas.CreateAsync(http.User.GetRequiredUserId(),
            new OwnedServerRequest(request.NodeId, request.TemplateId, request.Name, request.MemoryMb, request.CpuPercent, request.Variables), ct);
        return created.IsSuccess
            ? TypedResults.Created($"/api/v1/servers/{created.Value.Server.Id}", created.Value)
            : ApiProblems.FromDomainError(created.Error!);
    }
}
