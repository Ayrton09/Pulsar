using System.Buffers;
using System.IO.Pipelines;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Sftp;

/// <summary>An open file or folder of an SFTP session.</summary>
internal abstract class Handle(string clientPath)
{
    /// <summary>The path the client opened it by (permissions and stats are checked by it again).</summary>
    public string ClientPath { get; } = clientPath;
}

/// <summary>One entry of a listing as SFTP shows it.</summary>
internal sealed record Listed(string Name, FileEntryKind Kind, long Size, DateTimeOffset Modified, bool Writable)
{
    public static List<Listed> From(IReadOnlyList<FileEntry> entries, bool writable) =>
        [.. entries.Select(e => new Listed(e.Name, e.Kind, e.Size, e.ModifiedAt, writable))];
}

/// <summary>A folder being listed: the root's servers, or a server's folder a page at a time.</summary>
internal sealed class DirectoryHandle(string clientPath, SftpServer? server, string path, List<Listed> entries, string? next) : Handle(clientPath)
{
    public SftpServer? Server { get; } = server;

    public string Path { get; } = path;

    public List<Listed> Entries { get; set; } = entries;

    public int Index { get; set; }

    /// <summary>Entries returned so far (<see cref="SftpSession.MaxListingEntries"/>).</summary>
    public int Listed { get; set; }

    /// <summary>Where the next page starts; null: the last page is listed.</summary>
    public string? Next { get; set; } = next;
}

/// <summary>
/// A file open for reading: one download from the node, served in order. A read a little ahead skips forward keeping
/// what it skipped, and a read of what was just served is answered from what was kept (clients that read in parallel or
/// ask again); anything else starts a new download at its offset. The download gives its place among the node's
/// transfers back once it reached its end, or when no read used it for a while (<see cref="ReleaseIfIdle"/>).
/// </summary>
internal sealed class ReadHandle(string clientPath, SftpServer server, string path, long size, DateTimeOffset modified) : Handle(clientPath)
{
    private const int SkipChunk = 64 * 1024;

    private byte[]? _window;
    private int _windowSize;
    private bool _completed;
    private FileContent? _content;
    private long _lastUsed;

    public SftpServer Server { get; } = server;

    public string Path { get; } = path;

    public long Size { get; private set; } = size;

    public DateTimeOffset Modified { get; private set; } = modified;

    public FileContent? Content => Volatile.Read(ref _content);

    /// <summary>Held by the session while a read uses the download (so it is never released under a read).</summary>
    public SemaphoreSlim Busy { get; } = new(1, 1);

    /// <summary>The next offset the download delivers.</summary>
    public long Position { get; private set; }

    /// <summary>Where the download ends.</summary>
    public long End { get; private set; }

    /// <summary>How many bytes before <see cref="Position"/> are kept.</summary>
    public long Retained { get; private set; }

    public void Start(FileContent content, long offset, int window)
    {
        ArgumentNullException.ThrowIfNull(content);
        DropContent();
        Volatile.Write(ref _content, content);
        Position = offset;
        End = offset + content.Length;
        Size = content.File.Entry.Size;
        Modified = content.File.Entry.ModifiedAt;
        _completed = false;
        _windowSize = window;
        _window = window > 0 ? ArrayPool<byte>.Shared.Rent(window) : null;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> from <paramref name="offset"/> (within the kept bytes or the window ahead) up to the
    /// download's end; the bytes served. A download that ends early is an <see cref="IOException"/>.
    /// </summary>
    public async Task<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var content = Content ?? throw new InvalidOperationException("No download is running.");
        var served = 0;
        if (offset < Position)
        {
            served = (int)Math.Min(Position - offset, buffer.Length);
            CopyKept(offset, buffer.Span[..served]);
        }
        else if (offset > Position)
        {
            var skip = ArrayPool<byte>.Shared.Rent(SkipChunk);
            try
            {
                while (Position < offset)
                {
                    var chunk = (int)Math.Min(Math.Min(SkipChunk, offset - Position), End - Position);
                    if (chunk <= 0)
                    {
                        return 0;
                    }

                    var n = await content.Content.ReadAsync(skip.AsMemory(0, chunk), cancellationToken);
                    Advance(skip.AsSpan(0, n));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(skip);
            }
        }

        while (served < buffer.Length && Position < End)
        {
            var room = (int)Math.Min(buffer.Length - served, End - Position);
            var n = await content.Content.ReadAsync(buffer.Slice(served, room), cancellationToken);
            Advance(buffer.Span.Slice(served, n));
            served += n;
        }

        if (Position == End && !_completed)
        {
            _completed = true;
            content.Complete();
            // All of it arrived: the node's transfer place is free again, and what was kept still answers reads of it.
            content.Dispose();
        }

        return served;
    }

    /// <summary>A read used the download at <paramref name="timestamp"/> (<see cref="TimeProvider.GetTimestamp"/>).</summary>
    public void Used(long timestamp) => Interlocked.Exchange(ref _lastUsed, timestamp);

    /// <summary>
    /// Ends a download that no read used for <paramref name="after"/>, unless a read is using it now: true when it did.
    /// Called away from the session's requests.
    /// </summary>
    public bool ReleaseIfIdle(TimeProvider clock, TimeSpan after)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Content is null || clock.GetElapsedTime(Interlocked.Read(ref _lastUsed)) < after || !Busy.Wait(0))
        {
            return false;
        }

