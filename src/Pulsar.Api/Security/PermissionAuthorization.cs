using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Http;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Api.Security;

/// <summary>Endpoint metadata: the endpoint requires any of these permissions (read by the endpoint coverage test).</summary>
/// <param name="Anywhere">Held at any scope is enough at the gate: the endpoint shows or acts only where it applies.</param>
public sealed record RequiredPermissionMetadata(IReadOnlyList<string> AnyOf, bool Anywhere = false);

/// <summary>Endpoint metadata: the endpoint is available to any signed-in user (e.g. "who am I", logout).</summary>
public sealed class SignedInUserMetadata
{
    public static SignedInUserMetadata Instance { get; } = new();
}

/// <summary>
/// Endpoint metadata: open to a signed-in user who still has to set up the two-factor authentication a role requires
/// (the session, logout and setting it up).
/// </summary>
public sealed class AllowWithoutTwoFactorMetadata
{
    public static AllowWithoutTwoFactorMetadata Instance { get; } = new();
}

internal sealed record PermissionRequirement(IReadOnlyList<string> AnyOf, bool Anywhere) : IAuthorizationRequirement;

/// <summary>A resource the user may not even see: answered 404 like one that does not exist (docs/architecture/02).</summary>
internal sealed record HiddenResourceFailure(string Code, string Title);

/// <summary>Part of every signed-in policy: a user whose role requires two-factor authentication must have it.</summary>
internal sealed class TwoFactorRequirement : IAuthorizationRequirement
{
    public const string FailureReason = "auth.two_factor_required";

    public static TwoFactorRequirement Instance { get; } = new();
}

internal sealed class TwoFactorAuthorizationHandler(IPermissionEvaluator evaluator) : AuthorizationHandler<TwoFactorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TwoFactorRequirement requirement)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return;
        }

        var http = context.Resource as HttpContext;
        if (context.User.HasClaim(PulsarClaimsPrincipalFactory.TwoFactorClaim, "true")
            || http?.GetEndpoint()?.Metadata.GetMetadata<AllowWithoutTwoFactorMetadata>() is not null
            || !(await evaluator.GetAsync(user, http?.RequestAborted ?? CancellationToken.None)).RequiresTwoFactor)
        {
            context.Succeed(requirement);
            return;
        }

        context.Fail(new AuthorizationFailureReason(this, TwoFactorRequirement.FailureReason));
    }
}

