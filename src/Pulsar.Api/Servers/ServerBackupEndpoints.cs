using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Servers;

/// <param name="Name">Defaults to the time it was asked for.</param>
public sealed record CreateBackupRequest([property: StringLength(Backup.MaxNameLength)] string? Name);

public sealed record LockBackupRequest(bool Locked);

/// <param name="Limit">How many backups to keep (locked ones besides); null keeps them all.</param>
public sealed record BackupSettingsRequest([property: Range(1, Server.MaxBackupLimit)] int? Limit);

/// <summary>
/// A server's backups (docs/architecture/07, "Backups"): listing, making one (it may run while the server does),
/// downloading (streamed from the node), restoring (the server stopped), locking, deleting and the retention limit. A
/// backup being made is cancelled like any operation (<c>POST /servers/{id}/operations/{operationId}/cancel</c>).
/// </summary>
public static class ServerBackupEndpoints
{
    public static RouteGroupBuilder MapServerBackupEndpoints(this RouteGroupBuilder api)
    {
        var backups = api.MapGroup("/servers/{id:guid}/backups").WithTags("Backups");
        backups.MapGet("/", async (Guid id, ServerBackups b, CancellationToken ct) => (await b.ListAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.BackupsView);
        backups.MapPost("/", CreateAsync).RequirePermission(Permissions.Server.BackupsCreate);
        backups.MapGet("/{backupId:guid}/download", (Guid id, Guid backupId, ServerBackups b, HttpContext http) =>
                ServerFileEndpoints.SendAsync(http, b.OpenAsync(new ServerId(id), new BackupId(backupId), http.RequestAborted), attachment: true,
                    contentType: "application/zip"))
            .RequirePermission(Permissions.Server.BackupsView)
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status404NotFound);
        backups.MapPost("/{backupId:guid}/restore", async (Guid id, Guid backupId, ServerBackups b, CancellationToken ct) =>
                (await b.RestoreAsync(new ServerId(id), new BackupId(backupId), ct)).ToHttp())
            .RequirePermission(Permissions.Server.BackupsRestore);
        backups.MapPut("/{backupId:guid}/lock", async (Guid id, Guid backupId, LockBackupRequest request, ServerBackups b, CancellationToken ct) =>
                (await b.SetLockedAsync(new ServerId(id), new BackupId(backupId), request.Locked, ct)).ToHttp())
            .RequirePermission(Permissions.Server.BackupsDelete);
        backups.MapDelete("/{backupId:guid}", async (Guid id, Guid backupId, ServerBackups b, CancellationToken ct) =>
                (await b.DeleteAsync(new ServerId(id), new BackupId(backupId), ct)).ToHttp())
            .RequirePermission(Permissions.Server.BackupsDelete);
        // Retention deletes backups: the same right as deleting them by hand.
        backups.MapPut("/settings", async (Guid id, BackupSettingsRequest request, ServerBackups b, CancellationToken ct) =>
                (await b.SetLimitAsync(new ServerId(id), request.Limit, ct)).ToHttp())
            .RequirePermission(Permissions.Server.BackupsDelete);
        return api;
    }

    private static async Task<Results<Created<BackupView>, ProblemHttpResult>> CreateAsync(Guid id, CreateBackupRequest request, ServerBackups backups,
        CancellationToken ct)
    {
        var result = await backups.CreateAsync(new ServerId(id), request.Name, OperationRequester.User, ct);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/servers/{id}/backups/{result.Value.Id}", result.Value)
            : ApiProblems.FromDomainError(result.Error!);
    }
}
