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

/// <param name="StorageId">Where new backups are copied besides the node; null: on the node only.</param>
public sealed record BackupStorageChoiceRequest(Guid? StorageId);

/// <param name="Endpoint">The service's address (https://…); null or empty for AWS S3.</param>
/// <param name="SecretAccessKey">Required to create; leave it out on an update to keep the current one.</param>
/// <param name="PathStyle">The bucket in the path (most S3-compatible services) rather than in the host name.</param>
public sealed record BackupStorageRequest(
    [property: Required, StringLength(BackupStorage.MaxNameLength)] string Name,
    [property: StringLength(BackupStorage.MaxEndpointLength)] string? Endpoint,
    [property: Required, StringLength(BackupStorage.MaxRegionLength)] string Region,
    [property: Required, StringLength(63)] string Bucket,
    [property: StringLength(BackupStorage.MaxPrefixLength)] string? Prefix,
    [property: Required, StringLength(BackupStorage.MaxAccessKeyLength)] string AccessKeyId,
    [property: StringLength(256)] string? SecretAccessKey,
    bool PathStyle)
{
    internal BackupStorageDefinition ToDefinition() => new(Name, Endpoint, Region, Bucket, Prefix, AccessKeyId, SecretAccessKey, PathStyle);
}

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
        // ?clean=true first deletes the files the backup's rules select that it does not have (made after it); without it
        // they stay, as for restores asked before clean ones existed (scripts with API keys).
        backups.MapPost("/{backupId:guid}/restore", async (Guid id, Guid backupId, ServerBackups b, CancellationToken ct, bool clean = false) =>
                (await b.RestoreAsync(new ServerId(id), new BackupId(backupId), clean, ct)).ToHttp())
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
        // Turning remote copies off is giving up on them: the same right as the retention limit.
        backups.MapPut("/storage", async (Guid id, BackupStorageChoiceRequest request, ServerBackups b, CancellationToken ct) =>
                (await b.SetStorageAsync(new ServerId(id), request.StorageId, ct)).ToHttp())
            .RequirePermission(Permissions.Server.BackupsDelete);

        // The storages themselves: panel settings (their secret keys are the panel's).
        var storages = api.MapGroup("/backup-storages").WithTags("Backups").RequirePermission(Permissions.Settings.Manage);
        storages.MapGet("/", async (BackupStorages s, CancellationToken ct) => TypedResults.Ok(await s.ListAsync(ct)));
        storages.MapPost("/", CreateStorageAsync);
        storages.MapPut("/{storageId:guid}", async (Guid storageId, BackupStorageRequest request, BackupStorages s, CancellationToken ct) =>
            (await s.UpdateAsync(storageId, request.ToDefinition(), ct)).ToHttp());
        storages.MapDelete("/{storageId:guid}", async (Guid storageId, BackupStorages s, CancellationToken ct) => (await s.DeleteAsync(storageId, ct)).ToHttp());
        return api;
    }

    private static async Task<Results<Created<BackupStorageView>, ProblemHttpResult>> CreateStorageAsync(BackupStorageRequest request, BackupStorages storages,
        CancellationToken ct)
    {
        var result = await storages.CreateAsync(request.ToDefinition(), ct);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/backup-storages/{result.Value.Id}", result.Value)
            : ApiProblems.FromDomainError(result.Error!);
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
