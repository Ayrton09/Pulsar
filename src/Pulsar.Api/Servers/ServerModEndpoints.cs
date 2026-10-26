using System.ComponentModel.DataAnnotations;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Manifests;

namespace Pulsar.Api.Servers;

/// <param name="ProjectId">The catalog's project (an id; a slug for Modrinth).</param>
/// <param name="VersionId">A version of it; null for its newest one for the server.</param>
/// <param name="Source">The catalog: <c>modrinth</c> (the default) or <c>curseforge</c>.</param>
public sealed record InstallModRequest(
    [property: Required, StringLength(64)] string ProjectId,
    [property: StringLength(64)] string? VersionId,
    [property: StringLength(16)] string? Source = null);

/// <param name="ModIds">The mods to update; null for every one with a newer version.</param>
public sealed record UpdateModsRequest([property: MaxLength(300)] IReadOnlyList<Guid>? ModIds);

/// <param name="ApiKey">The administrator's CurseForge API key: checked with CurseForge, kept encrypted, never shown again.</param>
public sealed record CurseForgeKeyRequest([property: Required, StringLength(CatalogSettings.MaxKeyLength)] string ApiKey);

/// <summary>
/// A server's mods and plugins from a catalog (Pulsar 1.0.9; docs/architecture/07, "Mods"): what it has, searching a
/// catalog for what it can take, installing (with what a project requires), updating and removing. Changing them is an
/// operation on the stopped server, and needs the right to install it, as addons do; so does installing a modpack. The
/// routes that ask a catalog share each user's budget of lookups with the Workshop's: Modrinth answers a panel's address
/// 300 times a minute. CurseForge needs the administrator's own key (<c>settings.manage</c> sets it).
/// </summary>
public static class ServerModEndpoints
{
    public static RouteGroupBuilder MapServerModEndpoints(this RouteGroupBuilder api)
    {
        var mods = api.MapGroup("/servers/{id:guid}/mods").WithTags("Mods");
        mods.MapGet("/", async (Guid id, ServerMods m, CancellationToken ct) => (await m.GetAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.View);
        mods.MapGet("/search", async (Guid id, string? source, string? query, int? offset, ServerMods m, CancellationToken ct) =>
                (await m.SearchAsync(new ServerId(id), Catalog(source), query, offset ?? 0, ct)).ToHttp())
            .RequirePermission(Permissions.Server.View).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapGet("/projects/{projectId}/versions", async (Guid id, string projectId, string? source, ServerMods m, CancellationToken ct) =>
                (await m.VersionsAsync(new ServerId(id), Catalog(source), projectId, ct)).ToHttp())
            .RequirePermission(Permissions.Server.View).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapGet("/updates", async (Guid id, ServerMods m, CancellationToken ct) => (await m.UpdatesAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.View).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapPost("/", async (Guid id, InstallModRequest request, ServerMods m, CancellationToken ct) =>
                (await m.InstallAsync(new ServerId(id), Catalog(request.Source), request.ProjectId, request.VersionId, ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapPost("/update", async (Guid id, UpdateModsRequest request, ServerMods m, CancellationToken ct) =>
                (await m.UpdateAsync(new ServerId(id), request.ModIds?.Select(i => new ServerModId(i)).ToList(), ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        mods.MapDelete("/{modId:guid}", async (Guid id, Guid modId, ServerMods m, CancellationToken ct) =>
                (await m.RemoveAsync(new ServerId(id), new ServerModId(modId), ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install);

        // Modpacks: installing one installs the server again, as its type and game version (the right to install it).
        var modpacks = api.MapGroup("/servers/{id:guid}/modpacks").WithTags("Mods");
        modpacks.MapGet("/search", async (Guid id, string? source, string? query, int? offset, ServerMods m, CancellationToken ct) =>
                (await m.SearchModpacksAsync(new ServerId(id), Catalog(source), query, offset ?? 0, ct)).ToHttp())
            .RequirePermission(Permissions.Server.View).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        modpacks.MapGet("/projects/{projectId}/versions", async (Guid id, string projectId, string? source, ServerMods m, CancellationToken ct) =>
                (await m.ModpackVersionsAsync(new ServerId(id), Catalog(source), projectId, ct)).ToHttp())
            .RequirePermission(Permissions.Server.View).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        modpacks.MapPost("/", async (Guid id, InstallModRequest request, ServerMods m, CancellationToken ct) =>
                (await m.InstallModpackAsync(new ServerId(id), Catalog(request.Source), request.ProjectId, request.VersionId, ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);

        // The administrator's CurseForge key: write-only; checking it asks CurseForge, so it shares the lookups' budget.
        var settings = api.MapGroup("/mod-catalogs").WithTags("Mods");
        settings.MapGet("/", async (CatalogSettings s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(ct)))
            .RequirePermission(Permissions.Settings.Manage);
        settings.MapPut("/curseforge-key", async (CurseForgeKeyRequest request, CatalogSettings s, CancellationToken ct) =>
                (await s.SetCurseForgeKeyAsync(request.ApiKey, ct)).ToHttp())
            .RequirePermission(Permissions.Settings.Manage).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        settings.MapDelete("/curseforge-key", async (CatalogSettings s, CancellationToken ct) => TypedResults.Ok(await s.RemoveCurseForgeKeyAsync(ct)))
            .RequirePermission(Permissions.Settings.Manage);
        return api;
    }

    /// <summary>The catalog asked for; Modrinth when none is named (what the API offered first).</summary>
    private static string Catalog(string? source) => string.IsNullOrEmpty(source) ? ModSourceDefinition.Modrinth : source;
}
