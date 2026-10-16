using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Files;

/// <summary>
/// The file manager with plain .NET file APIs: checks every path component for junctions and links, but acts as the
/// agent itself and cannot rule out a link swapped in between a check and its use. For tests and platforms without
/// a stronger implementation; the Windows agent uses one that works on handles as the server's account.
/// </summary>
public sealed class DirectServerFileManager : IServerFileManager
{
    public FileListResult List(ProvisionedServer server, string path) =>
        Guard(path, () => ServerFileRules.Listing(EnumerateDirectory(server, path)));

    public FileListResult ListPage(ProvisionedServer server, string path, string? after) =>
        Guard(path, () => ServerFileRules.Page(EnumerateDirectory(server, path), path, after));

    public FileEntryResult Stat(ProvisionedServer server, string path) => Existing(Resolve(server, path), path) ?? throw NotFound(path);

    public FileEntryResult SetModified(ProvisionedServer server, string path, DateTimeOffset modifiedAt)
    {
        var full = Resolve(server, path);
        var entry = Existing(full, path) ?? throw NotFound(path);
        if (entry.Entry.Kind == FileEntryKind.Link)
        {
            throw new FileManagerException(FileErrors.Link, $"'{path}' is a junction or link.");
        }

        var time = DateTime.FromFileTimeUtc(ServerFileRules.FileTime(modifiedAt));
        Guard(path, () =>
        {
            if (entry.Entry.Kind == FileEntryKind.Directory)
            {
                Directory.SetLastWriteTimeUtc(full, time);
            }
            else
            {
                File.SetLastWriteTimeUtc(full, time);
            }
        });
        return Existing(full, path) ?? throw NotFound(path);
    }

    /// <summary>The entries of the directory at <paramref name="path"/>, read as they are consumed.</summary>
    private static IEnumerable<FileEntry> EnumerateDirectory(ProvisionedServer server, string path)
    {
        var full = Resolve(server, path, allowRoot: true);
        // Resolve checks the ancestors only: the listed folder itself may be the junction.
        if (IsLink(full))
        {
            throw new FileManagerException(FileErrors.Link, $"'{path}' is a junction or link.");
        }

        var info = new DirectoryInfo(full);
        if (!info.Exists)
        {
            throw File.Exists(full) || IsLink(full)
                ? new FileManagerException(FileErrors.NotADirectory, $"'{path}' is not a directory.")
                : NotFound(path);
        }

        return info.EnumerateFileSystemInfos().Select(Entry);
    }

    public void CreateDirectory(ProvisionedServer server, string path)
    {
        var full = Resolve(server, path);
        if (Directory.Exists(full) || File.Exists(full))
        {
            throw new FileManagerException(FileErrors.Exists, $"'{path}' already exists.");
        }

        for (var parent = ServerFilePath.Parent(path); parent.Length > 0; parent = ServerFilePath.Parent(parent))
        {
            if (File.Exists(Resolve(server, parent)))
            {
                throw new FileManagerException(FileErrors.NotADirectory, $"'{parent}' is not a directory.");
            }
        }

        Guard(path, () => Directory.CreateDirectory(full));
    }

    public void EnsureDirectory(ProvisionedServer server, string path)
    {
        var full = Resolve(server, path);
        for (var current = path; current.Length > 0; current = ServerFilePath.Parent(current))
        {
            var existing = Resolve(server, current);
            if (IsLink(existing))
            {
                throw new FileManagerException(FileErrors.Link, $"'{current}' is a junction or link.");
            }

            if (File.Exists(existing))
            {
                throw new FileManagerException(FileErrors.NotADirectory, $"'{current}' is not a directory.");
            }
        }

        Guard(path, () => Directory.CreateDirectory(full));
    }

    public TreeWalk Walk(ProvisionedServer server)
    {
        var root = Resolve(server, ServerFilePath.Root, allowRoot: true);
        var entries = new TreeWalk();
        Guard(ServerFilePath.Root, () => WalkInto(root, ServerFilePath.Root, entries, 0));
        return entries;
    }

