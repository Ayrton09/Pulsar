using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

public sealed class FileManagerOptions
{
    /// <summary>Largest file a browser may upload (the editor's limit is <see cref="FileLimits.MaxEditableBytes"/>).</summary>
    public long MaxUploadBytes { get; set; } = 1L << 30;
}

/// <param name="Path">The listed directory (empty: the server directory).</param>
/// <param name="Truncated">The directory has more entries than a listing returns.</param>
/// <param name="Skipped">Entries left out because their names cannot be opened, renamed or deleted from the panel.</param>
public sealed record FileListing(string Path, IReadOnlyList<FileEntry> Entries, bool Truncated, int Skipped);

/// <param name="Version">For the editor: saving with it fails if the file changed in between.</param>
public sealed record FileDetails(FileEntry Entry, string Version);

/// <summary>
/// A file's content on its way from the node: copy <see cref="Content"/> (exactly <see cref="Length"/> bytes) to the
/// browser, then <see cref="Complete"/>. Disposing it ends the transfer either way.
/// </summary>
public sealed class FileContent(FileDetails file, Stream content, long length, FileTransfers.Download download) : IDisposable
{
    public FileDetails File { get; } = file;

    public Stream Content { get; } = content;

    public long Length { get; } = length;

    /// <summary>All of it reached the browser.</summary>
    public void Complete() => download.Finish(complete: true);

    public void Dispose() => download.Dispose();
}