/// <summary>Answers a missing two-factor setup with its own code, so the SPA can send the user to set it up.</summary>
internal sealed class PulsarAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.Any(r => r.Message == TwoFactorRequirement.FailureReason) == true)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ApiProblems.Create(StatusCodes.Status403Forbidden, TwoFactorRequirement.FailureReason,
                "Set up two-factor authentication to continue.").ExecuteAsync(context);
        }

        if (authorizeResult.Forbidden && context.Items[typeof(HiddenResourceFailure)] is HiddenResourceFailure hidden)
        {
            return ApiProblems.Create(StatusCodes.Status404NotFound, hidden.Code, hidden.Title).ExecuteAsync(context);
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

/// <summary>
/// The permission gate of every endpoint, with role assignments on nodes and servers (docs/architecture/02): routes
/// under <c>/servers/{id}</c> and <c>/nodes/{id}</c> are checked against that server or node, so a role on one server
/// opens nothing on another; a server or node the user may not see at all is answered 404, as if it did not exist.
/// Other routes need the permission globally, or anywhere for the lists and pages that show only what it applies to.
/// </summary>
internal sealed class PermissionAuthorizationHandler(IPermissionEvaluator evaluator, IPulsarDbContext db) : AuthorizationHandler<PermissionRequirement>
{
    private const string ServerRoute = "/api/v1/servers/{id";
    private const string NodeRoute = "/api/v1/nodes/{id";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return;
        }

        var http = context.Resource as HttpContext;
        var cancellationToken = http?.RequestAborted ?? CancellationToken.None;
        var permissions = await evaluator.GetAsync(user, cancellationToken);
        if (requirement.AnyOf.Any(permissions.HasGlobal)
            || (requirement.Anywhere && requirement.AnyOf.Any(permissions.HasAnywhere)))
        {
            context.Succeed(requirement);
            return;
        }

        var route = (http?.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (route is null || http?.GetRouteValue("id") is not string raw || !Guid.TryParse(raw, out var id)
            || !(route.StartsWith(ServerRoute, StringComparison.Ordinal) || route.StartsWith(NodeRoute, StringComparison.Ordinal)))
        {
            return;
        }

        var server = route.StartsWith(ServerRoute, StringComparison.Ordinal);
        var visibility = server ? ResourceVisibility.Server : ResourceVisibility.Node;
        if (!requirement.AnyOf.Concat(visibility).Any(permissions.HasAnywhere))
        {
            return; // nothing anywhere: a plain 403, as before scoped roles
        }

        var target = server
            ? await db.Servers.Where(s => s.Id == new ServerId(id)).Select(s => AccessTarget.ForServer(s.Id, s.NodeId)).SingleOrDefaultAsync(cancellationToken)
            : await db.Nodes.AnyAsync(n => n.Id == new NodeId(id), cancellationToken) ? AccessTarget.ForNode(new NodeId(id)) : null;
        if (target is null)
        {
            // Gone or never there: the endpoint says so, as it would to anyone.
            if (requirement.AnyOf.Any(permissions.HasAnywhere))
            {
                context.Succeed(requirement);
            }

            return;
        }

        if (requirement.AnyOf.Any(p => permissions.Has(p, target)))
        {
            context.Succeed(requirement);
        }
        else if (!visibility.Any(p => permissions.Has(p, target)) && http is not null)
        {
            http.Items[typeof(HiddenResourceFailure)] = server
                ? new HiddenResourceFailure(ServerErrors.NotFound.Code, ServerErrors.NotFound.Message)
                : new HiddenResourceFailure(NodeErrors.NotFound.Code, NodeErrors.NotFound.Message);
        }
    }
}

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Requires a signed-in user holding any of <paramref name="anyOf"/> globally. Resource-scoped checks
    /// (node/server) happen in the handler, which answers 404 for resources the user cannot see.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params string[] anyOf)
        where TBuilder : IEndpointConventionBuilder => Require(builder, anyOf, anywhere: false);

    /// <summary>
    /// Requires any of <paramref name="anyOf"/> at any scope (a role on one node or server is enough): for lists and
    /// catalogs that show only what the permission applies to, and for actions that check their target themselves.
    /// </summary>
    public static TBuilder RequirePermissionAnywhere<TBuilder>(this TBuilder builder, params string[] anyOf)
        where TBuilder : IEndpointConventionBuilder => Require(builder, anyOf, anywhere: true);

    private static TBuilder Require<TBuilder>(TBuilder builder, string[] anyOf, bool anywhere)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentOutOfRangeException.ThrowIfZero(anyOf.Length, nameof(anyOf));
        if (anyOf.FirstOrDefault(p => !PermissionCatalog.Exists(p)) is { } unknown)
        {
            throw new ArgumentException($"Unknown permission '{unknown}'.", nameof(anyOf));
        }

        // The session cookie or an API key (docs/architecture/02, "API keys").
        var policy = new AuthorizationPolicyBuilder(UserSchemes.CookieOrApiKey)
            .RequireAuthenticatedUser()
            .AddRequirements(TwoFactorRequirement.Instance, new PermissionRequirement(anyOf, anywhere))
            .Build();
        return builder.RequireAuthorization(policy).WithMetadata(new RequiredPermissionMetadata(anyOf, anywhere));
    }

    /// <summary>Requires a signed-in user (the session cookie: never an API key), without any particular permission.</summary>
    public static TBuilder RequireSignedInUser<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        var policy = new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(TwoFactorRequirement.Instance)
            .Build();
        return builder.RequireAuthorization(policy).WithMetadata(SignedInUserMetadata.Instance);
    }

    /// <summary>Keeps the endpoint open to a user who still has to set up two-factor authentication.</summary>
    public static TBuilder AllowWithoutTwoFactor<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(AllowWithoutTwoFactorMetadata.Instance);
}
