using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Security;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Access;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Users");
        users.MapGet("/", ListAsync).RequirePermission(Permissions.User.View, Permissions.User.Manage);
        users.MapGet("/{id:guid}", (Guid id, UserManagement m, CancellationToken ct) => m.GetAsync(new UserId(id), ct))
            .RequirePermission(Permissions.User.View, Permissions.User.Manage);
        users.MapPost("/", (CreateUserRequest request, UserManagement m, CancellationToken ct) => m.CreateAsync(request, ct))
            .RequirePermission(Permissions.User.Manage);
        users.MapPut("/{id:guid}", (Guid id, UpdateUserRequest request, UserManagement m, CancellationToken ct) =>
            m.UpdateAsync(new UserId(id), request, ct)).RequirePermission(Permissions.User.Manage);
        users.MapPost("/{id:guid}/password", (Guid id, ResetPasswordRequest request, UserManagement m, CancellationToken ct) =>
            m.ResetPasswordAsync(new UserId(id), request, ct)).RequirePermission(Permissions.User.Manage);
        users.MapPost("/{id:guid}/two-factor/reset", (Guid id, UserManagement m, CancellationToken ct) =>
            m.ResetTwoFactorAsync(new UserId(id), ct)).RequirePermission(Permissions.User.Manage);
        users.MapDelete("/{id:guid}/api-keys", (Guid id, UserManagement m, CancellationToken ct) => m.RevokeApiKeysAsync(new UserId(id), ct))
            .RequirePermission(Permissions.User.Manage);
        users.MapPut("/{id:guid}/roles", (Guid id, SetUserRolesRequest request, UserManagement m, CancellationToken ct) =>
            m.SetRolesAsync(new UserId(id), request, ct)).RequirePermission(Permissions.User.Manage);
        users.MapDelete("/{id:guid}", (Guid id, UserManagement m, CancellationToken ct) => m.DeleteAsync(new UserId(id), ct))
            .RequirePermission(Permissions.User.Manage);
        users.MapPut("/{id:guid}/nodes/{nodeId:guid}/roles/{roleId:guid}", (Guid id, Guid nodeId, Guid roleId, UserManagement m, CancellationToken ct) =>
            m.SetAccessAsync(new UserId(id), PermissionScope.Node, nodeId, new RoleId(roleId), granted: true, ct)).RequirePermission(Permissions.User.Manage);
        users.MapDelete("/{id:guid}/nodes/{nodeId:guid}/roles/{roleId:guid}", (Guid id, Guid nodeId, Guid roleId, UserManagement m, CancellationToken ct) =>
            m.SetAccessAsync(new UserId(id), PermissionScope.Node, nodeId, new RoleId(roleId), granted: false, ct)).RequirePermission(Permissions.User.Manage);
        users.MapPut("/{id:guid}/servers/{serverId:guid}/roles/{roleId:guid}", (Guid id, Guid serverId, Guid roleId, UserManagement m, CancellationToken ct) =>
            m.SetAccessAsync(new UserId(id), PermissionScope.Server, serverId, new RoleId(roleId), granted: true, ct)).RequirePermission(Permissions.User.Manage);
        users.MapDelete("/{id:guid}/servers/{serverId:guid}/roles/{roleId:guid}", (Guid id, Guid serverId, Guid roleId, UserManagement m, CancellationToken ct) =>
            m.SetAccessAsync(new UserId(id), PermissionScope.Server, serverId, new RoleId(roleId), granted: false, ct)).RequirePermission(Permissions.User.Manage);
        return api;
    }

    private static async Task<Ok<IReadOnlyList<UserResponse>>> ListAsync(UserManagement users, CancellationToken ct) =>
        TypedResults.Ok(await users.ListAsync(ct));
}
