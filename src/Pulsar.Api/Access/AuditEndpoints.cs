using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
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

    private static async Task<Results<Ok<AuditPage>, ProblemHttpResult>> ListAsync(
        AuditQueries audit, Guid? before, string? action, Guid? actorUserId, DateTimeOffset? from, DateTimeOffset? until,
        string? targetType, string? targetId, CancellationToken ct, int limit = 50)
    {
        if (action?.Length > 64 || targetType?.Length > 32 || targetId?.Length > 512 || (from is not null && until <= from))
        {
            return ApiProblems.Create(StatusCodes.Status400BadRequest, "validation.failed", "The audit filters are not valid.");
        }

        return TypedResults.Ok(await audit.ListAsync(before, limit, action, actorUserId, ct, from, until, targetType, targetId));
    }
}
