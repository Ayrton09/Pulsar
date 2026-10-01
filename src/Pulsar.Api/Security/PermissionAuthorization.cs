using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;

namespace Pulsar.Api.Security;

/// <summary>Endpoint metadata: the endpoint requires any of these global permissions (read by the endpoint coverage test).</summary>
public sealed record RequiredPermissionMetadata(IReadOnlyList<string> AnyOf);

/// <summary>Endpoint metadata: the endpoint is available to any signed-in user (e.g. "who am I", logout).</summary>
public sealed class SignedInUserMetadata
{
    public static SignedInUserMetadata Instance { get; } = new();
}

internal sealed record PermissionRequirement(IReadOnlyList<string> AnyOf) : IAuthorizationRequirement;

internal sealed class PermissionAuthorizationHandler(IPermissionEvaluator evaluator) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return;
        }

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        var permissions = await evaluator.GetAsync(user, cancellationToken);
        if (requirement.AnyOf.Any(permissions.HasGlobal))
        {
            context.Succeed(requirement);
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
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentOutOfRangeException.ThrowIfZero(anyOf.Length, nameof(anyOf));
        if (anyOf.FirstOrDefault(p => !PermissionCatalog.Exists(p)) is { } unknown)
        {
            throw new ArgumentException($"Unknown permission '{unknown}'.", nameof(anyOf));
        }

        var policy = new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(anyOf))
            .Build();
        return builder.RequireAuthorization(policy).WithMetadata(new RequiredPermissionMetadata(anyOf));
    }

    /// <summary>Requires a signed-in user, without any particular permission.</summary>
    public static TBuilder RequireSignedInUser<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        var policy = new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser().Build();
        return builder.RequireAuthorization(policy).WithMetadata(SignedInUserMetadata.Instance);
    }
}
