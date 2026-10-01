using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Security;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;

namespace Pulsar.Api.Access;

public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/audit", ListAsync).WithTags("Audit").RequirePermission(Permissions.Audit.View);
        return api;
    }

    private static async Task<Ok<AuditPage>> ListAsync(
        AuditQueries audit, Guid? before, string? action, Guid? actorUserId, CancellationToken ct, int limit = 50) =>
        TypedResults.Ok(await audit.ListAsync(before, limit, action, actorUserId, ct));
}
