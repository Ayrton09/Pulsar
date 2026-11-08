using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Auth;

/// <param name="Permissions">The only permissions the key may use (still only where you have them); null: all of yours.</param>
/// <param name="ExpiresInDays">Days until the key stops working; null: it does not expire.</param>
/// <param name="Password">The current password.</param>
/// <param name="Code">With two-factor authentication on: the code the authenticator app shows now (not a recovery code).</param>
public sealed record CreateApiKeyRequest(
    [property: Required, StringLength(ApiKey.MaxNameLength, MinimumLength = 1)] string Name,
    List<string>? Permissions,
    [property: Range(1, 3650)] int? ExpiresInDays,
    [property: Required, StringLength(256)] string Password,
    [property: StringLength(16)] string? Code);

/// <summary>
/// The signed-in user's API keys (docs/architecture/02, "API keys"). Session cookie only: a key never creates or
/// revokes keys. Creating one takes the password and, with two-factor authentication on, the authenticator code (1.0.36):
/// a key works on its own, so a stolen session alone must not make one.
/// </summary>
public static class ApiKeyEndpoints
{
    public static RouteGroupBuilder MapApiKeyEndpoints(this RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/account/api-keys").WithTags("Account");
        keys.MapGet("/", async (HttpContext http, ApiKeys k, CancellationToken ct) => TypedResults.Ok(await k.ListAsync(http.User.GetRequiredUserId(), ct)))
            .RequireSignedInUser();
        keys.MapPost("/", CreateAsync).RequireSignedInUser().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        keys.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ApiKeys k, CancellationToken ct) =>
                (await k.RevokeAsync(http.User.GetRequiredUserId(), new ApiKeyId(id), ct)).ToHttp())
            .RequireSignedInUser();
        return api;
    }

    private static async Task<Results<Created<CreatedApiKey>, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(CreateApiKeyRequest request,
        HttpContext http, ApiKeys keys, PulsarDbContext db, UserManager<PulsarUser> users, SignInLocks locks, IAuditLog audit, TimeProvider clock,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        // The request first: a mistake in it (or one key too many) costs neither a password attempt nor the code.
        var expiresAt = request.ExpiresInDays is { } days ? clock.GetUtcNow().AddDays(days) : (DateTimeOffset?)null;
        var valid = await keys.ValidateAsync(user.Id, request.Name, request.Permissions, expiresAt, ct);
        if (!valid.IsSuccess)
        {
            return ApiProblems.FromDomainError(valid.Error!);
        }

        if (await AuthEndpoints.WrongPasswordAsync(user, request.Password, users, db, locks, audit, AuditActions.Auth.ApiKeyCreated, ct) is { } wrong)
        {
            return wrong;
        }

        if (user.TwoFactorEnabled
            && await TwoFactorEndpoints.WrongCodeAsync(user, request.Code, users, db, locks, audit, AuditActions.Auth.ApiKeyCreated, ct) is { } wrongCode)
        {
            return wrongCode;
        }

        var created = await keys.CreateAsync(user.Id, request.Name, request.Permissions, expiresAt, ct);
        return created.IsSuccess
            ? TypedResults.Created($"/api/v1/account/api-keys/{created.Value.Key.Id}", created.Value)
            : ApiProblems.FromDomainError(created.Error!);
    }
}
