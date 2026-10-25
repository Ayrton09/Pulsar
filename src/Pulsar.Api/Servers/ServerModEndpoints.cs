using System.ComponentModel.DataAnnotations;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Servers;

/// <param name="ProjectId">The catalog's project (id or slug).</param>
/// <param name="VersionId">A version of it; null for its newest one for the server.</param>
public sealed record InstallModRequest(
    [property: Required, StringLength(64)] string ProjectId,
    [property: StringLength(64)] string? VersionId);

/// <param name="ModIds">The mods to update; null for every one with a newer version.</param>
public sealed record UpdateModsRequest([property: MaxLength(300)] IReadOnlyList<Guid>? ModIds);

/// <summary>
/// A server's mods and plugins from a catalog (Pulsar 1.0.9; docs/architecture/07, "Mods"): what it has, searching the
/// catalog for what it can take, installing (with what a project requires), updating and removing. Changing them is an
/// operation on the stopped server, and needs the right to install it, as addons do. The routes that ask the catalog
/// share each user's budget of lookups with the Workshop's: Modrinth answers a panel's address 300 times a minute.
/// </summary>
public static class ServerModEndpoints
{
    public static RouteGroupBuilder MapServerModEndpoints(this RouteGroupBuilder api)
    {
        var mods = api.MapGroup("/servers/{id:guid}/mods").WithTags("Mods");
        mods.MapGet("/", async (Guid id, ServerMods m, CancellationToken ct) => (await m.GetAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.View);
        mods.MapGet("/search", async (Guid id, string? query, int? offset, ServerMods m, CancellationToken ct) =>
                (await m.SearchAsync(new ServerId(id), query, offset ?? 0, ct)).ToHttp())
            .RequirePermission(Permissions.Server.View).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapGet("/projects/{projectId}/versions", async (Guid id, string projectId, ServerMods m, CancellationToken ct) =>
                (await m.VersionsAsync(new ServerId(id), projectId, ct)).ToHttp())
            .RequirePermission(Permissions.Server.View).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapGet("/updates", async (Guid id, ServerMods m, CancellationToken ct) => (await m.UpdatesAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.View).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapPost("/", async (Guid id, InstallModRequest request, ServerMods m, CancellationToken ct) =>
                (await m.InstallAsync(new ServerId(id), request.ProjectId, request.VersionId, ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapPost("/update", async (Guid id, UpdateModsRequest request, ServerMods m, CancellationToken ct) =>
                (await m.UpdateAsync(new ServerId(id), request.ModIds?.Select(i => new ServerModId(i)).ToList(), ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapDelete("/{modId:guid}", async (Guid id, Guid modId, ServerMods m, CancellationToken ct) =>
                (await m.RemoveAsync(new ServerId(id), new ServerModId(modId), ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install);
        return api;
    }
}
