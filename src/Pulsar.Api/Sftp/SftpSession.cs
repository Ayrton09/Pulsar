using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipelines;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Sftp;

/// <summary>
/// One SFTP version 3 session over an SSH channel (docs/guides/sftp.md). Requests are handled one at a time in the order
/// they arrive, so what waits is bounded by the channel's window (the SSH library stops taking data the session has not
/// read); packets are at most <see cref="SftpOptions.MaxPacketBytes"/>, open files and folders at most
/// <see cref="SftpOptions.MaxHandlesPerSession"/>. Every request runs through <see cref="SftpFileSystem"/> (its own scope,
/// the permissions checked again) and becomes what the web file manager does: listings by pages, a stat, a download
/// from an offset, a streamed upload, a move, a folder made or deleted.
/// </summary>
internal sealed partial class SftpSession(
    Stream stream, SftpConnection connection, SftpFileSystem files, SftpOptions options, TimeProvider clock, ILogger logger) : IAsyncDisposable
{
    /// <summary>Writes ahead of a gap a session keeps at once, whatever their size.</summary>
    internal const int MaxReorderEntries = 256;

    /// <summary>What each write kept ahead of a gap counts for <see cref="SftpOptions.MaxReorderBytes"/> besides its bytes.</summary>
    internal const int ReorderEntryOverhead = 96;

    /// <summary>
    /// Entries one listing (one OPENDIR) returns at most: each page reads the folder again on the node, so paging through
    /// a huge folder costs CPU there for every page.
    /// </summary>
    internal const int MaxListingEntries = 100_000;

    private const int MaxNamesPerReply = 100;

    private readonly Dictionary<string, Handle> _handles = new(StringComparer.Ordinal);
    private uint _nextHandle;
    private bool _initialized;
    private long _reorderBytes;
    private int _reorderEntries;

    /// <summary>Serves requests until the client closes the channel, the connection ends or the client breaks the protocol.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await FillAsync(header, cancellationToken))
            {
                return;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length < 1 || length > options.MaxPacketBytes)
            {
                LogBadPacket(logger, connection.CorrelationId, length);
                return;
            }

            var packet = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                var body = packet.AsMemory(0, (int)length);
                if (!await FillAsync(body, cancellationToken))
                {
                    return;
                }

                connection.Touch(clock.GetTimestamp());
                using var reply = await HandleAsync(body, cancellationToken);
                if (reply is null)
                {
                    return;
                }

                await stream.WriteAsync(reply.Packet, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_handles.Values.Select(CloseQuietlyAsync));
        _handles.Clear();
    }

    private async Task<bool> FillAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    /// <summary>The reply to one packet, or null to end the session (a client that does not speak SFTP version 3).</summary>
    private async Task<SftpWriter?> HandleAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        Request request;
        try
        {
            request = Parse(packet.Span, out var dataStart, out var dataLength);
            request.Data = packet.Slice(dataStart, dataLength);
        }
        catch (SftpBadMessageException ex)
        {
            if (packet.Length >= 5 && packet.Span[0] != SftpProtocol.Init)
            {
                return Status(BinaryPrimitives.ReadUInt32BigEndian(packet.Span[1..]), SftpProtocol.BadMessage, ex.Message);
            }

            return null;
        }

        if (request.Type == SftpProtocol.Init)
        {
            if (_initialized)
            {
                return null;
            }

            _initialized = true;
            return new SftpWriter(SftpProtocol.VersionReply)
                .UInt32(SftpProtocol.Version)
                .String(SftpProtocol.PosixRename).String("1")
                .String(SftpProtocol.Limits).String("1");
        }

        if (!_initialized)
        {
            return null;
        }

        try
        {
            return request.Type switch
            {
                SftpProtocol.RealPath => RealPath(request),
                SftpProtocol.Stat or SftpProtocol.LStat => await StatAsync(request, cancellationToken),
                SftpProtocol.FStat => await FStatAsync(request, cancellationToken),
                SftpProtocol.OpenDir => await OpenDirectoryAsync(request, cancellationToken),
                SftpProtocol.ReadDir => await ReadDirectoryAsync(request, cancellationToken),
                SftpProtocol.Open => await OpenAsync(request, cancellationToken),
                SftpProtocol.Read => await ReadAsync(request, cancellationToken),
                SftpProtocol.Write => await WriteAsync(request, cancellationToken),
                SftpProtocol.Close => await CloseAsync(request, cancellationToken),
                SftpProtocol.Remove => await RemoveAsync(request, directory: false, cancellationToken),
                SftpProtocol.RmDir => await RemoveAsync(request, directory: true, cancellationToken),
                SftpProtocol.MkDir => await MakeDirectoryAsync(request, cancellationToken),
                SftpProtocol.Rename => await RenameAsync(request, replace: false, cancellationToken),
                SftpProtocol.SetStat => await SetStatAsync(request.Id, request.Path!, request.Attributes, cancellationToken),
                SftpProtocol.FSetStat => await FSetStatAsync(request, cancellationToken),
                SftpProtocol.Extended when request.Extension == SftpProtocol.PosixRename => await RenameAsync(request, replace: true, cancellationToken),
                SftpProtocol.Extended when request.Extension == SftpProtocol.Limits => Limits(request.Id),
                _ => Status(request.Id, SftpProtocol.OpUnsupported, "Not supported."),
            };
        }
        catch (SftpException ex)
        {
            return Status(request.Id, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException) && !cancellationToken.IsCancellationRequested)
        {
            // The database or the node failed this request: the client is told, the session goes on.
            LogRequestFailed(logger, connection.CorrelationId, request.Type, ex);
            return Status(request.Id, SftpProtocol.Failure, "The panel could not do this. Try again.");
        }
    }

    // ---- requests ----

    private static SftpWriter RealPath(Request request)
    {
        var path = SftpFileSystem.Normalize(request.Path!);
        return new SftpWriter(SftpProtocol.Name).UInt32(request.Id).UInt32(1).String(path).String(path).UInt32(0);
    }

    private SftpWriter Limits(uint id) =>
        new SftpWriter(SftpProtocol.ExtendedReply).UInt32(id)
            .UInt64((ulong)options.MaxPacketBytes)
            .UInt64((ulong)options.MaxDataBytes)
            .UInt64((ulong)options.MaxDataBytes)
            .UInt64((ulong)options.MaxHandlesPerSession);

    private async Task<SftpWriter> StatAsync(Request request, CancellationToken cancellationToken)
    {
        await using var operation = await files.BeginAsync(cancellationToken);
        var location = await files.ResolveAsync(operation, request.Path!, write: false, cancellationToken);
        var (kind, size, modified) = await DescribeAsync(operation, location, cancellationToken);
        var reply = new SftpWriter(SftpProtocol.Attrs).UInt32(request.Id);
        SftpEntries.WriteAttributes(reply, kind, size, modified, location.CanEdit);
        return reply;
    }

    private async Task<(FileEntryKind Kind, long Size, DateTimeOffset Modified)> DescribeAsync(SftpOperation operation, SftpLocation location,
        CancellationToken cancellationToken)
    {
        if (location.Server is null || location.Path.Length == 0)
        {
            // The root and a server's own directory: folders that are always there.
            return (FileEntryKind.Directory, 0, location.Server?.CreatedAt ?? clock.GetUtcNow());
        }

        var stat = await operation.Files.StatAsync(location.Server.Id, location.Path, cancellationToken);
        if (!stat.IsSuccess)
        {
            throw SftpFileSystem.Map(stat.Error!);
        }

        return (stat.Value.Entry.Kind, stat.Value.Entry.Size, stat.Value.Entry.ModifiedAt);
    }

    private async Task<SftpWriter> FStatAsync(Request request, CancellationToken cancellationToken)
    {
        var handle = Find<Handle>(request.Handle!);
        await using var operation = await files.BeginAsync(cancellationToken);
        var location = await files.ResolveAsync(operation, handle.ClientPath, write: false, cancellationToken);
        (FileEntryKind Kind, long Size, DateTimeOffset Modified) entry;
        switch (handle)
        {
            case ReadHandle read:
                entry = (FileEntryKind.File, read.Size, read.Modified);
                break;
            case WriteHandle write when write.Upload is not null || !write.Exists:
                // Being written: what arrived so far.
                entry = (FileEntryKind.File, write.Mode == FileWriteMode.Append ? write.ExistingSize + write.Written : write.Written, clock.GetUtcNow());
                break;
            default:
                entry = await DescribeAsync(operation, location, cancellationToken);
                break;
        }

        var reply = new SftpWriter(SftpProtocol.Attrs).UInt32(request.Id);
        SftpEntries.WriteAttributes(reply, entry.Kind, entry.Size, entry.Modified, location.CanEdit);
        return reply;
    }

    private async Task<SftpWriter> OpenDirectoryAsync(Request request, CancellationToken cancellationToken)
    {
        EnsureHandleRoom();
        await using var operation = await files.BeginAsync(cancellationToken);
        var location = await files.ResolveAsync(operation, request.Path!, write: false, cancellationToken);
        if (location.Server is null)
        {
            var root = await SftpFileSystem.RootAsync(operation, cancellationToken);
            return HandleReply(request.Id, new DirectoryHandle(request.Path!, null, ServerFilePath.Root,
                [.. root.Select(r => new Listed(r.Server.Folder, FileEntryKind.Directory, 0, r.Server.CreatedAt, r.CanEdit))], next: null));
        }

        var page = await operation.Files.ListPageAsync(location.Server.Id, location.Path, null, cancellationToken);
        if (!page.IsSuccess)
        {
            throw SftpFileSystem.Map(page.Error!);
        }

        return HandleReply(request.Id, new DirectoryHandle(request.Path!, location.Server, location.Path,
            Listed.From(page.Value.Entries, location.CanEdit), page.Value.Next));
    }

    private async Task<SftpWriter> ReadDirectoryAsync(Request request, CancellationToken cancellationToken)
    {
        var handle = Find<DirectoryHandle>(request.Handle!);
        if (handle.Index >= handle.Entries.Count && handle.Next is not null && handle.Listed >= MaxListingEntries)
        {
            // Each page reads the folder again on the node: one listing of a huge folder stops here.
            LogListingCut(logger, connection.CorrelationId, handle.Listed);
            handle.Next = null;
        }

        if (handle.Index >= handle.Entries.Count && handle.Next is { } after && handle.Server is { } server)
        {
            await using var operation = await files.BeginAsync(cancellationToken);
            SftpFileSystem.Require(operation, server, write: false);
            var page = await operation.Files.ListPageAsync(server.Id, handle.Path, after, cancellationToken);
            if (!page.IsSuccess)
            {
                throw SftpFileSystem.Map(page.Error!);
            }

            handle.Entries = Listed.From(page.Value.Entries, operation.Permissions.Has(Permissions.Server.FilesEdit, server.Target));
            handle.Index = 0;
            handle.Next = page.Value.Next;
        }

        if (handle.Index >= handle.Entries.Count)
        {
            return Status(request.Id, SftpProtocol.Eof, "End of folder.");
        }

        var reply = new SftpWriter(SftpProtocol.Name, 8192).UInt32(request.Id).UInt32(0);
        var count = 0u;
        while (handle.Index < handle.Entries.Count && count < MaxNamesPerReply && reply.Length < options.MaxPacketBytes / 2)
        {
            var entry = handle.Entries[handle.Index++];
            reply.String(entry.Name).String(SftpEntries.LongName(entry.Name, entry.Kind, entry.Size, entry.Modified, entry.Writable));
            SftpEntries.WriteAttributes(reply, entry.Kind, entry.Size, entry.Modified, entry.Writable);
            count++;
        }

        handle.Listed += (int)count;
        // The count goes after the length, the type and the id.
        reply.Patch(9, count);
        return reply;
    }

    private async Task<SftpWriter> OpenAsync(Request request, CancellationToken cancellationToken)
    {
        EnsureHandleRoom();
        var flags = request.Flags;
        var write = (flags & (SftpProtocol.OpenWrite | SftpProtocol.OpenAppend | SftpProtocol.OpenCreate | SftpProtocol.OpenTruncate)) != 0;
        if (write && (flags & SftpProtocol.OpenRead) != 0)
        {
            throw new SftpException(SftpProtocol.OpUnsupported, "A file cannot be open for reading and writing at once.");
        }

        await using var operation = await files.BeginAsync(cancellationToken);
        var location = await files.ResolveAsync(operation, request.Path!, write, cancellationToken);
        if (location.Server is not { } server || location.Path.Length == 0)
        {
            throw SftpException.Failure("This is a folder.");
        }

        var stat = await operation.Files.StatAsync(server.Id, location.Path, cancellationToken);
        var exists = stat.IsSuccess;
        if (!stat.IsSuccess && stat.Error!.Code != FileErrors.NotFound)
        {
            throw SftpFileSystem.Map(stat.Error);
        }

        if (exists && stat.Value.Entry.Kind != FileEntryKind.File)
        {
            throw SftpException.Failure(stat.Value.Entry.Kind == FileEntryKind.Directory ? "This is a folder." : "Links are not followed.");
        }

        if (!write)
        {
            return exists
                ? HandleReply(request.Id, new ReadHandle(request.Path!, server, location.Path, stat.Value.Entry.Size, stat.Value.Entry.ModifiedAt))
                : throw SftpException.NotFound();
        }

        var create = (flags & SftpProtocol.OpenCreate) != 0;
        var exclusive = create && (flags & SftpProtocol.OpenExclusive) != 0;
        if (!exists && !create)
        {
            throw SftpException.NotFound();
        }

        if (exists && exclusive)
        {
            throw SftpException.Failure("The file already exists.");
        }

        if (!exists && ServerFilePath.Parent(location.Path) is { Length: > 0 } parent)
        {
            // The folder it goes in must exist, as for a local file.
            var folder = await operation.Files.StatAsync(server.Id, parent, cancellationToken);
            if (!folder.IsSuccess || folder.Value.Entry.Kind != FileEntryKind.Directory)
            {
                throw SftpException.NotFound("The folder does not exist.");
            }
        }

        var handle = new WriteHandle(request.Path!, server, location.Path)
        {
            Exists = exists,
            ExistingSize = exists ? stat.Value.Entry.Size : 0,
            Create = create,
            Append = (flags & SftpProtocol.OpenAppend) != 0,
            Truncate = (flags & SftpProtocol.OpenTruncate) != 0,
            Exclusive = exclusive,
        };
        return HandleReply(request.Id, handle);
    }

    private async Task<SftpWriter> ReadAsync(Request request, CancellationToken cancellationToken)
    {
        var handle = Find<ReadHandle>(request.Handle!);
        var want = (int)Math.Min(request.Length, (uint)options.MaxDataBytes);
        if ((request.Offset >= (ulong)Math.Max(handle.Size, 0) && handle.Content is null) || request.Offset > long.MaxValue)
        {
            return Status(request.Id, SftpProtocol.Eof, "End of file.");
        }

        var offset = (long)request.Offset;
        await using (var operation = await files.BeginAsync(cancellationToken))
        {
            SftpFileSystem.Require(operation, handle.Server, write: false);
            if (handle.Content is null || offset < handle.Position - handle.Retained || offset > handle.Position + options.ReadWindowBytes)
            {
                if (handle.Content is not null && offset >= handle.End && handle.End == handle.Size)
                {
                    return Status(request.Id, SftpProtocol.Eof, "End of file.");
                }

                // A seek back or far ahead: the download starts again there (audited as a download, with its offset).
                handle.DropContent();
                var opened = await operation.Files.OpenAtAsync(handle.Server.Id, handle.Path, offset, cancellationToken);
                if (!opened.IsSuccess)
                {
                    throw SftpFileSystem.Map(opened.Error!);
                }

                handle.Start(opened.Value, offset, options.ReadWindowBytes);
            }
        }

        if (offset >= handle.End)
        {
            return Status(request.Id, SftpProtocol.Eof, "End of file.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(want, 1));
        try
        {
            var served = await handle.ReadAsync(offset, buffer.AsMemory(0, want), cancellationToken);
            if (served == 0)
            {
                return Status(request.Id, SftpProtocol.Eof, "End of file.");
            }

            return new SftpWriter(SftpProtocol.Data, served + 16).UInt32(request.Id).Bytes(buffer.AsSpan(0, served));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            handle.DropContent();
            throw SftpException.Failure("The download from the node did not complete.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<SftpWriter> WriteAsync(Request request, CancellationToken cancellationToken)
    {
        var handle = Find<WriteHandle>(request.Handle!);
        if (handle.Failure is { } failed)
        {
            throw failed;
        }

        await using (var operation = await files.BeginAsync(cancellationToken))
        {
            SftpFileSystem.Require(operation, handle.Server, write: true);
        }

        var data = request.Data;
        if (data.Length == 0)
        {
            return Status(request.Id, SftpProtocol.Ok, string.Empty);
        }

        try
        {
            if (handle.Upload is null)
            {
                Begin(handle, request.Offset);
            }

            if (handle.Append || request.Offset == (ulong)handle.NextOffset)
            {
                await PushAsync(handle, data, cancellationToken);
                while (handle.Pending.Remove(handle.NextOffset, out var next))
                {
                    _reorderBytes -= next.Length + ReorderEntryOverhead;
                    _reorderEntries--;
                    await PushAsync(handle, next, cancellationToken);
                }
            }
            else if (request.Offset > (ulong)handle.NextOffset && request.Offset <= long.MaxValue && !handle.Pending.ContainsKey((long)request.Offset)
                && _reorderEntries < MaxReorderEntries && _reorderBytes + data.Length + ReorderEntryOverhead <= options.MaxReorderBytes)
            {
                // Ahead of the next offset: kept until the gap is filled. Each one costs more than its bytes (the array,
                // the dictionary's node), so many small ones cannot hold many times the budget.
                handle.Pending.Add((long)request.Offset, data.ToArray());
                _reorderBytes += data.Length + ReorderEntryOverhead;
                _reorderEntries++;
            }
            else
            {
                throw SftpException.Failure(request.Offset < (ulong)handle.NextOffset
                    ? "Writes must not go back over what was written."
                    : "Writes arrived too far out of order.");
            }
        }
        catch (SftpException ex)
        {
            await FailAsync(handle, ex);
            throw;
        }

        return Status(request.Id, SftpProtocol.Ok, string.Empty);
    }

    private async Task<SftpWriter> CloseAsync(Request request, CancellationToken cancellationToken)
    {
        var handle = Find<Handle>(request.Handle!);
        _handles.Remove(request.Handle!);
        switch (handle)
        {
            case ReadHandle read:
                read.DropContent();
                return Status(request.Id, SftpProtocol.Ok, string.Empty);
            case WriteHandle write:
                await FinishAsync(write, cancellationToken);
                return Status(request.Id, SftpProtocol.Ok, string.Empty);
            default:
                return Status(request.Id, SftpProtocol.Ok, string.Empty);
        }
    }

    private async Task<SftpWriter> RemoveAsync(Request request, bool directory, CancellationToken cancellationToken)
    {
        await using var operation = await files.BeginAsync(cancellationToken);
        var location = await files.ResolveAsync(operation, request.Path!, write: true, cancellationToken);
        if (location.Server is not { } server || location.Path.Length == 0)
        {
            throw SftpException.Denied("A server's own folder cannot be deleted.");
        }

        var stat = await operation.Files.StatAsync(server.Id, location.Path, cancellationToken);
        if (!stat.IsSuccess)
        {
            throw SftpFileSystem.Map(stat.Error!);
        }

        var isDirectory = stat.Value.Entry.Kind == FileEntryKind.Directory;
        if (directory != isDirectory)
        {
            throw SftpException.Failure(isDirectory ? "This is a folder." : "This is not a folder.");
        }

        if (directory)
        {
            // The node deletes folders with what is in them: SFTP's rmdir only removes an empty one. Something added
            // between this listing and the delete is deleted with it (docs/guides/sftp.md).
            var page = await operation.Files.ListPageAsync(server.Id, location.Path, null, cancellationToken);
            if (!page.IsSuccess)
            {
                throw SftpFileSystem.Map(page.Error!);
            }

            if (page.Value.Entries.Count > 0 || page.Value.Skipped > 0 || page.Value.Next is not null || page.Value.Truncated)
            {
                throw SftpException.Failure("The folder is not empty.");
            }
        }

        var deleted = await operation.Files.DeleteAsync(server.Id, location.Path, cancellationToken);
        return deleted.IsSuccess ? Status(request.Id, SftpProtocol.Ok, string.Empty) : throw SftpFileSystem.Map(deleted.Error!);
    }

    private async Task<SftpWriter> MakeDirectoryAsync(Request request, CancellationToken cancellationToken)
    {
        await using var operation = await files.BeginAsync(cancellationToken);
        var location = await files.ResolveAsync(operation, request.Path!, write: true, cancellationToken);
        if (location.Server is not { } server || location.Path.Length == 0)
        {
            throw SftpException.Failure("The folder already exists.");
        }

        if (ServerFilePath.Parent(location.Path) is { Length: > 0 } parent)
        {
            // The node makes missing parents too; SFTP's mkdir makes one folder, in a folder that exists.
            var folder = await operation.Files.StatAsync(server.Id, parent, cancellationToken);
            if (!folder.IsSuccess && folder.Error!.Code is not (FileErrors.NotFound or FileErrors.NotADirectory))
            {
                throw SftpFileSystem.Map(folder.Error);
            }

            if (!folder.IsSuccess || folder.Value.Entry.Kind != FileEntryKind.Directory)
            {
                throw SftpException.NotFound("The folder it goes in does not exist.");
            }
        }

        var made = await operation.Files.CreateDirectoryAsync(server.Id, location.Path, cancellationToken);
        return made.IsSuccess ? Status(request.Id, SftpProtocol.Ok, string.Empty) : throw SftpFileSystem.Map(made.Error!);
    }

    private async Task<SftpWriter> RenameAsync(Request request, bool replace, CancellationToken cancellationToken)
    {
        await using var operation = await files.BeginAsync(cancellationToken);
        var from = await files.ResolveAsync(operation, request.Path!, write: true, cancellationToken);
        var to = await files.ResolveAsync(operation, request.Target!, write: true, cancellationToken);
        if (from.Server is not { } server || from.Path.Length == 0 || to.Server is null || to.Path.Length == 0)
        {
            throw SftpException.Denied("A server's own folder cannot be moved.");
        }

        if (to.Server.Id != server.Id)
        {
            throw SftpException.Failure("Files cannot be moved between servers.");
        }

        var moved = await operation.Files.MoveAsync(server.Id, from.Path, to.Path, replace, cancellationToken);
        return moved.IsSuccess ? Status(request.Id, SftpProtocol.Ok, string.Empty) : throw SftpFileSystem.Map(moved.Error!);
    }

    private async Task<SftpWriter> SetStatAsync(uint id, string path, SftpAttributes attributes, CancellationToken cancellationToken)
    {
        // Permissions and sizes are accepted and ignored (Windows has no permissions to set; nothing is truncated).
        await using var operation = await files.BeginAsync(cancellationToken);
        if (attributes.ModifiedTime is not { } time)
        {
            await files.ResolveAsync(operation, path, write: false, cancellationToken);
            return Status(id, SftpProtocol.Ok, string.Empty);
        }

        var location = await files.ResolveAsync(operation, path, write: true, cancellationToken);
        if (location.Server is not { } server || location.Path.Length == 0)
        {
            return Status(id, SftpProtocol.Ok, string.Empty);
        }

        var set = await operation.Files.SetModifiedAsync(server.Id, location.Path, DateTimeOffset.FromUnixTimeSeconds(time), cancellationToken);
        return set.IsSuccess ? Status(id, SftpProtocol.Ok, string.Empty) : throw SftpFileSystem.Map(set.Error!);
    }

    private async Task<SftpWriter> FSetStatAsync(Request request, CancellationToken cancellationToken)
    {
        var handle = Find<Handle>(request.Handle!);
        if (handle is WriteHandle write)
        {
            // Applied once the file is complete: the upload's commit would change it again.
            if (request.Attributes.ModifiedTime is { } time)
            {
                write.ModifiedTime = time;
            }

            return Status(request.Id, SftpProtocol.Ok, string.Empty);
        }

        return await SetStatAsync(request.Id, handle.ClientPath, request.Attributes, cancellationToken);
    }

    // ---- uploads ----

    /// <summary>Starts the upload at the first write (or at the close of a file created empty).</summary>
    private void Begin(WriteHandle handle, ulong firstOffset)
    {
        FileWriteMode mode;
        long start;
        if (handle.Exclusive)
        {
            (mode, start) = (FileWriteMode.CreateNew, 0);
        }
        else if (handle.Append)
        {
            (mode, start) = (FileWriteMode.Append, 0);
        }
        else if (handle.Truncate || !handle.Exists)
        {
            (mode, start) = (FileWriteMode.Replace, 0);
        }
        else if (firstOffset == 0)
        {
            // An existing file written from its start without truncation: replaced by what is written, which must be at
            // least as long as the file (FinishAsync refuses to cut it short).
            (mode, start) = (FileWriteMode.Replace, 0);
        }
        else if (firstOffset == (ulong)handle.ExistingSize)
        {
            // A resumed upload: the rest goes at the end of what is there.
            (mode, start) = (FileWriteMode.Append, handle.ExistingSize);
        }
        else
        {
            throw SftpException.Failure("Writes must start at the beginning or at the end of the file.");
        }

        handle.Mode = mode;
        handle.NextOffset = start;
        handle.Upload = SftpUpload.Start(files, handle.Server, handle.Path, mode, connection.Ended);
    }

    private static async Task PushAsync(WriteHandle handle, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var upload = handle.Upload!;
        if (upload.Task.IsCompleted)
        {
            throw await upload.FailureAsync();
        }

        var written = upload.Pipe.Writer.WriteAsync(data, cancellationToken).AsTask();
        if (!written.IsCompleted && await Task.WhenAny(written, upload.Task) != written)
        {
            throw await upload.FailureAsync();
        }

        var result = await written;
        if (result.IsCompleted)
        {
            // The upload stopped reading: it ended, with an error.
            throw await upload.FailureAsync();
        }

        handle.NextOffset += data.Length;
        handle.Written += data.Length;
    }

    /// <summary>The client closed the file: the content ends, and the node's answer is the close's.</summary>
    private async Task FinishAsync(WriteHandle handle, CancellationToken cancellationToken)
    {
        var gap = handle.Pending.Count > 0;
        ReleasePending(handle);
        if (handle.Failure is { } failed)
        {
            await handle.AbortAsync();
            throw failed;
        }

        if (gap)
        {
            await handle.AbortAsync();
            throw SftpException.Failure("Part of the file never arrived: it was not saved.");
        }

        if (handle.Mode == FileWriteMode.Replace && handle.Exists && !handle.Truncate && handle.Written < handle.ExistingSize)
        {
            // Written from its start for less than it holds: saving it would cut off the rest, which a local file keeps.
            await handle.AbortAsync();
            throw SftpException.Failure("Partial overwrite is not supported: the file was not changed. Write all of it, or truncate it first.");
        }

        if (handle.Upload is null)
        {
            if (!(handle.Truncate || handle.Exclusive || (handle.Create && !handle.Exists)))
            {
                await SetTimeAsync(handle, cancellationToken);
                return;
            }

            // Nothing written to a file created or truncated: it is saved empty.
            Begin(handle, 0);
        }

        var upload = handle.Upload!;
        Result<FileDetails> result;
        try
        {
            await upload.Pipe.Writer.CompleteAsync();
            result = await upload.ResultAsync();
        }
        finally
        {
            await upload.DisposeAsync();
        }

        if (!result.IsSuccess)
        {
            throw SftpFileSystem.Map(result.Error!);
        }

        await SetTimeAsync(handle, cancellationToken);
    }

    private async Task SetTimeAsync(WriteHandle handle, CancellationToken cancellationToken)
    {
        if (handle.ModifiedTime is not { } time)
        {
            return;
        }

        try
        {
            using var reply = await SetStatAsync(0, handle.ClientPath, new SftpAttributes(null, null, time), cancellationToken);
        }
        catch (SftpException ex)
        {
            // The file is saved; only its time could not be kept.
            LogTimeNotSet(logger, connection.CorrelationId, ex.Message);
        }
    }

    private async Task FailAsync(WriteHandle handle, SftpException failure)
    {
        handle.Failure ??= failure;
        ReleasePending(handle);
        await handle.AbortAsync();
    }

    /// <summary>The writes a file kept ahead of a gap are dropped, and leave the session's budget.</summary>
    private void ReleasePending(WriteHandle handle)
    {
        _reorderBytes -= handle.Pending.Values.Sum(p => (long)p.Length + ReorderEntryOverhead);
        _reorderEntries -= handle.Pending.Count;
        handle.Pending.Clear();
    }

    private static async Task CloseQuietlyAsync(Handle handle)
    {
        switch (handle)
        {
            case ReadHandle read:
                read.DropContent();
                break;
            case WriteHandle write:
                // The client went away with the file open: what was written is not taken for the whole file.
                await write.AbortAsync();
                break;
        }
    }

    // ---- handles and replies ----

    private void EnsureHandleRoom()
    {
        if (_handles.Count >= options.MaxHandlesPerSession)
        {
            throw SftpException.Failure($"Too many open files (at most {options.MaxHandlesPerSession}).");
        }
    }

    private SftpWriter HandleReply(uint id, Handle handle)
    {
        var name = (++_nextHandle).ToString("x8", CultureInfo.InvariantCulture);
        _handles.Add(name, handle);
        return new SftpWriter(SftpProtocol.Handle).UInt32(id).String(name);
    }

    private T Find<T>(string name)
        where T : Handle =>
        _handles.TryGetValue(name, out var handle) && handle is T found ? found : throw SftpException.Failure("Not an open file or folder.");

    private static SftpWriter Status(uint id, uint code, string message) =>
        new SftpWriter(SftpProtocol.Status).UInt32(id).UInt32(code).String(message).String("en");

    private static Request Parse(ReadOnlySpan<byte> packet, out int dataStart, out int dataLength)
    {
        var reader = new SftpReader(packet);
        var type = reader.Byte();
        dataStart = 0;
        dataLength = 0;
        if (type == SftpProtocol.Init)
        {
            reader.UInt32();
            return new Request(type, 0);
        }

        var request = new Request(type, reader.UInt32());
        switch (type)
        {
            case SftpProtocol.RealPath or SftpProtocol.Stat or SftpProtocol.LStat or SftpProtocol.OpenDir or SftpProtocol.Remove or SftpProtocol.RmDir:
                request.Path = reader.String();
                break;
            case SftpProtocol.MkDir or SftpProtocol.SetStat:
                request.Path = reader.String();
                request.Attributes = reader.Attributes();
                break;
            case SftpProtocol.Open:
                request.Path = reader.String();
                request.Flags = reader.UInt32();
                request.Attributes = reader.Attributes();
                break;
            case SftpProtocol.Close or SftpProtocol.ReadDir or SftpProtocol.FStat:
                request.Handle = reader.String();
                break;
            case SftpProtocol.FSetStat:
                request.Handle = reader.String();
                request.Attributes = reader.Attributes();
                break;
            case SftpProtocol.Read:
                request.Handle = reader.String();
                request.Offset = reader.UInt64();
                request.Length = reader.UInt32();
                break;
            case SftpProtocol.Write:
                request.Handle = reader.String();
                request.Offset = reader.UInt64();
                var data = reader.Bytes();
                dataLength = data.Length;
                dataStart = packet.Length - reader.Remaining - data.Length;
                break;
            case SftpProtocol.Rename:
                request.Path = reader.String();
                request.Target = reader.String();
                break;
            case SftpProtocol.Extended:
                request.Extension = reader.String();
                if (request.Extension == SftpProtocol.PosixRename)
                {
                    request.Path = reader.String();
                    request.Target = reader.String();
                }

                break;
        }

        return request;
    }

    private sealed class Request(byte type, uint id)
    {
        public byte Type { get; } = type;

        public uint Id { get; } = id;

        public string? Path { get; set; }

        public string? Target { get; set; }

        public string? Handle { get; set; }

        public string? Extension { get; set; }

        public uint Flags { get; set; }

        public ulong Offset { get; set; }

        public uint Length { get; set; }

        public SftpAttributes Attributes { get; set; }

        public ReadOnlyMemory<byte> Data { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "SFTP session {CorrelationId} sent a packet of {Length} bytes: closed")]
    private static partial void LogBadPacket(ILogger logger, string correlationId, uint length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SFTP request {Type} of session {CorrelationId} failed")]
    private static partial void LogRequestFailed(ILogger logger, string correlationId, byte type, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "SFTP session {CorrelationId}: a folder listing stopped after {Count} entries")]
    private static partial void LogListingCut(ILogger logger, string correlationId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SFTP session {CorrelationId}: a file's time was not kept ({Reason})")]
    private static partial void LogTimeNotSet(ILogger logger, string correlationId, string reason);
}
