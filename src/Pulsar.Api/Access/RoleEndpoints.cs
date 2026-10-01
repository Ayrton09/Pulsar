using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Access;

public sealed record PermissionResponse(string Key, IReadOnlyList<PermissionScope> GrantableScopes);

public sealed record CreateRoleRequest(
    [property: Required, StringLength(Role.MaxNameLength)] string Name,
    [property: StringLength(Role.MaxDescriptionLength)] string? Description,
    [property: Required] PermissionScope Scope,
    [property: Required] IReadOnlyList<string> Permissions);

public sealed record UpdateRoleRequest(
    [property: Required, StringLength(Role.MaxNameLength)] string Name,
    [property: StringLength(Role.MaxDescriptionLength)] string? Description,
    [property: Required] IReadOnlyList<string> Permissions);

public static class RoleEndpoints
{
    public static RouteGroupBuilder MapRoleEndpoints(this RouteGroupBuilder api)
    {
        // The catalog is not secret: the UI uses it to label permissions everywhere.
        api.MapGet("/permissions", GetPermissions).WithTags("Roles").RequireSignedInUser();

        var roles = api.MapGroup("/roles").WithTags("Roles");
        roles.MapGet("/", ListAsync)
            .RequirePermission(Permissions.Role.Manage, Permissions.User.Manage, Permissions.User.View);
        roles.MapGet("/{id:guid}", GetAsync)
            .RequirePermission(Permissions.Role.Manage, Permissions.User.Manage, Permissions.User.View);
        roles.MapPost("/", CreateAsync).RequirePermission(Permissions.Role.Manage);
        roles.MapPut("/{id:guid}", UpdateAsync).RequirePermission(Permissions.Role.Manage);
        roles.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(Permissions.Role.Manage);
        return api;
    }

    private static Ok<PermissionResponse[]> GetPermissions() => TypedResults.Ok(PermissionCatalog.All
        .OrderBy(d => d.Key, StringComparer.Ordinal)
        .Select(d => new PermissionResponse(d.Key, [.. d.GrantableScopes.Order()]))
        .ToArray());

    private static async Task<Ok<IReadOnlyList<RoleSummary>>> ListAsync(RoleManagement roles, CancellationToken ct) =>
        TypedResults.Ok(await roles.ListAsync(ct));

    private static async Task<Results<Ok<RoleSummary>, ProblemHttpResult>> GetAsync(Guid id, RoleManagement roles, CancellationToken ct) =>
        (await roles.GetAsync(new RoleId(id), ct)).ToHttp();

    private static async Task<Results<Created<RoleSummary>, ProblemHttpResult>> CreateAsync(
        CreateRoleRequest request, RoleManagement roles, CancellationToken ct)
    {
        var result = await roles.CreateAsync(new RoleInput(request.Name, request.Description, request.Scope, request.Permissions), ct);
        if (!result.IsSuccess)
        {
            return ApiProblems.FromDomainError(result.Error!);
        }

        return TypedResults.Created($"/api/v1/roles/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<Ok<RoleSummary>, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateRoleRequest request, RoleManagement roles, CancellationToken ct) =>
        (await roles.UpdateAsync(new RoleId(id), request.Name, request.Description, request.Permissions, ct)).ToHttp();

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, RoleManagement roles, CancellationToken ct) =>
        (await roles.DeleteAsync(new RoleId(id), ct)).ToHttp();
}