/// <summary>
/// The file manager as the browser sees it (docs/architecture/01, "Files"): the paths are checked here, the server's
/// node does the work (docs/architecture/08, "File manager"), and every change and download is audited. Content never
/// goes through the hub: it is piped between the browser's request and the agent's (<see cref="FileTransfers"/>).
/// </summary>
public sealed class ServerFiles(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    FileTransfers transfers,
    FileManagerOptions options,
    IAuditLog audit,
    IRequestContext request)
{
    /// <summary>How long the agent's report on a file it received may take once all of it was sent.</summary>
    private static readonly TimeSpan s_resultTimeout = TimeSpan.FromMinutes(2);

    public long MaxUploadBytes => options.MaxUploadBytes;

    public async Task<Result<FileListing>> ListAsync(ServerId id, string? path, CancellationToken cancellationToken)
    {
        path ??= ServerFilePath.Root;
        if (ServerFilePath.Error(path, allowRoot: true) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        var target = await TargetAsync(id, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        var ack = await SendAsync(target.Value, new ListFilesCommand(id.Value, path));
        if (!ack.IsSuccess)
        {
            return ack.Error!;
        }

        return ack.Value.Result is FileListResult listing
            ? new FileListing(path, listing.Entries, listing.Truncated, listing.Skipped)
            : ServerErrors.CommandRejected;
    }

    public Task<Result> CreateDirectoryAsync(ServerId id, string path, CancellationToken cancellationToken) =>
        ChangeAsync(id, [path], new CreateDirectoryCommand(id.Value, path), AuditActions.Server.DirectoryCreated, errorCode => new { path, errorCode },
            cancellationToken);

    public Task<Result> MoveAsync(ServerId id, string path, string destination, CancellationToken cancellationToken)
    {
        if (ServerFilePath.IsValid(path) && ServerFilePath.IsValid(destination) && ServerFilePath.IsInside(destination, path))
        {
            return Task.FromResult<Result>(ServerErrors.FilePathInvalid("A folder cannot be moved inside itself."));
        }

        return ChangeAsync(id, [path, destination], new MoveFileCommand(id.Value, path, destination), AuditActions.Server.FileMoved,
            errorCode => new { path, destination, errorCode }, cancellationToken);
    }

    public Task<Result> DeleteAsync(ServerId id, string path, CancellationToken cancellationToken) =>
        ChangeAsync(id, [path], new DeleteFileCommand(id.Value, path), AuditActions.Server.FileDeleted, errorCode => new { path, errorCode },
            cancellationToken);

    /// <summary>
    /// Starts a file's way from the node. <paramref name="maxSize"/> limits what the editor opens; downloads have no
    /// limit and are audited.
    /// </summary>
    public async Task<Result<FileContent>> OpenAsync(ServerId id, string path, long? maxSize, CancellationToken cancellationToken)
    {
        if (ServerFilePath.Error(path) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        var target = await TargetAsync(id, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        var download = transfers.BeginDownload(target.Value.NodeId);
        try
        {
            var ack = await SendAsync(target.Value, new SendFileCommand(id.Value, path, download.Ticket, maxSize ?? long.MaxValue));
            if (!ack.IsSuccess || ack.Value.Result is not FileEntryResult file)
            {
                download.Dispose();
                return ack.IsSuccess ? ServerErrors.CommandRejected : ack.Error!;
            }

            await download.WaitForClaimAsync(cancellationToken);
            var (content, length) = await download.WaitForContentAsync(cancellationToken);
            if (length > maxSize)
            {
                // The node was told the limit; the panel does not rely on it.
                download.Dispose();
                return ServerErrors.FileTooLarge;
            }

            if (maxSize is null)
            {
                audit.Record(AuditActions.Server.FileDownloaded, AuditOutcome.Success, new { path, size = length })
                    .WithServer(target.Value.Id, target.Value.NodeId);
                await db.SaveChangesAsync(cancellationToken);
            }

            // Named by what was asked for: the name in the node's answer is the node's to choose (it becomes the download's name).
            return new FileContent(new FileDetails(file.Entry with { Name = ServerFilePath.Name(path) }, file.Version), content, length, download);
        }
        catch (TimeoutException)
        {
            download.Dispose();
            return ServerErrors.FileTransferFailed;
        }
        catch
        {
            download.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes <paramref name="size"/> bytes from <paramref name="content"/> (the browser's request body) to the file,
    /// which appears complete or not at all. <paramref name="expectedVersion"/> (an editor saving what it opened) fails
    /// with <c>files.changed</c> when the file changed in between; without it, <paramref name="overwrite"/> says whether
    /// an existing file may be replaced.
    /// </summary>
    public async Task<Result<FileDetails>> WriteAsync(ServerId id, string path, Stream content, long? size, bool overwrite, string? expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (ServerFilePath.Error(path) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        if (size is not { } length || length < 0)
        {
            return ServerErrors.FileLengthRequired;
        }

        if (length > options.MaxUploadBytes)
        {
            return ServerErrors.FileTooLarge;
        }

        var target = await TargetAsync(id, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        Result<FileDetails> result;
        var abandoned = false;
        using (var upload = transfers.BeginUpload(target.Value.NodeId, content, length))
        {
            try
            {
                result = await ReceiveAsync(target.Value, upload, new ReceiveFileCommand(id.Value, path, upload.Ticket, length, overwrite, expectedVersion),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The browser went away after the node was told: the node may have written the file, so this is audited.
                result = ServerErrors.FileTransferFailed;
                abandoned = true;
            }
        }

        audit.Record(AuditActions.Server.FileWritten, result.IsSuccess ? AuditOutcome.Success : AuditOutcome.Failure,
                new { path, size = length, replace = overwrite || expectedVersion is not null, errorCode = result.Error?.Code })
            .WithServer(target.Value.Id, target.Value.NodeId);
        await db.SaveChangesAsync(CancellationToken.None);
        if (abandoned)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return result;
    }

    private async Task<Result<FileDetails>> ReceiveAsync(Server server, FileTransfers.Upload upload, ReceiveFileCommand command,
        CancellationToken cancellationToken)
    {
        var ack = await SendAsync(server, command);
        if (!ack.IsSuccess)
        {
            return ack.Error!;
        }

        try
        {
            await upload.WaitForClaimAsync(cancellationToken);

            // The agent reports once it has all of the content (or as soon as it fails to get it).
            await upload.WaitUntilSentAsync(cancellationToken);
            var outcome = await upload.Result.WaitAsync(s_resultTimeout, cancellationToken);
            return outcome is { ErrorCode: null, File: { } file }
                ? new FileDetails(file.Entry with { Name = ServerFilePath.Name(command.Path) }, file.Version)
                : ServerErrors.FileRejected(outcome.ErrorCode ?? FileErrors.Failed, outcome.Message ?? "The node could not write the file.");
        }
        catch (TimeoutException)
        {
            return ServerErrors.FileTransferFailed;
        }
    }

    /// <param name="details">The audit metadata, given the error code (null on success).</param>
    private async Task<Result> ChangeAsync(ServerId id, string[] paths, AgentCommand command, string action, Func<string?, object> details,
        CancellationToken cancellationToken)
    {
        if (paths.Select(p => ServerFilePath.Error(p)).OfType<string>().FirstOrDefault() is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        var target = await TargetAsync(id, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        var ack = await SendAsync(target.Value, command);
        audit.Record(action, ack.IsSuccess ? AuditOutcome.Success : AuditOutcome.Failure, details(ack.Error?.Code))
            .WithServer(target.Value.Id, target.Value.NodeId);
        await db.SaveChangesAsync(CancellationToken.None);
        return ack.IsSuccess ? Result.Success() : ack.Error!;
    }

    private async Task<Result<Server>> TargetAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (connections.Find(server.NodeId) is not { } live || !sessions.IsActive(live.SessionId))
        {
            return NodeErrors.NotConnected;
        }

        return live.Capabilities.Contains(AgentCapabilities.ListFiles, StringComparer.Ordinal) ? server : ServerErrors.FilesUnsupported;
    }

    private async Task<Result<CommandAck>> SendAsync(Server server, AgentCommand command)
    {
        // Once sent, the node's answer is waited for (it has its own timeout) even if the request goes away: what the
        // node did must still be audited.
        var ack = await connections.SendAsync(server.NodeId, command, request.UserId, CancellationToken.None);
        return ack switch
        {
            null => NodeErrors.NotConnected,
            { Status: AckStatus.Accepted } => ack,
            { Status: AckStatus.Busy } => ServerErrors.Busy,
            { Status: AckStatus.Rejected, ErrorCode: { } code } when code.StartsWith("files.", StringComparison.Ordinal) =>
                ServerErrors.FileRejected(code, ack.Message ?? code),
            _ => ServerErrors.CommandRejected,
        };
    }
}
