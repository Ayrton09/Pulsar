using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api.Agents;

/// <summary>
/// HTTP endpoints of the agent protocol (docs/architecture/03). They live outside /api/v1: no cookies, no
/// antiforgery; each request proves itself (enrollment token, node key signature).
/// </summary>
internal static class AgentEndpoints
{
    public const string EnrollRateLimit = "agent-enroll";
    public const string AuthRateLimit = "agent-auth";

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // Not in the OpenAPI document: that document describes the browser API; the agent protocol is Pulsar.Contracts.
        var agent = app.MapGroup(AgentProtocol.BasePath)
            .WithMetadata(AgentEndpointMetadata.Instance)
            .AllowAnonymous()
            .ExcludeFromDescription();
        agent.MapGet("/panel-identity", async (IPanelKeyRing keys, CancellationToken ct) =>
            TypedResults.Ok(new PanelIdentityResponse(await keys.GetPublicKeysAsync(ct))));
        agent.MapPost("/enroll", EnrollAsync).RequireRateLimiting(EnrollRateLimit);
        agent.MapPost("/auth/challenge", ChallengeAsync).RequireRateLimiting(AuthRateLimit);
        agent.MapPost("/auth/token", TokenAsync).RequireRateLimiting(AuthRateLimit);
        return app;
    }

    /// <summary>The origin the agent reached ("https://panel.example.com"), bound into its signatures.</summary>
    public static string PanelOrigin(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}".ToLowerInvariant();

    private static async Task<Results<Ok<SignedMessage>, BadRequest<AgentError>>> EnrollAsync(
        EnrollRequest request, HttpContext http, AgentAccess access, CancellationToken ct)
    {
        var result = await access.EnrollAsync(request, PanelOrigin(http.Request), ct);
        return result.IsSuccess
            ? TypedResults.Ok(result.Value)
            : TypedResults.BadRequest(new AgentError(result.Error!.Code, result.Error.Message));
    }

    private static async Task<Results<Ok<ChallengeResponse>, UnauthorizedHttpResult>> ChallengeAsync(
        ChallengeRequest request, HttpContext http, AgentAccess access, CancellationToken ct)
    {
        var result = await access.ChallengeAsync(request, PanelOrigin(http.Request), ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : TypedResults.Unauthorized();
    }

    private static async Task<Results<Ok<TokenResponse>, UnauthorizedHttpResult>> TokenAsync(
        TokenRequest request, HttpContext http, AgentAccess access, CancellationToken ct)
    {
        var result = await access.AuthenticateAsync(request, PanelOrigin(http.Request), ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : TypedResults.Unauthorized();
    }
}