    private static void WalkInto(string directory, string path, TreeWalk entries, int depth)
    {
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (!ServerFilePath.IsValid(ServerFilePath.Join(path, info.Name)))
            {
                entries.Refuse(ServerFilePath.Join(path, info.Name));
                continue;
            }

            var entry = Entry(info);
            var entryPath = ServerFilePath.Join(path, entry.Name);
            entries.Add(new TreeEntry(entryPath, entry.Kind, entry.Size, entry.ModifiedAt));
            ServerFileRules.CheckWalk(entries.Count, depth);
            if (entry.Kind == FileEntryKind.Directory && !ServerFileRules.IsSteamLoginFolder(entryPath))
            {
                WalkInto(info.FullName, entryPath, entries, depth + 1);
            }
        }
    }

    public void Move(ProvisionedServer server, string path, string destination, bool replace = false)
    {
        ServerFileRules.CheckMove(path, destination);
        var source = Resolve(server, path);
        var target = Resolve(server, destination);
        RequireDirectory(server, ServerFilePath.Parent(destination));
        var existing = Existing(target, destination);
        if (existing is not null && !replace)
        {
            throw new FileManagerException(FileErrors.Exists, $"'{destination}' already exists.");
        }

        if (existing is not null)
        {
            var kind = IsLink(source) ? FileEntryKind.Link : Directory.Exists(source) ? FileEntryKind.Directory : FileEntryKind.File;
            ServerFileRules.CheckReplace(destination, kind, existing.Entry.Kind);
        }

        Guard(path, () =>
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else if (File.Exists(source))
            {
                File.Move(source, target, overwrite: existing is not null);
            }
            else
            {
                throw NotFound(path);
            }
        });
    }

    public void Delete(ProvisionedServer server, string path)
    {
        var full = Resolve(server, path);
        if (!Exists(full))
        {
            throw NotFound(path);
        }

        Guard(path, () => DeleteTree(full));
    }

    public OpenedFile OpenRead(ProvisionedServer server, string path, long maxSize)
    {
        var full = Resolve(server, path);
        var entry = Existing(full, path) ?? throw NotFound(path);
        if (entry.Entry.Kind != FileEntryKind.File)
        {
            throw entry.Entry.Kind == FileEntryKind.Link
                ? new FileManagerException(FileErrors.Link, $"'{path}' is a junction or link.")
                : new FileManagerException(FileErrors.IsADirectory, $"'{path}' is a directory.");
        }

        if (entry.Entry.Size > maxSize)
        {
            throw new FileManagerException(FileErrors.TooLarge, $"'{path}' is larger than {maxSize} bytes.");
        }

        return Guard(path, () => new OpenedFile(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), entry));
    }

    public PendingFile Create(ProvisionedServer server, string path, long size, bool overwrite, string? expectedVersion, bool sizeIsLimit = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        var full = Resolve(server, path);
        var directory = RequireDirectory(server, ServerFilePath.Parent(path));
        ServerFileRules.CheckTarget(path, Existing(full, path), overwrite, expectedVersion);
        var temporary = Path.Combine(directory, ServerFileRules.TemporaryName());
        return Guard(path, () => new Pending(path, full, temporary, sizeIsLimit ? null : size, overwrite, expectedVersion));
    }

    public PendingFile Append(ProvisionedServer server, string path, string? expectedVersion)
    {
        var full = Resolve(server, path);
        RequireDirectory(server, ServerFilePath.Parent(path));
        var existing = Existing(full, path);
        if (existing is not null || expectedVersion is not null)
        {
            ServerFileRules.CheckTarget(path, existing, overwrite: true, expectedVersion);
        }

        return Guard(path, () => new Appending(path, full));
    }

    /// <summary>The absolute path, after checking that no component on the way is a junction or link.</summary>
    private static string Resolve(ProvisionedServer server, string path, bool allowRoot = false)
    {
        ArgumentNullException.ThrowIfNull(server);
        ServerFileRules.Validate(path, allowRoot);

        // By name only: these APIs cannot see where a short name leads (the Windows file manager checks its handles).
        if (ServerFileRules.IsSteamLoginFolder(path))
        {
            throw ServerFileRules.SteamLoginRefused(path);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.Directory));
        if (!Directory.Exists(root))
        {
            throw new FileManagerException(FileErrors.NotFound, "The server directory does not exist.");
        }

        if (path.Length == 0)
        {
            return root;
        }

        var full = Path.GetFullPath(Path.Join(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileManagerException(FileErrors.OutsideServer, $"'{path}' resolves outside the server directory.");
        }

        var current = root;
        var segments = path.Split('/');
        foreach (var segment in segments[..^1])
        {
            current = Path.Combine(current, segment);
            if (IsLink(current))
            {
                throw new FileManagerException(FileErrors.Link, $"'{Path.GetRelativePath(root, current)}' is a junction or link.");
            }
        }

        return full;
    }

    private static string RequireDirectory(ProvisionedServer server, string path)
    {
        var full = Resolve(server, path, allowRoot: true);
        if (IsLink(full) || !Directory.Exists(full))
        {
            throw File.Exists(full) || IsLink(full)
                ? new FileManagerException(FileErrors.NotADirectory, $"'{path}' is not a directory.")
                : new FileManagerException(FileErrors.NotFound, $"The folder '{path}' does not exist.");
        }

        return full;
    }

    private static bool Exists(string full) => File.Exists(full) || Directory.Exists(full) || IsLink(full);

    private static bool IsLink(string full)
    {
        try
        {
            return File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static FileEntryResult? Existing(string full, string path)
    {
        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if (!info.Exists && !IsLink(full))
        {
            return null;
        }

        var entry = Entry(info) with { Name = ServerFilePath.Name(path) };
        return new FileEntryResult(entry, FileVersion.Of(entry.Size, entry.ModifiedAt));
    }

    private static FileEntry Entry(FileSystemInfo info)
    {
        var kind = info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? FileEntryKind.Link
            : info is DirectoryInfo ? FileEntryKind.Directory
            : FileEntryKind.File;
        var size = kind == FileEntryKind.File ? ((FileInfo)info).Length : 0;
        return new FileEntry(info.Name, kind, size, ModifiedAt(info));
    }

    /// <summary>
    /// The last write of <paramref name="info"/>. A game's account can set a time NTFS keeps but a DateTime cannot hold
    /// (beyond year 9999), which makes the property throw: that reads as the latest time there is.
    /// </summary>
    private static DateTimeOffset ModifiedAt(FileSystemInfo info)
    {
        try
        {
            return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
        }
    }

    /// <summary>Deletes links as links: a recursive delete must never empty a junction's target.</summary>
    private static void DeleteTree(string full)
    {
        var attributes = File.GetAttributes(full);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            File.SetAttributes(full, attributes & ~FileAttributes.ReadOnly);
            File.Delete(full);
            return;
        }

        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(full))
            {
                DeleteTree(child);
            }
        }

        Directory.Delete(full);
    }

    private static void Guard(string path, Action action) => Guard(path, () =>
    {
        action();
        return 0;
    });

    private static T Guard<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ServerFileRules.Translate(path, ex) is { } failure)
        {
            throw failure;
        }
    }

    private static FileManagerException NotFound(string path) => new(FileErrors.NotFound, $"'{path}' does not exist.");

    private sealed class Pending : PendingFile
    {
        private static readonly TimeSpan[] s_replaceRetries = [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250)];

        private readonly string _path;
        private readonly string _target;
        private readonly string _temporary;
        private readonly long? _size;
        private readonly bool _overwrite;
        private readonly string? _expectedVersion;
        private readonly FileStream _stream;
        private bool _committed;

        /// <param name="size">The exact length expected; null: whatever is written (a streamed upload, bounded by the caller).</param>
        public Pending(string path, string target, string temporary, long? size, bool overwrite, string? expectedVersion)
        {
            (_path, _target, _temporary, _size, _overwrite, _expectedVersion) = (path, target, temporary, size, overwrite, expectedVersion);
            _stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        public override Stream Content => _stream;

        public override FileEntryResult Commit()
        {
            if (_size is not null && _stream.Length != _size)
            {
                throw new FileManagerException(FileErrors.TransferFailed, $"Received {_stream.Length} of {_size} bytes.");
            }

            _stream.Dispose();
            ServerFileRules.CheckTarget(_path, Existing(_target, _path), _overwrite, _expectedVersion);
            Guard(_path, Replace);
            _committed = true;
            return Existing(_target, _path)!;
        }

        /// <summary>
        /// A replace on Windows (MoveFileEx) fails while another program reads the old file, as a virus scanner does
        /// right after it is written: tried again a few times before giving up.
        /// </summary>
        private void Replace()
        {
            foreach (var delay in s_replaceRetries)
            {
                try
                {
                    File.Move(_temporary, _target, overwrite: true);
                    return;
                }
                catch (Exception ex) when (OperatingSystem.IsWindows()
                    && (ex is UnauthorizedAccessException || ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)))
                {
                    Thread.Sleep(delay);
                }
            }

            File.Move(_temporary, _target, overwrite: true);
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            _stream.Dispose();
            if (!_committed)
            {
                File.Delete(_temporary);
            }
        }
    }

    /// <summary>A file written at its end, in place: what was written stays, committed or not.</summary>
    private sealed class Appending : PendingFile
    {
        private readonly string _path;
        private readonly string _target;
        private readonly FileStream _stream;

        public Appending(string path, string target)
        {
            (_path, _target) = (path, target);
            _stream = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }

        public override Stream Content => _stream;

        public override FileEntryResult Commit()
        {
            Guard(_path, _stream.Flush);
            _stream.Dispose();
            return Existing(_target, _path) ?? throw NotFound(_path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }
        }
    }
}
