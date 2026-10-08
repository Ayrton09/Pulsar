using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
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

        // Calls made within an agent session (the hub's scheme, never a browser cookie).
        var session = app.MapGroup(AgentProtocol.BasePath)
            .WithMetadata(AgentEndpointMetadata.Instance)
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(AgentAuthenticationHandler.Scheme).RequireAuthenticatedUser())
            .ExcludeFromDescription();
        session.MapPost("/credentials/rotate", RotateCredentialAsync).RequireRateLimiting(AuthRateLimit);
        session.MapGet("/transfers/{ticket}", SendToAgentAsync);
        session.MapPut("/transfers/{ticket}", ReceiveFromAgentAsync);
        return app;
    }

    /// <summary>
    /// The agent takes what a browser uploads (docs/architecture/03, "Large transfers"): the browser's request body is
    /// piped into this response as it arrives.
    /// </summary>
    private static async Task SendToAgentAsync(string ticket, HttpContext http, FileTransfers transfers)
    {
        var upload = transfers.ClaimUpload(ticket, http.User.NodeId());
        if (upload is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        http.Response.ContentType = "application/octet-stream";
        http.Response.ContentLength = upload.Size;
        try
        {
            var buffer = new byte[81920];
            long sent = 0;
            while (sent < upload.Size)
            {
                var read = await upload.Content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, upload.Size - sent)), http.RequestAborted);
                if (read == 0)
                {
                    break;
                }

                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                sent += read;
                upload.Progressed();
            }

            if (sent < upload.Size)
            {
                upload.Fail("The browser stopped sending the file.");
                http.Abort();
                return;
            }

            await http.Response.CompleteAsync();
            upload.ContentSent();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException)
        {
            upload.Fail("The upload was interrupted.");
            http.Abort();
        }
    }

    /// <summary>The agent sends what a browser downloads: its request body is piped to the browser's response.</summary>
    private static async Task<IResult> ReceiveFromAgentAsync(string ticket, HttpContext http, FileTransfers transfers)
    {
        var download = transfers.ClaimDownload(ticket, http.User.NodeId());
        if (download is null)
        {
            return Results.NotFound();
        }

        if (http.Request.ContentLength is not { } length)
        {
            download.Dispose();
            return Results.StatusCode(StatusCodes.Status411LengthRequired);
        }

        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }

        // The request must stay open until the browser has all of it.
        return await download.DeliverAsync(http.Request.Body, length) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status410Gone);
    }

    private static async Task<Results<Ok<SignedMessage>, BadRequest<AgentError>, UnauthorizedHttpResult>> RotateCredentialAsync(
        RotateCredentialRequest request, HttpContext http, AgentAccess access, CancellationToken ct)
    {
        var principal = http.User;
        var result = await access.RotateCredentialAsync(principal.NodeId(), principal.CredentialId(), principal.SessionId(), request, ct);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        return result.Error == AgentErrors.AuthenticationRejected
            ? TypedResults.Unauthorized()
            : TypedResults.BadRequest(new AgentError(result.Error!.Code, result.Error.Message));
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
