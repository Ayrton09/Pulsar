using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Databases;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Databases;

namespace Pulsar.Api.Databases;

/// <param name="Name">What follows the server's prefix: 1 to 20 lowercase letters, digits and underscores.</param>
public sealed record CreateDatabaseRequest(Guid HostId, [property: Required, StringLength(ServerDatabase.MaxSuffixLength)] string Name);

/// <param name="Password">Required to create; leave it out on an update to keep the current one.</param>
/// <param name="ServerAddress">Where game servers connect, when not <paramref name="Host"/> (e.g. 127.0.0.1 for a database on the node).</param>
/// <param name="UserHost">Where the users it creates may connect from: %, an address or a pattern; % when left out.</param>
/// <param name="NodeIds">Nodes whose servers may use it; empty: every node.</param>
/// <param name="RequireTls">
/// Refuse to connect without TLS. Left out: on a new host, required unless its address is localhost, a loopback, private
/// or link-local address; on an update, unchanged.
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
    bool? RequireTls = null)
{
    internal DatabaseHostDefinition ToDefinition() =>
        new(Name, Host, Port, UserName, Password, ServerAddress, ServerPort, UserHost, NodeIds ?? [], RequireTls);
}

/// <summary>
/// Game databases (docs/architecture/07, "Databases"): a server's MySQL/MariaDB databases, and the database hosts they
/// are created on (panel settings: their accounts are the panel's).
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
        databases.MapPost("/{databaseId:guid}/rotate-password", async (Guid id, Guid databaseId, ServerDatabases d, HttpResponse response,
                CancellationToken ct) => NoStore(response, (await d.RotatePasswordAsync(new ServerId(id), databaseId, ct)).ToHttp()))
            .RequirePermission(Permissions.Server.DatabasesManage);
        databases.MapDelete("/{databaseId:guid}", async (Guid id, Guid databaseId, ServerDatabases d, CancellationToken ct) =>
                (await d.DeleteAsync(new ServerId(id), databaseId, ct)).ToHttp())
            .RequirePermission(Permissions.Server.DatabasesManage);

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
