using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Pulsar.Api.Auth;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Sftp;

/// <param name="Name">What the key is for ("Work laptop"); empty: the comment after the key.</param>
/// <param name="PublicKey">
/// The public key's line in OpenSSH format: <c>ssh-ed25519 AAAA… comment</c>, as in ssh-keygen's <c>.pub</c> file or
/// PuTTYgen's "Public key for pasting into OpenSSH authorized_keys file".
/// </param>
/// <param name="Password">The current password.</param>
/// <param name="Code">With two-factor authentication on: the code the authenticator app shows now (not a recovery code).</param>
public sealed record AddSshKeyRequest(
    [property: StringLength(SshKey.MaxNameLength)] string? Name,
    [property: Required, StringLength(SshPublicKey.MaxTextLength)] string PublicKey,
    [property: Required, StringLength(256)] string Password,
    [property: StringLength(16)] string? Code);

/// <summary>
/// The signed-in user's SSH keys for SFTP (docs/guides/sftp.md, "SSH keys"). Session cookie only, like the rest of the
/// account: an API key never adds or deletes them. Adding one takes the password and, with two-factor authentication on,
/// the authenticator code: a key alone signs in, so a stolen session alone must not add one.
/// </summary>
public static class SshKeyEndpoints
{
    public static RouteGroupBuilder MapSshKeyEndpoints(this RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/account/ssh-keys").WithTags("Account");
        keys.MapGet("/", async (HttpContext http, SshKeys k, CancellationToken ct) => TypedResults.Ok(await k.ListAsync(http.User.GetRequiredUserId(), ct)))
            .RequireSignedInUser();
        keys.MapPost("/", AddAsync).RequireSignedInUser().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        keys.MapDelete("/{id:guid}", DeleteAsync).RequireSignedInUser();
        return api;
    }

    private static async Task<Results<Created<SshKeyView>, ProblemHttpResult, UnauthorizedHttpResult>> AddAsync(AddSshKeyRequest request,
        HttpContext http, PulsarDbContext db, UserManager<PulsarUser> users, SignInLocks locks, IAuditLog audit, SshKeys keys, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(http.User.GetRequiredUserId().ToString());
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        // The key first: a mistake in it (or a key added already, or one too many) costs neither a password attempt nor the code.
        var parsed = SshPublicKey.Parse(request.PublicKey);
        if (!parsed.IsSuccess)
        {
            return ApiProblems.FromDomainError(parsed.Error!);
        }

        var valid = await keys.ValidateAsync(user.Id, request.Name, parsed.Value, ct);
        if (!valid.IsSuccess)
        {
            return ApiProblems.FromDomainError(valid.Error!);
        }

        if (await AuthEndpoints.WrongPasswordAsync(user, request.Password, users, db, locks, audit, AuditActions.Auth.SshKeyAdded, ct) is { } wrong)
        {
            return wrong;
        }

        if (user.TwoFactorEnabled
            && await TwoFactorEndpoints.WrongCodeAsync(user, request.Code, users, db, locks, audit, AuditActions.Auth.SshKeyAdded, ct) is { } wrongCode)
        {
            return wrongCode;
        }

        var added = await keys.AddAsync(user.Id, request.Name, parsed.Value, ct);
        return added.IsSuccess
            ? TypedResults.Created($"/api/v1/account/ssh-keys/{added.Value.Id}", added.Value)
            : ApiProblems.FromDomainError(added.Error!);
    }

    /// <summary>The SFTP sessions the key signed in end with it.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, HttpContext http, SshKeys keys, SftpConnections sftp,
        CancellationToken ct)
    {
        var user = http.User.GetRequiredUserId();
        var deleted = await keys.DeleteAsync(user, new SshKeyId(id), ct);
        if (deleted.IsSuccess)
        {
            sftp.EndKey(user, new SshKeyId(id));
        }

        return deleted.ToHttp();
    }
}
