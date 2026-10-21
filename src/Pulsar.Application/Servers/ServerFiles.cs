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

/// <summary>A page of a folder as SFTP lists it (<see cref="ServerFiles.ListPageAsync"/>), by <see cref="FileNameOrder"/>.</summary>
/// <param name="Path">The listed directory (empty: the server directory).</param>
/// <param name="Next">The name the next page starts after; null: this is the last page.</param>
/// <param name="Truncated">The folder holds more than the node reads of one folder: some entries are in no page.</param>
/// <param name="Skipped">
/// Entries left out because their names cannot be used or SFTP hides them (format characters), counted on the first page.
/// </param>
public sealed record FileListPage(string Path, IReadOnlyList<FileEntry> Entries, string? Next, bool Truncated, int Skipped);

/// <summary>What a streamed write (<see cref="ServerFiles.WriteStreamAsync"/>) does with an existing file.</summary>
public enum FileWriteMode
{
    /// <summary>The file must not exist yet (<c>files.exists</c>); it appears complete or not at all.</summary>
    CreateNew,

    /// <summary>An existing file is replaced in one step once all of the content arrived; a new one appears complete or not at all.</summary>
    Replace,

    /// <summary>
    /// The content is added at the end of the file (created when missing), in place as it arrives: an interrupted write
    /// leaves what arrived (resumed uploads).
    /// </summary>
    Append,
}

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

    /// <summary>Cancelled when the transfer was ended before it was done (<see cref="FileTransfers.Transfer.Aborted"/>).</summary>
    public CancellationToken Aborted => download.Aborted;

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
    public Task<Result<FileContent>> OpenAsync(ServerId id, string path, long? maxSize, CancellationToken cancellationToken) =>
        OpenAsync(id, path, maxSize, offset: null, cancellationToken);

    /// <summary>
    /// SFTP: starts a file's way from the node at <paramref name="offset"/> (resumed downloads, a client reading in parallel
    /// or seeking back). <see cref="FileContent.Length"/> is what is left from there, nothing at or past the end;
    /// <see cref="FileContent.File"/> still describes the whole file. Audited as a download, with its offset.
    /// </summary>
    public Task<Result<FileContent>> OpenAtAsync(ServerId id, string path, long offset, CancellationToken cancellationToken) =>
        offset < 0
            ? Task.FromResult<Result<FileContent>>(ServerErrors.FileOffsetInvalid)
            : OpenAsync(id, path, maxSize: null, offset, cancellationToken);

    /// <param name="offset">Null: the web's download (from the start, on any agent); otherwise SFTP's.</param>
    private async Task<Result<FileContent>> OpenAsync(ServerId id, string path, long? maxSize, long? offset, CancellationToken cancellationToken)
    {
        if (ServerFilePath.Error(path) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        var sftp = offset is not null;
        if (sftp && SftpView.IsHidden(path))
        {
            return Hidden(path);
        }

        var target = await TargetAsync(id, cancellationToken, sftp);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        if (transfers.BeginDownload(target.Value.NodeId, request.UserId, request.ApiKeyId) is not { } download)
        {
            return ServerErrors.FileTransfersBusy;
        }

        try
        {
            AgentCommand command = sftp
                ? new SendFileCommand(id.Value, path, download.Ticket, long.MaxValue, offset!.Value)
                : new SendFileCommand(id.Value, path, download.Ticket, maxSize ?? long.MaxValue);
            var ack = await SendAsync(target.Value, command);
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

            // What is left of the file from the offset, as the node described it: never more.
            if (offset is { } start && length > file.Entry.Size - Math.Min(start, file.Entry.Size))
            {
                download.Dispose();
                return ServerErrors.FileTransferFailed;
            }

            if (maxSize is null)
            {
                object details = sftp ? new { path, size = length, offset } : new { path, size = length };
                audit.Record(AuditActions.Server.FileDownloaded, AuditOutcome.Success, details)
                    .WithServer(target.Value.Id, target.Value.NodeId);
                await db.SaveChangesAsync(cancellationToken);
            }

            // Named by what was asked for: the name in the node's answer is the node's to choose (it becomes the download's name).
            return new FileContent(new FileDetails(file.Entry with { Name = ServerFilePath.Name(path) }, file.Version), content, length, download);
        }
        catch (Exception ex) when (ex is TimeoutException
            || (ex is OperationCanceledException && download.Aborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested))
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

        // What the editor saves (a config to fix, on a nearly full drive too) is not held back.
        if (length > FileLimits.MaxEditableBytes && connections.Find(target.Value.NodeId)?.BelowFloor(DiskUses.Servers, length) is not null)
        {
            return NodeErrors.DiskLow;
        }

        // Refused before the node is told: it would hold one more of its connections to the panel for it.
        if (transfers.BeginUpload(target.Value.NodeId, request.UserId, content, length, request.ApiKeyId) is not { } started)
        {
            return ServerErrors.FileTransfersBusy;
        }

        return await UploadAsync(target.Value, started, ticket => new ReceiveFileCommand(id.Value, path, ticket, length, overwrite, expectedVersion),
            (result, _) => new { path, size = length, replace = overwrite || expectedVersion is not null, errorCode = result.Error?.Code },
            cancellationToken);
    }

    /// <summary>
    /// SFTP: writes what <paramref name="content"/> holds until it ends, at most <see cref="FileManagerOptions.MaxUploadBytes"/>
    /// (more fails with <c>files.too_large</c>), to the file, as <paramref name="mode"/> says. The caller ends the content
    /// to commit it; content that fails or is cancelled is never taken for a shorter file (but see
    /// <see cref="FileWriteMode.Append"/>). Audited as a write, with the bytes that went to the node.
    /// </summary>
    public async Task<Result<FileDetails>> WriteStreamAsync(ServerId id, string path, Stream content, FileWriteMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (ServerFilePath.Error(path) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (SftpView.IsHidden(path))
        {
            return Hidden(path);
        }

        var target = await TargetAsync(id, cancellationToken, sftp: true);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        // The size is unknown until the content ends: refused on a drive already below the floor.
        if (connections.Find(target.Value.NodeId)?.BelowFloor(DiskUses.Servers) is not null)
        {
            return NodeErrors.DiskLow;
        }

        var limit = options.MaxUploadBytes;
        if (transfers.BeginStreamedUpload(target.Value.NodeId, request.UserId, content, limit, request.ApiKeyId) is not { } started)
        {
            return ServerErrors.FileTransfersBusy;
        }

        return await UploadAsync(target.Value, started,
            ticket => new ReceiveFileCommand(id.Value, path, ticket, limit, mode == FileWriteMode.Replace, null, Streamed: true,
                Append: mode == FileWriteMode.Append),
            (result, sent) => new
            {
                path,
                size = sent,
                replace = mode == FileWriteMode.Replace,
                append = mode == FileWriteMode.Append,
                errorCode = result.Error?.Code,
            },
            cancellationToken);
    }

    /// <summary>
    /// SFTP: one page of a folder's entries, those after <paramref name="after"/> (null: the first page) by
    /// <see cref="FileNameOrder"/>, up to <see cref="FileLimits.MaxListEntries"/>. Unlike <see cref="ListAsync"/>, every
    /// entry can be reached by asking for the pages that follow (<see cref="FileListPage.Next"/>); what SFTP hides
    /// (<see cref="SftpView"/>) is left out, and a hidden folder is not found.
    /// </summary>
    public async Task<Result<FileListPage>> ListPageAsync(ServerId id, string? path, string? after, CancellationToken cancellationToken)
    {
        path ??= ServerFilePath.Root;
        if (ServerFilePath.Error(path, allowRoot: true) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        if (after is not null && !AgentInput.IsFileName(after))
        {
            return ServerErrors.FilePathInvalid("A page starts after one name.");
        }

        if (SftpView.IsHidden(path))
        {
            return Hidden(path);
        }

        var target = await TargetAsync(id, cancellationToken, sftp: true);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        var ack = await SendAsync(target.Value, new ListFilesCommand(id.Value, path, Paged: true, After: after));
        if (!ack.IsSuccess)
        {
            return ack.Error!;
        }

        return ack.Value.Result is FileListResult listing ? Page(path, after, listing) : ServerErrors.CommandRejected;
    }

    /// <summary>
    /// A page as the panel passes it on, whatever the node answered (<see cref="AgentInput"/> already left out what the panel
    /// cannot name): only entries after <paramref name="after"/> and up to the next page's start, each name once, nothing
    /// SFTP hides, in order; a next page that would not move on ends the listing (marked truncated) rather than repeat it.
    /// </summary>
    private static FileListPage Page(string path, string? after, FileListResult listing)
    {
        var next = listing.Next;
        var truncated = listing.Truncated;
        if (next is not null && after is not null && FileNameOrder.Compare(next, after) <= 0)
        {
            next = null;
            truncated = true;
        }

        var skipped = (long)listing.Skipped;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<FileEntry>();
        foreach (var entry in listing.Entries)
        {
            if ((after is not null && FileNameOrder.Compare(entry.Name, after) <= 0) || (next is not null && FileNameOrder.Compare(entry.Name, next) > 0)
                || !names.Add(entry.Name))
            {
                continue;
            }

            if (SftpView.IsHiddenName(entry.Name))
            {
                skipped++;
            }
            else if (!SftpView.IsHidden(ServerFilePath.Join(path, entry.Name)))
            {
                entries.Add(entry);
            }
        }

        entries.Sort((a, b) => FileNameOrder.Compare(a.Name, b.Name));
        return new FileListPage(path, entries, next, truncated, (int)Math.Min(AgentInput.MaxSkipped, skipped));
    }

    /// <summary>SFTP: what one entry is (a link is described, not followed); a hidden one is not found.</summary>
    public async Task<Result<FileDetails>> StatAsync(ServerId id, string path, CancellationToken cancellationToken)
    {
        if (ServerFilePath.Error(path) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        if (SftpView.IsHidden(path))
        {
            return Hidden(path);
        }

        var target = await TargetAsync(id, cancellationToken, sftp: true);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        return Described(path, await SendAsync(target.Value, new StatFileCommand(id.Value, path)));
    }

    /// <summary>
    /// SFTP: renames or moves an entry, replacing an existing file at <paramref name="destination"/> in one step when
    /// <paramref name="replace"/> (never a directory or link, and only by a file). Audited as a move.
    /// </summary>
    public Task<Result> MoveAsync(ServerId id, string path, string destination, bool replace, CancellationToken cancellationToken)
    {
        if (ServerFilePath.IsValid(path) && ServerFilePath.IsValid(destination) && ServerFilePath.IsInside(destination, path))
        {
            return Task.FromResult<Result>(ServerErrors.FilePathInvalid("A folder cannot be moved inside itself."));
        }

        if (new[] { path, destination }.FirstOrDefault(p => ServerFilePath.IsValid(p) && SftpView.IsHidden(p)) is { } hidden)
        {
            return Task.FromResult<Result>(Hidden(hidden));
        }

        return ChangeAsync(id, [path, destination], new MoveFileCommand(id.Value, path, destination, replace), AuditActions.Server.FileMoved,
            errorCode => new { path, destination, replace, errorCode }, cancellationToken, sftp: true);
    }

    /// <summary>
    /// SFTP: sets the last write time of a file or directory (clients keep a file's time after an upload). Not audited:
    /// it changes no content, and the write it follows is.
    /// </summary>
    public async Task<Result<FileDetails>> SetModifiedAsync(ServerId id, string path, DateTimeOffset modifiedAt, CancellationToken cancellationToken)
    {
        if (ServerFilePath.Error(path) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        if (SftpView.IsHidden(path))
        {
            return Hidden(path);
        }

        var target = await TargetAsync(id, cancellationToken, sftp: true);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        return Described(path, await SendAsync(target.Value, new SetFileModifiedCommand(id.Value, path, modifiedAt)));
    }

    /// <summary>What SFTP hides is not there, whatever is.</summary>
    private static DomainError Hidden(string path) => ServerErrors.FileRejected(FileErrors.NotFound, $"'{path}' does not exist.");

    /// <summary>The entry an ack describes, named by what was asked for (the node's name for it is the node's to choose).</summary>
    private static Result<FileDetails> Described(string path, Result<CommandAck> ack)
    {
        if (!ack.IsSuccess)
        {
            return ack.Error!;
        }

        return ack.Value.Result is FileEntryResult file
            ? new FileDetails(file.Entry with { Name = ServerFilePath.Name(path) }, file.Version)
            : ServerErrors.CommandRejected;
    }

    /// <summary>Runs an upload the node is told of with <paramref name="command"/>, then audits it whatever happened.</summary>
    /// <param name="details">The audit metadata, given the result and the bytes that went to the node.</param>
    private async Task<Result<FileDetails>> UploadAsync(Server server, FileTransfers.Upload started, Func<string, ReceiveFileCommand> command,
        Func<Result<FileDetails>, long, object> details, CancellationToken cancellationToken)
    {
        Result<FileDetails> result;
        long sent;
        var abandoned = false;
        using (var upload = started)
        {
            try
            {
                result = await ReceiveAsync(server, upload, command(upload.Ticket), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The browser went away after the node was told: the node may have written the file, so this is audited.
                result = ServerErrors.FileTransferFailed;
                abandoned = true;
            }

            sent = upload.BytesSent;
        }

        audit.Record(AuditActions.Server.FileWritten, result.IsSuccess ? AuditOutcome.Success : AuditOutcome.Failure, details(result, sent))
            .WithServer(server.Id, server.NodeId);
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
        catch (Exception ex) when (ex is TimeoutException
            || (ex is OperationCanceledException && upload.Aborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested))
        {
            return ServerErrors.FileTransferFailed;
        }
    }

    /// <param name="details">The audit metadata, given the error code (null on success).</param>
    /// <param name="sftp">The command uses what <see cref="AgentCapabilities.SftpFiles"/> adds.</param>
    private async Task<Result> ChangeAsync(ServerId id, string[] paths, AgentCommand command, string action, Func<string?, object> details,
        CancellationToken cancellationToken, bool sftp = false)
    {
        if (paths.Select(p => ServerFilePath.Error(p)).OfType<string>().FirstOrDefault() is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        var target = await TargetAsync(id, cancellationToken, sftp);
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

    /// <param name="sftp">
    /// What SFTP needs: refused for an agent that does not announce <see cref="AgentCapabilities.SftpFiles"/>, which would
    /// ignore the new fields of the file commands and do something else than asked.
    /// </param>
    private async Task<Result<Server>> TargetAsync(ServerId id, CancellationToken cancellationToken, bool sftp = false)
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

        if (!live.Capabilities.Contains(AgentCapabilities.ListFiles, StringComparer.Ordinal))
        {
            return ServerErrors.FilesUnsupported;
        }

        return !sftp || live.Capabilities.Contains(AgentCapabilities.SftpFiles, StringComparer.Ordinal) ? server : ServerErrors.FilesSftpUnsupported;
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
