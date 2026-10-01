namespace Pulsar.Api;

internal static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Answers any unmatched request under <paramref name="prefix"/> with a 404 ProblemDetails, so that
    /// unknown API, hub or agent routes are not served the SPA's <c>index.html</c> by the global fallback.
    /// </summary>
    public static IEndpointConventionBuilder MapNotFoundFallback(this IEndpointRouteBuilder endpoints, string prefix) =>
        endpoints.MapGroup(prefix).MapFallback("{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound));
}
