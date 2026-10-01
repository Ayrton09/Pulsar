using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;

namespace Pulsar.Api.Security;

/// <summary>Endpoint metadata: the global permission an endpoint requires (read by the endpoint coverage test).</summary>
public sealed record RequiredPermissionMetadata(string Permission);

/// <summary>Endpoint metadata: the endpoint is available to any signed-in user (e.g. "who am I", logout).</summary>
public sealed class SignedInUserMetadata
{
    public static SignedInUserMetadata Instance { get; } = new();
}

internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

internal sealed class PermissionAuthorizationHandler(IPermissionEvaluator evaluator) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return;
        }

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        if (await evaluator.HasAsync(user, requirement.Permission, AccessTarget.Global, cancellationToken))
        {
            context.Succeed(requirement);
        }
    }
}

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Requires a signed-in user holding <paramref name="permission"/> globally. Resource-scoped checks (node/server)
    /// happen in the handler, which answers 404 for resources the user cannot see.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!PermissionCatalog.Exists(permission))
        {
            throw new ArgumentException($"Unknown permission '{permission}'.", nameof(permission));
        }

        var policy = new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
        return builder.RequireAuthorization(policy).WithMetadata(new RequiredPermissionMetadata(permission));
    }

    /// <summary>Requires a signed-in user, without any particular permission.</summary>
    public static TBuilder RequireSignedInUser<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        var policy = new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser().Build();
        return builder.RequireAuthorization(policy).WithMetadata(SignedInUserMetadata.Instance);
    }
}
