using System.ComponentModel.DataAnnotations;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;

namespace Pulsar.Api.Servers;

/// <param name="AppId">The game's Steam app id, from the variable's <c>workshop</c> settings.</param>
/// <param name="Ids">1 to 100 Workshop ids.</param>
public sealed record WorkshopLookupRequest(long AppId, [property: Required, MaxLength(WorkshopLookup.MaxIds)] IReadOnlyList<string> Ids);

/// <param name="GameId">The game on CurseForge, from the variable's <c>curseforge</c> settings.</param>
/// <param name="Ids">1 to 100 CurseForge project ids.</param>
public sealed record CurseForgeLookupRequest(int GameId, [property: Required, MaxLength(CurseForgeLists.MaxIds)] IReadOnlyList<string> Ids);

/// <summary>
/// Steam Workshop lookups for the editor of variables that list Workshop items (docs/architecture/07, "Workshop"): for
/// whoever sets such variables, when creating a server or changing its startup settings.
/// </summary>
public static class WorkshopEndpoints
{
    /// <summary>The per-user limit of the routes that cost more than a database read (<see cref="AuthRateLimitOptions.LookupPermitLimit"/>).</summary>
    public const string LookupRateLimitPolicy = "lookups";

    public static RouteGroupBuilder MapWorkshopEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/workshop/lookup", async (WorkshopLookupRequest request, WorkshopLookup lookup, CancellationToken ct) =>
                (await lookup.LookupAsync(request.AppId, request.Ids, ct)).ToHttp())
            .WithTags("Servers")
            .RequirePermissionAnywhere(Permissions.Server.Create, Permissions.Server.StartupEdit)
            .RequireRateLimiting(LookupRateLimitPolicy);

        // Pulsar 1.0.14: the editor of a variable that lists CurseForge projects (ARK: Survival Ascended's mods).
        api.MapPost("/curseforge/lookup", async (CurseForgeLookupRequest request, CurseForgeLists lists, CancellationToken ct) =>
                (await lists.LookupAsync(request.GameId, request.Ids, ct)).ToHttp())
            .WithTags("Servers")
            .RequirePermissionAnywhere(Permissions.Server.Create, Permissions.Server.StartupEdit)
            .RequireRateLimiting(LookupRateLimitPolicy);
        api.MapGet("/curseforge/search", async (int gameId, string? query, int? offset, CurseForgeLists lists, CancellationToken ct) =>
                (await lists.SearchAsync(gameId, query, offset ?? 0, ct)).ToHttp())
            .WithTags("Servers")
            .RequirePermissionAnywhere(Permissions.Server.Create, Permissions.Server.StartupEdit)
            .RequireRateLimiting(LookupRateLimitPolicy);
        return api;
    }
}
