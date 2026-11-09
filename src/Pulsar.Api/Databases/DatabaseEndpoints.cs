using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Databases;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Databases;

namespace Pulsar.Api.Databases;

/// <param name="Name">What follows the server's prefix: 1 to 20 lowercase letters, digits and underscores.</param>
public sealed record CreateDatabaseRequest(Guid HostId, [property: Required, StringLength(ServerDatabase.MaxSuffixLength)] string Name);

/// <param name="HostId">The database host its game's database is on (made there the first time); null: the game's own files.</param>
public sealed record GameDatabaseRequest(Guid? HostId);

/// <param name="Password">Required to create; leave it out on an update to keep the current one.</param>
/// <param name="ServerAddress">Where game servers connect, when not <paramref name="Host"/> (e.g. 127.0.0.1 for a database on the node).</param>
/// <param name="UserHost">Where the users it creates may connect from: %, an address or a pattern; % when left out.</param>
/// <param name="NodeIds">Nodes whose servers may use it; empty: every node.</param>
/// <param name="RequireTls">
/// Refuse to connect without TLS. Left out: on a new host, required unless its address is localhost, a loopback, private
/// or link-local address; on an update, unchanged.
/// </param>
/// <param name="Engine">
/// What it runs: <c>mysql</c> (MySQL or MariaDB, when a new host leaves it out) or <c>postgres</c> (PostgreSQL, whose
/// account needs CREATEDB and CREATEROLE). Chosen when the host is added: an update leaves it out or repeats it.
/// </param>
public sealed record DatabaseHostRequest(
    [property: Required, StringLength(DatabaseHost.MaxNameLength)] string Name,
    [property: Required, StringLength(DatabaseHost.MaxHostLength)] string Host,
    [property: Range(1, 65535)] int Port,
    [property: Required, StringLength(DatabaseHost.MaxUserNameLength)] string UserName,
    [property: StringLength(256)] string? Password,
    [property: StringLength(DatabaseHost.MaxHostLength)] string? ServerAddress,
    [property: Range(1, 65535)] int? ServerPort,
    [property: StringLength(DatabaseHost.MaxHostLength)] string? UserHost,
    [property: MaxLength(500)] IReadOnlyList<Guid>? NodeIds,
    bool? RequireTls = null,
    DatabaseEngine? Engine = null)
{
    internal DatabaseHostDefinition ToDefinition() =>
        new(Name, Host, Port, UserName, Password, ServerAddress, ServerPort, UserHost, NodeIds ?? [], RequireTls, Engine);
}

