using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Pulsar.Api.Auth;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Application.Updates;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Updates;

public sealed record UpdateSettingsRequest(bool CheckAutomatically);

/// <param name="NodeIds">The nodes to update: the one the panel runs on updates the panel too.</param>
/// <param name="Password">The administrator's current password.</param>
/// <param name="Code">The authenticator code, with two-factor authentication on (never a recovery code).</param>
/// <param name="Version">
/// The release the administrator was shown and confirmed: another one found since (by another administrator's look, or
/// the daily one) is refused with <c>update.changed</c>, to be looked at again.
/// </param>
public sealed record ApplyUpdateRequest(IReadOnlyList<Guid> NodeIds, string Password, string? Code, string Version);

/// <summary>
/// Pulsar's updates (Phase 19, docs/guides/update.md, "One-click updates"): <c>settings.manage</c> sees what is available
/// and on which nodes, turns the daily look off and on, and looks now. Updating takes the session cookie (never an API
/// key), <c>settings.manage</c>, the current password and, with two-factor authentication on, the code: an update runs
/// code as SYSTEM on the node (the release's, signed, which the node checks itself), so a stolen session alone must not
/// start one.
/// </summary>
public static class UpdateEndpoints
{
    public const int MaxNodes = 100;

    public static RouteGroupBuilder MapUpdateEndpoints(this RouteGroupBuilder api)
    {
        var updates = api.MapGroup("/updates").WithTags("Updates");
        updates.MapGet("/", (PanelUpdates panelUpdates, CancellationToken ct) => panelUpdates.GetAsync(ct)).RequirePermission(Permissions.Settings.Manage);
        updates.MapPut("/settings", SetAsync).RequirePermission(Permissions.Settings.Manage);
        updates.MapPost("/check", CheckAsync).RequirePermission(Permissions.Settings.Manage).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        updates.MapPost("/apply", ApplyAsync).RequireSignedInUser().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        return api;
    }

    private static async Task<Ok<UpdatesView>> SetAsync(UpdateSettingsRequest request, PanelUpdates panelUpdates, CancellationToken ct)
    {
        await panelUpdates.SetCheckAutomaticallyAsync(request.CheckAutomatically, ct);
        return TypedResults.Ok(await panelUpdates.GetAsync(ct));
    }

    private static async Task<Ok<UpdatesView>> CheckAsync(PanelUpdates panelUpdates, CancellationToken ct)
    {
        await panelUpdates.CheckAsync(ct);
        return TypedResults.Ok(await panelUpdates.GetAsync(ct));
    }

    private static async Task<Results<Ok<IReadOnlyList<UpdateRequestResult>>, ProblemHttpResult, UnauthorizedHttpResult, ForbidHttpResult>> ApplyAsync(
        ApplyUpdateRequest request, HttpContext http, PulsarDbContext db, UserManager<PulsarUser> users, SignInLocks locks, IAuditLog audit,
        IPermissionEvaluator evaluator, PanelUpdates panelUpdates, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        // The session cookie alone reaches here (RequireSignedInUser); the permission is checked as the others check it.
        if (!(await evaluator.GetAsync(user.Id, ct)).HasGlobal(Permissions.Settings.Manage))
        {
            return TypedResults.Forbid();
        }

        if (request.NodeIds is not { Count: > 0 and <= MaxNodes })
        {
            return ApiProblems.Create(StatusCodes.Status400BadRequest, "update.invalid_nodes", $"Name between 1 and {MaxNodes} nodes to update.");
        }

        if (await AuthEndpoints.WrongPasswordAsync(user, request.Password ?? string.Empty, users, db, locks, audit, AuditActions.Updates.Requested, ct)
            is { } wrong)
        {
            return wrong;
        }

        if (user.TwoFactorEnabled
            && await TwoFactorEndpoints.WrongCodeAsync(user, request.Code, users, db, locks, audit, AuditActions.Updates.Requested, ct) is { } wrongCode)
        {
            return wrongCode;
        }

        var requested = await panelUpdates.RequestAsync([.. request.NodeIds.Select(id => new NodeId(id))], request.Version, ct);
        return requested.IsSuccess ? TypedResults.Ok(requested.Value) : ApiProblems.FromDomainError(requested.Error!);
    }
}
