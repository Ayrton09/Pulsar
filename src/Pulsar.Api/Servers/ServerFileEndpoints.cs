using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Servers;

public sealed record FilePathRequest([property: Required, StringLength(ServerFilePath.MaxLength, MinimumLength = 1)] string Path);

public sealed record MoveFileRequest(
    [property: Required, StringLength(ServerFilePath.MaxLength, MinimumLength = 1)] string Path,
    [property: Required, StringLength(ServerFilePath.MaxLength, MinimumLength = 1)] string Destination);

/// <summary>
/// The file manager (docs/architecture/07, "Files"). Paths are relative to the server directory, with forward slashes.
/// Content is raw bytes both ways: <c>GET …/content</c> opens a file for the editor (up to 1 MiB, with its version
/// as the ETag), <c>GET …/download</c> downloads any file, <c>PUT …/content</c> writes one (editor saves and uploads):
/// <c>If-Match</c> with the version it was opened with, <c>If-None-Match: *</c> to create it only if it does not
/// exist, neither to replace whatever is there.
/// </summary>
public static class ServerFileEndpoints
{
    private const string Binary = "application/octet-stream";

    public static RouteGroupBuilder MapServerFileEndpoints(this RouteGroupBuilder api)
    {
        var files = api.MapGroup("/servers/{id:guid}/files").WithTags("Files");
        files.MapGet("/", async (Guid id, string? path, ServerFiles f, CancellationToken ct) => (await f.ListAsync(new ServerId(id), path, ct)).ToHttp())
            .RequirePermission(Permissions.Server.FilesView);
        files.MapGet("/content", (Guid id, [Required] string path, ServerFiles f, HttpContext http) =>
                SendAsync(http, f.OpenAsync(new ServerId(id), path, FileLimits.MaxEditableBytes, http.RequestAborted), attachment: false))
            .RequirePermission(Permissions.Server.FilesView)
            .Produces(StatusCodes.Status200OK, contentType: Binary)
            .ProducesProblem(StatusCodes.Status404NotFound);
        files.MapGet("/download", (Guid id, [Required] string path, ServerFiles f, HttpContext http) =>
                SendAsync(http, f.OpenAsync(new ServerId(id), path, maxSize: null, http.RequestAborted), attachment: true))
            .RequirePermission(Permissions.Server.FilesView)
            .Produces(StatusCodes.Status200OK, contentType: Binary)
            .ProducesProblem(StatusCodes.Status404NotFound);
        // Any content type: the body is the file's bytes as they are (Accepts metadata would also filter requests by it).
        files.MapPut("/content", WriteAsync)
            .RequirePermission(Permissions.Server.FilesEdit);
        files.MapPost("/directories", async (Guid id, FilePathRequest request, ServerFiles f, CancellationToken ct) =>
                (await f.CreateDirectoryAsync(new ServerId(id), request.Path, ct)).ToHttp())
            .RequirePermission(Permissions.Server.FilesEdit);
        files.MapPost("/move", async (Guid id, MoveFileRequest request, ServerFiles f, CancellationToken ct) =>
                (await f.MoveAsync(new ServerId(id), request.Path, request.Destination, ct)).ToHttp())
            .RequirePermission(Permissions.Server.FilesEdit);
        files.MapDelete("/", async (Guid id, [Required] string path, ServerFiles f, CancellationToken ct) =>
                (await f.DeleteAsync(new ServerId(id), path, ct)).ToHttp())
            .RequirePermission(Permissions.Server.FilesEdit);
        return api;
    }

    /// <summary>Streams a file (or a backup) from the node to the browser, never holding it on the panel.</summary>
    internal static async Task<IResult> SendAsync(HttpContext http, Task<Result<FileContent>> opening, bool attachment, string contentType = Binary)
    {
        var opened = await opening;
        if (!opened.IsSuccess)
        {
            return ApiProblems.FromDomainError(opened.Error!);
        }

        using var content = opened.Value;
        var response = http.Response;
        response.ContentType = contentType;
        response.ContentLength = content.Length;
        response.Headers.CacheControl = "no-store";
        response.Headers.ETag = Quote(content.File.Version);
        if (attachment)
        {
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(DownloadFileName(content.File.Entry.Name));
            response.Headers.ContentDisposition = disposition.ToString();
        }

        try
        {
            // A transfer that was ended (its user's sessions, its API key, its time) breaks off like a lost connection.
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, content.Aborted);
            await content.Content.CopyToAsync(response.Body, sending.Token);
            content.Complete();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException)
        {
            // The node or the browser went away halfway: the browser must see a broken download, not a short file.
            http.Abort();
        }

        return Results.Empty;
    }

    private static async Task<Results<Ok<FileDetails>, ProblemHttpResult>> WriteAsync(Guid id, [Required] string path, ServerFiles files, HttpContext http)
    {
        var request = http.Request;
        string? expectedVersion = null;
        if (request.Headers.IfMatch.Count > 0)
        {
            if (!EntityTagHeaderValue.TryParse(request.Headers.IfMatch.ToString(), out var tag) || tag.IsWeak)
            {
                return ApiProblems.Create(StatusCodes.Status400BadRequest, "files.invalid_version", "If-Match must be the strong ETag the file was opened with.");
            }

            expectedVersion = tag.Tag.Value?.Trim('"');
        }

        var createOnly = request.Headers.IfNoneMatch.ToString() == "*";
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = files.MaxUploadBytes;
        }

        var result = await files.WriteAsync(new ServerId(id), path, request.Body, request.ContentLength, overwrite: !createOnly, expectedVersion,
            http.RequestAborted);
        if (!result.IsSuccess)
        {
            return ApiProblems.FromDomainError(result.Error!);
        }

        http.Response.Headers.ETag = Quote(result.Value.Version);
        return TypedResults.Ok(result.Value);
    }

    private static string Quote(string version) => $"\"{version}\"";

    /// <summary>
    /// <paramref name="name"/> for a browser's download dialog, which cannot show control, format (a right-to-left
    /// override, a zero-width space) or separator characters: each becomes an underscore, so "update&lt;RLO&gt;fdp.exe" cannot pass for another file.
    /// </summary>
    internal static string DownloadFileName(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            var hidden = System.Text.Rune.IsControl(rune)
                || System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;
            text.Append(hidden ? "_" : rune.ToString());
        }

        return text.ToString();
    }
}