/// <summary>
/// Game databases (docs/architecture/07, "Databases"): a server's MySQL/MariaDB and PostgreSQL databases, and the
/// database hosts they are created on (panel settings: their accounts are the panel's).
/// </summary>
public static class DatabaseEndpoints
{
    public static RouteGroupBuilder MapDatabaseEndpoints(this RouteGroupBuilder api)
    {
        var databases = api.MapGroup("/servers/{id:guid}/databases").WithTags("Databases");
        databases.MapGet("/", async (Guid id, ServerDatabases d, CancellationToken ct) => (await d.ListAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.DatabasesView);
        databases.MapPost("/", CreateAsync).RequirePermission(Permissions.Server.DatabasesManage);
        databases.MapGet("/{databaseId:guid}/password", async (Guid id, Guid databaseId, ServerDatabases d, HttpResponse response, CancellationToken ct) =>
                NoStore(response, (await d.PasswordAsync(new ServerId(id), databaseId, ct)).ToHttp()))
            .RequirePermission(Permissions.Server.DatabasesManage);
        databases.MapPost("/{databaseId:guid}/rotate-password", RotatePasswordAsync).RequirePermission(Permissions.Server.DatabasesManage);
        databases.MapDelete("/{databaseId:guid}", async (Guid id, Guid databaseId, ServerDatabases d, CancellationToken ct) =>
                (await d.DeleteAsync(new ServerId(id), databaseId, ct)).ToHttp())
            .RequirePermission(Permissions.Server.DatabasesManage);
        databases.MapPut("/game", SetGameAsync).RequirePermission(Permissions.Server.DatabasesManage);

        var hosts = api.MapGroup("/database-hosts").WithTags("Databases").RequirePermission(Permissions.Settings.Manage);
        hosts.MapGet("/", async (DatabaseHosts h, CancellationToken ct) => TypedResults.Ok(await h.ListAsync(ct)));
        hosts.MapPost("/", CreateHostAsync);
        hosts.MapPut("/{hostId:guid}", async (Guid hostId, DatabaseHostRequest request, DatabaseHosts h, CancellationToken ct) =>
            (await h.UpdateAsync(hostId, request.ToDefinition(), ct)).ToHttp());
        hosts.MapDelete("/{hostId:guid}", async (Guid hostId, DatabaseHosts h, CancellationToken ct) => (await h.DeleteAsync(hostId, ct)).ToHttp());
        hosts.MapPost("/{hostId:guid}/test", async (Guid hostId, DatabaseHosts h, CancellationToken ct) => (await h.TestAsync(hostId, ct)).ToHttp());
        return api;
    }

    private static async Task<Results<Created<ServerDatabaseCredentials>, ProblemHttpResult>> CreateAsync(Guid id, CreateDatabaseRequest request,
        ServerDatabases databases, HttpResponse response, CancellationToken ct)
    {
        var result = await databases.CreateAsync(new ServerId(id), request.HostId, request.Name, ct);
        NoStore(response, result);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/servers/{id}/databases/{result.Value.Database.Id}", result.Value)
            : ApiProblems.FromDomainError(result.Error!);
    }

    /// <summary>
    /// Where the server's game keeps its data from its next start (1.0.37); its node gets the server's spec at once. A
    /// node that does not take it would go on starting the game as before: the choice is undone, and its answer is said.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetGameAsync(Guid id, GameDatabaseRequest request, ServerDatabases databases,
        ServerManagement servers, CancellationToken ct)
    {
        var set = await databases.SetGameDatabaseAsync(new ServerId(id), request.HostId, () => SyncAsync(servers, new ServerId(id)), ct);
        return set.IsSuccess ? TypedResults.NoContent() : ApiProblems.FromDomainError(set.Error!);
    }

    /// <summary>A new password for the game's database reaches its node with the server's spec (the game uses it from its next start).</summary>
    private static async Task<Results<Ok<ServerDatabaseCredentials>, ProblemHttpResult>> RotatePasswordAsync(Guid id, Guid databaseId,
        ServerDatabases databases, ServerManagement servers, HttpResponse response, CancellationToken ct)
    {
        var rotated = await databases.RotatePasswordAsync(new ServerId(id), databaseId, ct);
        NoStore(response, rotated);
        if (!rotated.IsSuccess)
        {
            return ApiProblems.FromDomainError(rotated.Error!);
        }

        // The host has the new password now: a node that did not take it is said (Show password gives it again).
        if (rotated.Value.Database.ForGame && await SyncAsync(servers, new ServerId(id)) is not null)
        {
            return ApiProblems.FromDomainError(DatabaseErrors.PasswordNotGiven);
        }

        return TypedResults.Ok(rotated.Value);
    }

    /// <summary>Sends the server's spec to its node: null when it took it, otherwise why not (a spec the panel would not build too).</summary>
    private static async Task<DomainError?> SyncAsync(ServerManagement servers, ServerId id)
    {
        try
        {
            var synced = await servers.SyncSpecAsync(id, CancellationToken.None);
            return synced.IsSuccess ? null : synced.Error;
        }
        catch (DomainErrorException ex)
        {
            return ex.Error;
        }
    }

    /// <summary>Answers that carry a password are kept by no cache (the browser's included).</summary>
    private static T NoStore<T>(HttpResponse response, T result)
    {
        response.Headers.CacheControl = "no-store";
        return result;
    }

    private static async Task<Results<Created<DatabaseHostView>, ProblemHttpResult>> CreateHostAsync(DatabaseHostRequest request, DatabaseHosts hosts,
        CancellationToken ct)
    {
        var result = await hosts.CreateAsync(request.ToDefinition(), ct);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/database-hosts/{result.Value.Id}", result.Value)
            : ApiProblems.FromDomainError(result.Error!);
    }
}
