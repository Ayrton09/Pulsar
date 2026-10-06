using System.ComponentModel.DataAnnotations;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;

namespace Pulsar.Api.Servers;

/// <param name="AppId">The game's Steam app id, from the variable's <c>workshop</c> settings.</param>
/// <param name="Ids">1 to 100 Workshop ids.</param>
public sealed record WorkshopLookupRequest(long AppId, [property: Required, MaxLength(WorkshopLookup.MaxIds)] IReadOnlyList<string> Ids);

/// <summary>
/// Steam Workshop lookups for the editor of variables that list Workshop items (docs/architecture/07, "Workshop"): for
/// whoever sets such variables, when creating a server or changing its startup settings.
/// </summary>
public static class WorkshopEndpoints
{
    public static RouteGroupBuilder MapWorkshopEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/workshop/lookup", async (WorkshopLookupRequest request, WorkshopLookup lookup, CancellationToken ct) =>
                (await lookup.LookupAsync(request.AppId, request.Ids, ct)).ToHttp())
            .WithTags("Servers")
            .RequirePermissionAnywhere(Permissions.Server.Create, Permissions.Server.StartupEdit);
        return api;
    }
}
