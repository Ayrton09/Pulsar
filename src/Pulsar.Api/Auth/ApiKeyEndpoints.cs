using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Auth;

/// <param name="Permissions">The only permissions the key may use (still only where you have them); null: all of yours.</param>
/// <param name="ExpiresInDays">Days until the key stops working; null: it does not expire.</param>
public sealed record CreateApiKeyRequest(
    [property: Required, StringLength(ApiKey.MaxNameLength, MinimumLength = 1)] string Name,
    List<string>? Permissions,
    [property: Range(1, 3650)] int? ExpiresInDays);

/// <summary>
/// The signed-in user's API keys (docs/architecture/02, "API keys"). Session cookie only: a key never creates or
/// revokes keys.
/// </summary>
public static class ApiKeyEndpoints
{
    public static RouteGroupBuilder MapApiKeyEndpoints(this RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/account/api-keys").WithTags("Account");
        keys.MapGet("/", async (HttpContext http, ApiKeys k, CancellationToken ct) => TypedResults.Ok(await k.ListAsync(http.User.GetRequiredUserId(), ct)))
            .RequireSignedInUser();
        keys.MapPost("/", CreateAsync).RequireSignedInUser();
        keys.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ApiKeys k, CancellationToken ct) =>
                (await k.RevokeAsync(http.User.GetRequiredUserId(), new ApiKeyId(id), ct)).ToHttp())
            .RequireSignedInUser();
        return api;
    }

    private static async Task<Results<Created<CreatedApiKey>, ProblemHttpResult>> CreateAsync(CreateApiKeyRequest request, HttpContext http, ApiKeys keys,
        TimeProvider clock, CancellationToken ct)
    {
        var expiresAt = request.ExpiresInDays is { } days ? clock.GetUtcNow().AddDays(days) : (DateTimeOffset?)null;
        var created = await keys.CreateAsync(http.User.GetRequiredUserId(), request.Name, request.Permissions, expiresAt, ct);
        return created.IsSuccess
            ? TypedResults.Created($"/api/v1/account/api-keys/{created.Value.Key.Id}", created.Value)
            : ApiProblems.FromDomainError(created.Error!);
    }
}
