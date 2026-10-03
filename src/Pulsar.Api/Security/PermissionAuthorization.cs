using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using Pulsar.Api.Http;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Api.Security;

/// <summary>Endpoint metadata: the endpoint requires any of these global permissions (read by the endpoint coverage test).</summary>
public sealed record RequiredPermissionMetadata(IReadOnlyList<string> AnyOf);

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

internal sealed record PermissionRequirement(IReadOnlyList<string> AnyOf) : IAuthorizationRequirement;

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

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

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
            .AddRequirements(TwoFactorRequirement.Instance, new PermissionRequirement(anyOf))
            .Build();
        return builder.RequireAuthorization(policy).WithMetadata(new RequiredPermissionMetadata(anyOf));
    }

    /// <summary>Requires a signed-in user, without any particular permission.</summary>
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
