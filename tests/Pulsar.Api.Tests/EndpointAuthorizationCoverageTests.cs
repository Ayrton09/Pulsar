using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Agents;
using Pulsar.Api.Security;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests;

/// <summary>
/// Every endpoint must state who may call it (docs/architecture/02): a permission, "any signed-in user", or
/// explicitly anonymous. A new endpoint that forgets it fails here instead of shipping open.
/// </summary>
public sealed class EndpointAuthorizationCoverageTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    [Fact]
    public void EveryEndpointDeclaresItsAuthorization()
    {
        _ = factory.Server;
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.Contains(endpoints, e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true);

        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>() is null
                && e.Metadata.GetMetadata<SignedInUserMetadata>() is null
                && e.Metadata.GetMetadata<IAllowAnonymous>() is null
                && e.Metadata.GetMetadata<AgentEndpointMetadata>() is null
                && !IsFrameworkEndpoint(e))
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}")
            .ToList();

        Assert.True(undeclared.Count == 0, "Endpoints without authorization metadata: " + string.Join("; ", undeclared));
    }

    // Static files, the SPA fallback and the 404 fallbacks for backend prefixes serve no data.
    private static bool IsFrameworkEndpoint(RouteEndpoint endpoint)
    {
        var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
        return endpoint.DisplayName?.StartsWith("Fallback", StringComparison.Ordinal) == true
            || pattern.EndsWith("{**path}", StringComparison.Ordinal)
            || pattern.EndsWith("{*path:nonfile}", StringComparison.Ordinal)
            || !pattern.StartsWith('/') && !pattern.StartsWith("api", StringComparison.Ordinal) && !pattern.StartsWith("health", StringComparison.Ordinal);
    }
}