        try
        {
            if (Content is null)
            {
                return false;
            }

            DropContent();
            return true;
        }
        finally
        {
            Busy.Release();
        }
    }

    /// <summary>Ends the download (a new one starts at the next read, if any). Each part is let go once, whoever calls.</summary>
    public void DropContent()
    {
        Interlocked.Exchange(ref _content, null)?.Dispose();
        Retained = 0;
        if (Interlocked.Exchange(ref _window, null) is { } window)
        {
            ArrayPool<byte>.Shared.Return(window);
        }
    }

    private void Advance(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            throw new IOException("The download from the node ended early.");
        }

        if (_window is not null)
        {
            // The last _windowSize bytes, by their offset modulo the window.
            var keep = bytes.Length > _windowSize ? bytes[^_windowSize..] : bytes;
            var at = Position + (bytes.Length - keep.Length);
            while (keep.Length > 0)
            {
                var index = (int)(at % _windowSize);
                var part = Math.Min(keep.Length, _windowSize - index);
                keep[..part].CopyTo(_window.AsSpan(index));
                keep = keep[part..];
                at += part;
            }

            Retained = Math.Min(_windowSize, Retained + bytes.Length);
        }

        Position += bytes.Length;
    }

    private void CopyKept(long offset, Span<byte> destination)
    {
        var window = _window ?? throw new InvalidOperationException("Nothing is kept.");
        while (destination.Length > 0)
        {
            var index = (int)(offset % _windowSize);
            var part = Math.Min(destination.Length, _windowSize - index);
            window.AsSpan(index, part).CopyTo(destination);
            destination = destination[part..];
            offset += part;
        }
    }
}

/// <summary>A file open for writing: a streamed upload to the node, started at the first write and committed at the close.</summary>
internal sealed class WriteHandle(string clientPath, SftpServer server, string path) : Handle(clientPath)
{
    public SftpServer Server { get; } = server;

    public string Path { get; } = path;

    public bool Exists { get; init; }

    public long ExistingSize { get; init; }

    public bool Create { get; init; }

    public bool Append { get; init; }

    public bool Truncate { get; init; }

    public bool Exclusive { get; init; }

    public FileWriteMode? Mode { get; set; }

    /// <summary>The offset the next write must have to go straight to the node.</summary>
    public long NextOffset { get; set; }

    /// <summary>Bytes that went to the node.</summary>
    public long Written { get; set; }

    /// <summary>Writes ahead of <see cref="NextOffset"/>, by offset.</summary>
    public SortedDictionary<long, byte[]> Pending { get; } = [];

    /// <summary>The time the client asked for, set once the file is complete.</summary>
    public uint? ModifiedTime { get; set; }

    /// <summary>Why the file can no longer be saved: every later write and the close answer it.</summary>
    public SftpException? Failure { get; set; }

    public SftpUpload? Upload { get; set; }

    public async Task AbortAsync()
    {
        if (Upload is { } upload)
        {
            await upload.AbortAsync();
        }
    }
}

/// <summary>
/// The upload behind a <see cref="WriteHandle"/>: <see cref="ServerFiles.WriteStreamAsync"/> reading what the client writes
/// through a pipe of bounded size (a client faster than the node waits), in a DI scope of its own as the connection's user.
/// </summary>
internal sealed class SftpUpload : IAsyncDisposable
{
    private const long PauseBytes = 1024 * 1024;

    private readonly AsyncServiceScope _scope;
    private readonly CancellationTokenSource _cancel;
    private int _ended;

    private SftpUpload(AsyncServiceScope scope, CancellationTokenSource cancel, Pipe pipe, Task<Result<FileDetails>> task)
    {
        _scope = scope;
        _cancel = cancel;
        Pipe = pipe;
        Task = task;
    }

    public Pipe Pipe { get; }

    public Task<Result<FileDetails>> Task { get; }

    public static SftpUpload Start(SftpFileSystem files, SftpServer server, string path, FileWriteMode mode, CancellationToken ended)
    {
        var scope = files.CreateScope();
        var serverFiles = scope.ServiceProvider.GetRequiredService<ServerFiles>();
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: PauseBytes, resumeWriterThreshold: PauseBytes / 2, useSynchronizationContext: false));
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(ended);
        var task = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                return await serverFiles.WriteStreamAsync(server.Id, path, pipe.Reader.AsStream(), mode, cancel.Token);
            }
            finally
            {
                await pipe.Reader.CompleteAsync();
            }
        }, CancellationToken.None);
        return new SftpUpload(scope, cancel, pipe, task);
    }

    /// <summary>What the node answered; an upload cut off, or one that failed any other way, counts as a failed transfer.</summary>
    public async Task<Result<FileDetails>> ResultAsync()
    {
        try
        {
            return await Task;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ServerErrors.FileTransferFailed;
        }
    }

    /// <summary>Why the upload stopped before the client was done.</summary>
    public async Task<SftpException> FailureAsync()
    {
        var result = await ResultAsync();
        return result.IsSuccess ? SftpException.Failure("The upload ended before the file did.") : SftpFileSystem.Map(result.Error!);
    }

    /// <summary>The client gave up the file: the content fails, so the node never takes what arrived for the whole file.</summary>
    public async Task AbortAsync()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        await Pipe.Writer.CompleteAsync(new IOException("The SFTP client did not finish the file."));
        try
        {
            await Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Failed, as it should; or still waiting on the node, which the cancellation below ends.
        }

        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _ended, 1);
        if (Task.IsCompleted)
        {
            _cancel.Dispose();
            await _scope.DisposeAsync();
            return;
        }

        // Still running: it stops now and lets go of its scope when it is done (it audits what happened first).
        await _cancel.CancelAsync();
        _ = Task.ContinueWith(async _ =>
        {
            _cancel.Dispose();
            await _scope.DisposeAsync();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
