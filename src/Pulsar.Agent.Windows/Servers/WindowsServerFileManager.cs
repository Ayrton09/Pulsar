using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// The file manager on Windows (docs/architecture/08, "File manager"). After the path rules (layer 1) and the prefix
/// check of the full path (layer 2), every entry is opened without following a final link and the path the handle
/// really has is checked against the server directory, and against the lent Steam login's folder (layer 3); the
/// operation then works on that handle: listing, reading, renaming and deleting never go back to a path a server
/// could have swapped. Opening, creating, renaming and deleting run as the server's account (layer 4), so Windows
/// itself refuses anything outside what that account may touch, whatever a hard link or a race points at. Reading and
/// writing content happens on the opened handles, outside the impersonation.
/// </summary>
public sealed class WindowsServerFileManager(IServerImpersonation impersonation) : IServerFileManager
{
    private const uint ListDirectory = 0x0001;
    private const uint AddFile = 0x0002;
    private const uint AddSubdirectory = 0x0004;
    private const uint ReadAttributes = 0x0080;
    private const uint WriteAttributes = 0x0100;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint NameSurrogate = 0x20000000;

    // What one read of a directory's entries returns at most (a few hundred entries).
    private const int ListingBuffer = 64 * 1024;

    /// <summary>Tests: runs just before an upload's temporary file is created (to swap folders in the meantime).</summary>
    internal Action? BeforeCreate { get; init; }

    /// <summary>Tests: runs right after an upload's temporary file was renamed to its target (to fail what follows).</summary>
    internal Action? AfterRename { get; init; }

    public FileListResult List(ProvisionedServer server, string path)
    {
        ServerFileRules.Validate(path, allowRoot: true);
        return Run(server, root =>
        {
            using var directory = root.OpenDirectory(path, ListDirectory | ReadAttributes | Synchronize);
            return ServerFileRules.Listing(Enumerate(directory, path));
        }, write: false);
    }

    public FileListResult ListPage(ProvisionedServer server, string path, string? after)
    {
        ServerFileRules.Validate(path, allowRoot: true);
        return Run(server, root =>
        {
            using var directory = root.OpenDirectory(path, ListDirectory | ReadAttributes | Synchronize);
            return ServerFileRules.Page(Enumerate(directory, path), path, after);
        }, write: false);
    }

    public FileEntryResult Stat(ProvisionedServer server, string path)
    {
        ServerFileRules.Validate(path);
        return Run(server, root => Existing(root, path) ?? throw NotFound(path), write: false);
    }

    public FileEntryResult SetModified(ProvisionedServer server, string path, DateTimeOffset modifiedAt)
    {
        ServerFileRules.Validate(path);
        return Run(server, root =>
        {
            using var entry = root.TryOpen(path, WriteAttributes | ReadAttributes | Synchronize) ?? throw NotFound(path);
            if (Describe(entry.Handle).Kind == FileEntryKind.Link)
            {
                throw new FileManagerException(FileErrors.Link, $"'{path}' is a junction or link.");
            }

            // Zero leaves a time as it is: only the last write changes.
            var basic = new FILE_BASIC_INFO { LastWriteTime = ServerFileRules.FileTime(modifiedAt) };
            unsafe
            {
                if (!PInvoke.SetFileInformationByHandle((HANDLE)entry.Handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo, &basic,
                    (uint)sizeof(FILE_BASIC_INFO)))
                {
                    throw Win32Failure(path);
                }
            }

            return Result(path, Describe(entry.Handle));
        });
    }

    public void CreateDirectory(ProvisionedServer server, string path) => MakeDirectories(server, path, mustBeNew: true);

    public void EnsureDirectory(ProvisionedServer server, string path) => MakeDirectories(server, path, mustBeNew: false);

    public TreeWalk Walk(ProvisionedServer server) => Run(server, root =>
    {
        var entries = new TreeWalk();
        WalkInto(root, ServerFilePath.Root, entries, 0);
        return entries;
    }, write: false);

    /// <summary>Lists each directory from its own checked handle; links are recorded, never opened as directories.</summary>
    private static void WalkInto(ServerRoot root, string path, TreeWalk entries, int depth)
    {
        List<FileEntry> children;
        using (var directory = root.OpenDirectory(path, ListDirectory | ReadAttributes | Synchronize))
        {
            // One more than the walk may still keep is enough: past it, entries are only counted.
            children = [.. Enumerate(directory, path).Take(ServerFileRules.MaxWalkEntries - entries.Count + 1)];
        }

        foreach (var child in children)
        {
            var childPath = ServerFilePath.Join(path, child.Name);
            if (!ServerFilePath.IsValid(childPath) || !ServerFileRules.WalkKeeps(entries, child.Kind, depth + 1))
            {
                entries.Refuse(childPath);
                continue;
            }

            entries.Add(new TreeEntry(childPath, child.Kind, child.Size, child.ModifiedAt));
            if (child.Kind == FileEntryKind.Directory && !ServerFileRules.IsSteamLoginFolder(childPath))
            {
                WalkInto(root, childPath, entries, depth + 1);
            }
        }
    }

    private void MakeDirectories(ProvisionedServer server, string path, bool mustBeNew)
    {
        ServerFileRules.Validate(path);
        Run(server, root =>
        {
            var segments = path.Split('/');
            var current = ServerFilePath.Root;
            for (var i = 0; i < segments.Length; i++)
            {
                current = ServerFilePath.Join(current, segments[i]);
                var last = i == segments.Length - 1;
                using var existing = root.TryOpen(current, ReadAttributes | Synchronize);
                if (existing is not null)
                {
                    var kind = Describe(existing.Handle).Kind;
                    if (last && mustBeNew)
                    {
                        throw new FileManagerException(FileErrors.Exists, $"'{path}' already exists.");
                    }

                    if (kind != FileEntryKind.Directory)
                    {
                        throw NotADirectory(current, kind);
                    }

                    continue;
                }

                unsafe
                {
                    if (!PInvoke.CreateDirectory(root.Full(current), null))
                    {
                        throw Win32Failure(current);
                    }
                }

                // Checked before going below it: a junction swapped in here would take the next level elsewhere.
                using var created = root.TryOpen(current, ReadAttributes | Synchronize) ?? throw NotFound(current);
                if (Describe(created.Handle).Kind != FileEntryKind.Directory)
                {
                    throw NotADirectory(current, FileEntryKind.Link);
                }
            }

            return 0;
        });
    }

    public void Move(ProvisionedServer server, string path, string destination, bool replace = false)
    {
        ServerFileRules.CheckMove(path, destination);
        Run(server, root =>
        {
            using var source = root.TryOpen(path, DeleteAccess | ReadAttributes | Synchronize) ?? throw NotFound(path);
            using var directory = root.OpenDirectory(ServerFilePath.Parent(destination), ListDirectory | AddFile | AddSubdirectory | ReadAttributes | Synchronize);

            // Only a file found there now is replaced: anything that appears meanwhile makes the rename fail as "exists",
            // and Windows never renames over a directory, whatever was checked.
            var existing = replace ? Existing(root, destination) : null;
            if (existing is not null)
            {
                ServerFileRules.CheckReplace(destination, Describe(source.Handle).Kind, existing.Entry.Kind);
            }

            Rename(source.Handle, directory, ServerFilePath.Name(destination), replace: existing is not null, destination);
            return 0;
        });
    }

    public void Delete(ProvisionedServer server, string path)
    {
        ServerFileRules.Validate(path);
        Run(server, root =>
        {
            var entry = root.TryOpen(path, DeleteAccess | ReadAttributes | Synchronize) ?? throw NotFound(path);
            DeleteTree(entry.Detach(), path);
            return 0;
        });
    }

    public OpenedFile OpenRead(ProvisionedServer server, string path, long maxSize)
    {
        ServerFileRules.Validate(path);
        return Run(server, root =>
        {
            var opened = root.TryOpen(path, GenericRead | Synchronize, dataAccess: true) ?? throw NotFound(path);
            try
            {
                var info = Describe(opened.Handle);
                if (info.Kind != FileEntryKind.File)
                {
                    throw info.Kind == FileEntryKind.Link
                        ? new FileManagerException(FileErrors.Link, $"'{path}' is a junction or link.")
                        : new FileManagerException(FileErrors.IsADirectory, $"'{path}' is a directory.");
                }

                if (info.Size > maxSize)
                {
                    throw new FileManagerException(FileErrors.TooLarge, $"'{path}' is larger than {maxSize} bytes.");
                }

                var handle = opened.Detach();
                return new OpenedFile(new FileStream(handle, FileAccess.Read, bufferSize: 0), Result(path, info));
            }
            finally
            {
                opened.Dispose();
            }
        }, write: false);
    }

    public PendingFile Create(ProvisionedServer server, string path, long size, bool overwrite, string? expectedVersion, bool sizeIsLimit = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ServerFileRules.Validate(path);
        return Run(server, root =>
        {
            var parent = ServerFilePath.Parent(path);
            var directory = root.OpenDirectory(parent, ListDirectory | AddFile | ReadAttributes | Synchronize);
            try
            {
                ServerFileRules.CheckTarget(path, Existing(root, path), overwrite, expectedVersion);
                var temporary = ServerFilePath.Join(parent, ServerFileRules.TemporaryName());
                BeforeCreate?.Invoke();
                var handle = root.Create(directory, temporary, GenericWrite | DeleteAccess | ReadAttributes | Synchronize);
                return new Pending(this, server, path, sizeIsLimit ? null : size, overwrite, expectedVersion, directory, handle);
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        });
    }

    public PendingFile Append(ProvisionedServer server, string path, string? expectedVersion)
    {
        ServerFileRules.Validate(path);
        return Run(server, root =>
        {
            using var directory = root.OpenDirectory(ServerFilePath.Parent(path), ListDirectory | AddFile | ReadAttributes | Synchronize);
            var existing = Existing(root, path);
            if (existing is not null || expectedVersion is not null)
            {
                ServerFileRules.CheckTarget(path, existing, overwrite: true, expectedVersion);
            }

            if (existing is null)
            {
                BeforeCreate?.Invoke();
                return new Appending(path, root.Create(directory, path, GenericWrite | DeleteAccess | ReadAttributes | Synchronize));
            }

            var opened = root.TryOpen(path, GenericWrite | ReadAttributes | Synchronize, dataAccess: true) ?? throw NotFound(path);
            try
            {
                // Checked again on the handle written through: the name may lead elsewhere by now.
                var info = Describe(opened.Handle);
                ServerFileRules.CheckTarget(path, Result(path, info), overwrite: true, expectedVersion);
                return new Appending(path, opened.Detach());
            }
            finally
            {
                opened.Dispose();
            }
        });
    }

    /// <param name="write">
    /// Whether the action may create in the server directory; reads open it read-only, so they also work on a snapshot
    /// of it (a backup of a running game, docs/architecture/06, "Backup snapshots").
    /// </param>
    private T Run<T>(ProvisionedServer server, Func<ServerRoot, T> action, bool write = true)
    {
        ArgumentNullException.ThrowIfNull(server);
        try
        {
            return impersonation.Run(server.Identity, () =>
            {
                using var root = ServerRoot.Open(server.Directory, write);
                return action(root);
            });
        }
        catch (Win32Exception ex)
        {
            // Logging the account on failed (deleted meanwhile, policy): report it like any other refusal.
            throw new FileManagerException(FileErrors.AccessDenied, $"Cannot act as the server's account: {ex.Message}", ex);
        }
    }

    private static FileEntryResult? Existing(ServerRoot root, string path)
    {
        using var entry = root.TryOpen(path, ReadAttributes | Synchronize);
        return entry is null ? null : Result(path, Describe(entry.Handle));
    }

    /// <summary>
    /// Deletes a directory tree an untrusted account controls (a server's), with the agent's own rights. Each entry is
    /// opened relative to the handle of the folder it was listed in, by the very name listed, without following a link,
    /// and deleted through that handle: no path is ever looked up again, so a folder swapped for a junction meanwhile
    /// never takes the deletion outside, and names a Windows path would change ("name.", "name ") are deleted as they
    /// are. Links are removed themselves. Depth is not limited (the walk keeps its own stack). Nothing to do when it does
    /// not exist.
    /// </summary>
    /// <param name="beforeListing">Tests: runs before each directory's entries are listed (to swap folders meanwhile).</param>
    internal static void DeleteDirectoryTree(string directory, Action<string>? beforeListing = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(full) ?? throw new ArgumentException("A drive root cannot be deleted.", nameof(directory));
        if (!Directory.Exists(parent))
        {
            return;
        }

        using var root = ServerRoot.Open(parent);
        var name = Path.GetFileName(full);
        if (OpenChild(root.Handle, name, DeleteAccess | ReadAttributes | Synchronize, name) is { } top)
        {
            DeleteTree(top, name, beforeListing);
        }
    }

    /// <summary>
    /// Deletes what <paramref name="top"/> is, and everything below it when it is a folder (its handle is taken over).
    /// Each entry is opened relative to the folder it was listed in, by the name listed: see <see cref="DeleteDirectoryTree"/>.
    /// A folder's entries are read a batch at a time and deleted before the next batch is read: a folder of millions of
    /// entries is never held whole.
    /// </summary>
    private static void DeleteTree(SafeFileHandle top, string path, Action<string>? beforeListing = null)
    {
        var stack = new Stack<Deleting>();
        stack.Push(new Deleting(top, path));
        var buffer = new byte[ListingBuffer];
        try
        {
            while (stack.TryPeek(out var current))
            {
                if (current.Listing is null)
                {
                    if (Describe(current.Handle).Kind != FileEntryKind.Directory)
                    {
                        Finish(stack);
                        continue;
                    }

                    beforeListing?.Invoke(current.Path);
                    // Deleting needs only the right to delete each entry; listing a folder also needs the right to read
                    // it: the same folder, opened again through its own handle.
                    current.Listing = OpenChild(current.Handle, string.Empty, ListDirectory | Synchronize, current.Path) ?? throw NotFound(current.Path);
                }

                if (current.Next == current.Children.Count)
                {
                    // The entries read so far are deleted: the listing goes on after them.
                    current.Children.Clear();
                    current.Next = 0;
                    if (!ReadEntries(current.Listing, current.Path, current.Restart, buffer, current.Children))
                    {
                        Finish(stack);
                        continue;
                    }

                    current.Restart = false;
                }

                if (current.Next < current.Children.Count)
                {
                    var child = current.Children[current.Next++];
                    var childPath = ServerFilePath.Join(current.Path, child.Name);
                    if (OpenChild(current.Handle, child.Name, DeleteAccess | ReadAttributes | Synchronize, childPath) is { } handle)
                    {
                        stack.Push(new Deleting(handle, childPath));
                    }
                }
            }
        }
        finally
        {
            while (stack.TryPop(out var left))
            {
                left.Dispose();
            }
        }

        static void Finish(Stack<Deleting> stack)
        {
            using var done = stack.Pop();
            done.Listing?.Dispose();
            MarkDeleted(done.Handle, done.Path);
        }
    }

    /// <summary>
    /// Opens <paramref name="name"/> (one name, as listed; empty: the directory itself again) inside the directory
    /// <paramref name="directory"/> is: never through a path, never following a link. Null when it is gone.
    /// <paramref name="disposition"/> and <paramref name="options"/> (added to the defaults) create a file instead.
    /// </summary>
    private static unsafe SafeFileHandle? OpenChild(SafeFileHandle directory, string name, uint access, string path, uint disposition = NtFile.FileOpen,
        uint options = 0)
    {
        var added = false;
        directory.DangerousAddRef(ref added);
        try
        {
            fixed (char* buffer = name)
            {
                var objectName = new NtFile.UnicodeString
                {
                    Length = (ushort)(name.Length * sizeof(char)),
                    MaximumLength = (ushort)(name.Length * sizeof(char)),
                    Buffer = buffer,
                };
                var attributes = new NtFile.ObjectAttributes
                {
                    Length = (uint)sizeof(NtFile.ObjectAttributes),
                    RootDirectory = directory.DangerousGetHandle(),
                    ObjectName = &objectName,
                    Attributes = NtFile.ObjCaseInsensitive,
                };
                var status = NtFile.NtCreateFile(out var handle, access, &attributes, out _, null, 0,
                    (uint)(FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE), disposition,
                    NtFile.FileOpenReparsePoint | NtFile.FileOpenForBackupIntent | NtFile.FileSynchronousIoNonAlert | options, null, 0);
                if (status >= 0)
                {
                    return handle;
                }

                handle.Dispose();
                var error = (WIN32_ERROR)NtFile.RtlNtStatusToDosError(status);
                return error is WIN32_ERROR.ERROR_FILE_NOT_FOUND or WIN32_ERROR.ERROR_PATH_NOT_FOUND ? null : throw Failure(path, error);
            }
        }
        finally
        {
            if (added)
            {
                directory.DangerousRelease();
            }
        }
    }

    /// <summary>An entry being deleted: its handle, and for a folder the handle it is listed through and the batch of its entries being deleted.</summary>
    private sealed class Deleting(SafeFileHandle handle, string path) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;

        public string Path { get; } = path;

        public SafeFileHandle? Listing { get; set; }

        public bool Restart { get; set; } = true;

        public List<FileEntry> Children { get; } = [];

        public int Next { get; set; }

        public void Dispose()
        {
            Listing?.Dispose();
            Handle.Dispose();
        }
    }

    /// <summary>Deletes what the handle is (a link, never its target) as soon as possible, read-only or not.</summary>
    private static void MarkDeleted(SafeFileHandle handle, string path)
    {
        if (HandleFileOperations.MarkDeleted(handle) is var error and not WIN32_ERROR.ERROR_SUCCESS)
        {
            throw Failure(path, error);
        }
    }

    /// <summary>Renames what the handle is inside the checked directory (<see cref="HandleFileOperations.Rename"/>).</summary>
    private static void Rename(SafeFileHandle handle, SafeFileHandle directory, string name, bool replace, string path)
    {
        var status = HandleFileOperations.Rename(handle, directory, name, replace);
        if (status < 0)
        {
            throw Failure(path, (WIN32_ERROR)NtFile.RtlNtStatusToDosError(status));
        }
    }

    /// <summary>
    /// The entries of an opened directory, read from its handle as they are consumed: a folder of millions of entries
    /// is never held whole (the handle must stay open while enumerating).
    /// </summary>
    internal static IEnumerable<FileEntry> Enumerate(SafeFileHandle directory, string path)
    {
        var buffer = new byte[ListingBuffer];
        var batch = new List<FileEntry>();
        for (var restart = true; ReadEntries(directory, path, restart, buffer, batch); restart = false)
        {
            foreach (var entry in batch)
            {
                yield return entry;
            }

            batch.Clear();
        }
    }

    /// <summary>
    /// Adds the next entries of an opened directory (the first ones when <paramref name="restart"/>), as many as one
    /// <paramref name="buffer"/> holds, to <paramref name="entries"/>; false when there are no more.
    /// </summary>
    private static unsafe bool ReadEntries(SafeFileHandle directory, string path, bool restart, byte[] buffer, List<FileEntry> entries)
    {
        var infoClass = restart ? FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryRestartInfo : FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryInfo;
        fixed (byte* pointer = buffer)
        {
            if (!PInvoke.GetFileInformationByHandleEx((HANDLE)directory.DangerousGetHandle(), infoClass, pointer, (uint)buffer.Length))
            {
                var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
                if (error == WIN32_ERROR.ERROR_NO_MORE_FILES)
                {
                    return false;
                }

                throw Failure(path, error);
            }
        }

        // FILE_FULL_DIR_INFO: next entry offset, file index, four times, end of file, allocation size, attributes,
        // name length in bytes, EA size (the reparse tag for reparse points), name.
        var offset = 0;
        while (true)
        {
            var entry = buffer.AsSpan(offset);
            var next = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var lastWrite = BinaryPrimitives.ReadInt64LittleEndian(entry[24..]);
            var size = BinaryPrimitives.ReadInt64LittleEndian(entry[40..]);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(entry[60..]);
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(entry[64..]);
            var name = new string(MemoryMarshal.Cast<byte, char>(entry.Slice(68, nameLength)));
            if (name is not ("." or ".."))
            {
                var kind = Kind(attributes, tag);
                entries.Add(new FileEntry(name, kind, kind == FileEntryKind.File ? size : 0, Time(lastWrite)));
            }

            if (next == 0)
            {
                return true;
            }

            offset += next;
        }
    }

    private static FileEntryKind Kind(uint attributes, uint reparseTag)
    {
        // Name surrogates (junctions, symbolic links) point elsewhere; other reparse points (deduplicated or compressed
        // files, cloud placeholders) are ordinary files and folders to whoever reads them.
        if ((attributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && (reparseTag & NameSurrogate) != 0)
        {
            return FileEntryKind.Link;
        }

        return (attributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY) != 0 ? FileEntryKind.Directory : FileEntryKind.File;
    }

    private static unsafe Description Describe(SafeFileHandle handle)
    {
        BY_HANDLE_FILE_INFORMATION info;
        FILE_ATTRIBUTE_TAG_INFO tag;
        if (!PInvoke.GetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), &info)
            || !PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &tag,
                (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            throw Win32Failure("(opened entry)");
        }

        var kind = Kind(info.dwFileAttributes, tag.ReparseTag);
        var size = kind == FileEntryKind.File ? ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow : 0;
        var lastWrite = ((long)(uint)info.ftLastWriteTime.dwHighDateTime << 32) | (uint)info.ftLastWriteTime.dwLowDateTime;
        var reparse = (info.dwFileAttributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        return new Description(kind, size, Time(lastWrite), reparse && kind != FileEntryKind.Link);
    }

    // A game's account can set a time NTFS keeps but a DateTime cannot hold (beyond year 9999): it reads as the latest time there is.
    private static DateTimeOffset Time(long fileTime) =>
        new(DateTime.FromFileTimeUtc(Math.Clamp(fileTime, 0, DateTime.MaxValue.ToFileTimeUtc())), TimeSpan.Zero);

    private static FileEntryResult Result(string path, Description info) =>
        new(new FileEntry(ServerFilePath.Name(path), info.Kind, info.Size, info.ModifiedAt), FileVersion.Of(info.Size, info.ModifiedAt));

    private static FileManagerException NotFound(string path) => new(FileErrors.NotFound, $"'{path}' does not exist.");

    private static FileManagerException NotADirectory(string path, FileEntryKind kind) => kind == FileEntryKind.Link
        ? new FileManagerException(FileErrors.Link, $"'{path}' is a junction or link; it is not followed.")
        : new FileManagerException(FileErrors.NotADirectory, $"'{path}' is not a directory.");

    private static FileManagerException Win32Failure(string path) => Failure(path, (WIN32_ERROR)Marshal.GetLastPInvokeError());

    private static FileManagerException Failure(string path, WIN32_ERROR error)
    {
        var message = new Win32Exception((int)error).Message;
        var code = error switch
        {
            WIN32_ERROR.ERROR_FILE_NOT_FOUND or WIN32_ERROR.ERROR_PATH_NOT_FOUND => FileErrors.NotFound,
            WIN32_ERROR.ERROR_ACCESS_DENIED => FileErrors.AccessDenied,
            WIN32_ERROR.ERROR_SHARING_VIOLATION or WIN32_ERROR.ERROR_LOCK_VIOLATION or WIN32_ERROR.ERROR_DIR_NOT_EMPTY => FileErrors.InUse,
            WIN32_ERROR.ERROR_FILE_EXISTS or WIN32_ERROR.ERROR_ALREADY_EXISTS => FileErrors.Exists,
            WIN32_ERROR.ERROR_DISK_FULL or WIN32_ERROR.ERROR_HANDLE_DISK_FULL or WIN32_ERROR.ERROR_DISK_QUOTA_EXCEEDED => FileErrors.DiskFull,
            _ => FileErrors.Failed,
        };
        return new FileManagerException(code, $"'{path}': {message}", new Win32Exception((int)error));
    }

    private readonly record struct Description(FileEntryKind Kind, long Size, DateTimeOffset ModifiedAt, bool PlainReparsePoint);

    /// <summary>A server directory opened for one operation, and the checks every path below it goes through.</summary>
    private sealed class ServerRoot : IDisposable
    {
        private readonly string _path;
        private readonly string _final;
        private readonly SafeFileHandle _handle;

        private ServerRoot(string path, SafeFileHandle handle, string final) => (_path, _handle, _final) = (path, handle, final);

        public static ServerRoot Open(string directory, bool write = true)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

            // The server directory itself may be reached through a junction an administrator set up (the servers root on
            // another drive, say): it is followed; only what lies below it is the server's.
            var access = ListDirectory | ReadAttributes | Synchronize | (write ? AddFile | AddSubdirectory : 0);
            var handle = OpenRaw(path, access, FILE_CREATION_DISPOSITION.OPEN_EXISTING, follow: true)
                ?? throw new FileManagerException(FileErrors.NotFound, "The server directory does not exist.");
            try
            {
                return new ServerRoot(path, handle, FinalPath(handle));
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>The absolute path of <paramref name="path"/>, after the prefix check (layer 2), in long form.</summary>
        public string Full(string path)
        {
            if (path.Length == 0)
            {
                return Long(_path);
            }

            var relative = path.Replace('/', '\\');
            var full = Path.Join(_path, relative);
            // GetFullPath leaves a path in the \\?\ form (a snapshot's) as it is: then the relative part is checked alone.
            // Defence in depth, untested on purpose: every path reaching here passed ServerFilePath (Validate, or Walk's
            // IsValid), which already refuses all GetFullPath would change ('.', '..', empty segments, trailing dots and
            // spaces, '\', ':', device names), so no input reaches either branch's refusal.
            var normalized = _path.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? string.Equals(Path.GetFullPath(Path.Join(@"C:\pulsar", relative)), Path.Join(@"C:\pulsar", relative), StringComparison.Ordinal)
                : string.Equals(Path.GetFullPath(full), full, StringComparison.Ordinal);
            if (!normalized || !full.StartsWith(_path + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new FileManagerException(FileErrors.OutsideServer, $"'{path}' resolves outside the server directory.");
            }

            return Long(full);
        }

        /// <summary>
        /// Opens the entry itself (a link is not followed) and checks where the handle really is; null when it does not
        /// exist. <paramref name="dataAccess"/> reopens a plain reparse point (a deduplicated file) for its content.
        /// </summary>
        public Opened? TryOpen(string path, uint access, bool dataAccess = false)
        {
            if (path.Length == 0)
            {
                throw new FileManagerException(FileErrors.InvalidPath, "The server directory itself cannot be the target.");
            }

            var full = Full(path);
            var handle = OpenRaw(full, access, FILE_CREATION_DISPOSITION.OPEN_EXISTING, follow: false);
            if (handle is null)
            {
                return null;
            }

            try
            {
                Check(handle, path);
                if (dataAccess && Describe(handle).PlainReparsePoint)
                {
                    handle.Dispose();
                    handle = OpenRaw(full, access, FILE_CREATION_DISPOSITION.OPEN_EXISTING, follow: true) ?? throw NotFound(path);
                    Check(handle, path);
                }

                return new Opened(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>Opens a directory (<see cref="ServerFilePath.Root"/>: the server directory) to list it or create in it.</summary>
        public SafeFileHandle OpenDirectory(string path, uint access)
        {
            if (path.Length == 0)
            {
                return OpenRaw(Long(_path), access, FILE_CREATION_DISPOSITION.OPEN_EXISTING, follow: true)
                    ?? throw new FileManagerException(FileErrors.NotFound, "The server directory does not exist.");
            }

            using var opened = TryOpen(path, access) ?? throw new FileManagerException(FileErrors.NotFound, $"The folder '{path}' does not exist.");
            var kind = Describe(opened.Handle).Kind;
            if (kind != FileEntryKind.Directory)
            {
                throw NotADirectory(path, kind);
            }

            return opened.Detach();
        }

        /// <summary>
        /// Creates a new file (never opening an existing one or a link) in <paramref name="directory"/>, its folder as
        /// <see cref="OpenDirectory"/> opened and checked it: through that handle, not by path, so a folder on the path
        /// swapped for a junction since is not followed. Should the file still be outside (the folder itself moved away
        /// meanwhile), it is deleted through its own handle.
        /// </summary>
        public SafeFileHandle Create(SafeFileHandle directory, string path, uint access)
        {
            var handle = OpenChild(directory, ServerFilePath.Name(path), access, path, NtFile.FileCreate, NtFile.FileNonDirectoryFile)
                ?? throw NotFound(ServerFilePath.Parent(path));
            try
            {
                Check(handle, path);
                return handle;
            }
            catch
            {
                try
                {
                    MarkDeleted(handle, path);
                }
                catch (FileManagerException)
                {
                    // The file stays (named as a temporary one): nothing else depends on it.
                }

                handle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Checks that a folder <see cref="OpenDirectory"/> opened earlier is still where it was: below the server
        /// directory (or, for <see cref="ServerFilePath.Root"/>, the server directory itself).
        /// </summary>
        public void CheckDirectory(SafeFileHandle directory, string path)
        {
            if (path.Length > 0)
            {
                Check(directory, path);
            }
            else if (!FinalPath(directory).Equals(_final, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileManagerException(FileErrors.OutsideServer, "The server directory has moved.");
            }
        }

        /// <summary>The directory's handle, for what is opened relative to it.</summary>
        public SafeFileHandle Handle => _handle;

        public void Dispose() => _handle.Dispose();

        private void Check(SafeFileHandle handle, string path)
        {
            var final = FinalPath(handle);
            if (!Below(final, _final))
            {
                throw new FileManagerException(FileErrors.OutsideServer, $"'{path}' resolves outside the server directory.");
            }

            // The lent Steam login, told by where the handle is rather than by the name asked for: a short name
            // (PULSAR~1) or a junction the server's account made inside the server leads there under another one.
            var login = _final + "\\" + ServerFileRules.SteamLoginFolder.Replace('/', '\\');
            if (final.Equals(login, StringComparison.OrdinalIgnoreCase) || Below(final, login))
            {
                throw ServerFileRules.SteamLoginRefused(path);
            }
        }

        private static bool Below(string path, string directory) =>
            path.Length > directory.Length + 1 && path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) && path[directory.Length] == '\\';

        private static string Long(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

        private static unsafe SafeFileHandle? OpenRaw(string path, uint access, FILE_CREATION_DISPOSITION disposition, bool follow)
        {
            var flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | (follow ? 0 : FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT);
            var handle = PInvoke.CreateFile(path, access, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null, disposition, flags, null);
            if (!handle.IsInvalid)
            {
                return handle;
            }

            var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (disposition == FILE_CREATION_DISPOSITION.OPEN_EXISTING && error is WIN32_ERROR.ERROR_FILE_NOT_FOUND or WIN32_ERROR.ERROR_PATH_NOT_FOUND)
            {
                return null;
            }

            throw Failure(Path.GetFileName(path), error);
        }

        private static unsafe string FinalPath(SafeFileHandle handle)
        {
            var buffer = new char[32 * 1024];
            uint length;
            fixed (char* pointer = buffer)
            {
                length = PInvoke.GetFinalPathNameByHandle((HANDLE)handle.DangerousGetHandle(), pointer, (uint)buffer.Length,
                    GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
                if (length == 0)
                {
                    // A snapshot's volume has no drive letter: its device name (\Device\HarddiskVolumeShadowCopy7\...) does.
                    length = PInvoke.GetFinalPathNameByHandle((HANDLE)handle.DangerousGetHandle(), pointer, (uint)buffer.Length,
                        GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED | GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_NT);
                }
            }

            if (length == 0 || length >= buffer.Length)
            {
                throw new FileManagerException(FileErrors.Failed, "Cannot resolve where a server file is.");
            }

            var path = new string(buffer, 0, (int)length);
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + path[8..];
            }

            return Path.TrimEndingDirectorySeparator(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
        }
    }

    /// <summary>An opened, checked entry; <see cref="Detach"/> hands its handle over.</summary>
    private sealed class Opened(SafeFileHandle handle) : IDisposable
    {
        private SafeFileHandle? _handle = handle;

        public SafeFileHandle Handle => _handle ?? throw new ObjectDisposedException(nameof(Opened));

        public SafeFileHandle Detach()
        {
            var handle = Handle;
            _handle = null;
            return handle;
        }

        public void Dispose() => _handle?.Dispose();
    }

    private sealed class Pending : PendingFile
    {
        private readonly WindowsServerFileManager _owner;
        private readonly ProvisionedServer _server;
        private readonly string _path;
        private readonly long? _size;
        private readonly bool _overwrite;
        private readonly string? _expectedVersion;
        private readonly SafeFileHandle _directory;
        private readonly FileStream _stream;
        private bool _committed;

        /// <param name="size">The exact length expected; null: whatever is written (a streamed upload, bounded by the caller).</param>
        public Pending(WindowsServerFileManager owner, ProvisionedServer server, string path, long? size, bool overwrite, string? expectedVersion,
            SafeFileHandle directory, SafeFileHandle handle)
        {
            (_owner, _server, _path, _size, _overwrite, _expectedVersion, _directory) = (owner, server, path, size, overwrite, expectedVersion, directory);
            _stream = new FileStream(handle, FileAccess.Write, bufferSize: 0);
            try
            {
                // Reserves the space now: a full disk fails before the transfer, not after most of it. A streamed upload
                // has no length to reserve.
                if (size is { } length)
                {
                    _stream.SetLength(length);
                }
            }
            catch (IOException ex)
            {
                Abandon();
                throw ServerFileRules.Translate(path, ex) ?? new FileManagerException(FileErrors.Failed, ex.Message, ex);
            }
        }

        public override Stream Content => _stream;

        public override FileEntryResult Commit()
        {
            if (_size is not null && _stream.Position != _size)
            {
                throw new FileManagerException(FileErrors.TransferFailed, $"Received {_stream.Position} of {_size} bytes.");
            }

            _stream.Flush();
            var result = _owner.Run(_server, root =>
            {
                // The upload goes into the folder checked when it began, wherever that folder is now: checked again. NTFS
                // refuses to move a folder while a file in it is open; this does not rely on it.
                root.CheckDirectory(_directory, ServerFilePath.Parent(_path));
                ServerFileRules.CheckTarget(_path, Existing(root, _path), _overwrite, _expectedVersion);
                Rename(_stream.SafeFileHandle, _directory, ServerFilePath.Name(_path), replace: _overwrite || _expectedVersion is not null, _path);
                // At once: from here the file is at its target, and anything failing below must not make Dispose abandon
                // (delete) the upload, which would delete the file just put in place.
                _committed = true;
                _owner.AfterRename?.Invoke();
                return Result(_path, Describe(_stream.SafeFileHandle));
            });
            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            if (!_committed)
            {
                Abandon();
            }
            else
            {
                _stream.Dispose();
            }

            _directory.Dispose();
        }

        private void Abandon()
        {
            try
            {
                MarkDeleted(_stream.SafeFileHandle, _path);
            }
            catch (FileManagerException)
            {
                // The temporary file stays (named as one): nothing else depends on it.
            }

            _stream.Dispose();
        }
    }

    /// <summary>
    /// A file written at its end through the handle opened and checked as the server's account: what was written stays,
    /// committed or not.
    /// </summary>
    private sealed class Appending : PendingFile
    {
        private readonly string _path;
        private readonly FileStream _stream;

        public Appending(string path, SafeFileHandle handle)
        {
            _path = path;
            try
            {
                _stream = new FileStream(handle, FileAccess.Write, bufferSize: 0);
                _stream.Seek(0, SeekOrigin.End);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public override Stream Content => _stream;

        public override FileEntryResult Commit()
        {
            _stream.Flush();
            return Result(_path, Describe(_stream.SafeFileHandle));
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

internal static unsafe partial class NtFile
{
    public const int FileRenameInformation = 10;
    public const int FileRenameInformationEx = 65;

    public const uint FileRenameReplaceIfExists = 0x1;
    public const uint FileRenamePosixSemantics = 0x2;
    public const uint FileRenameIgnoreReadOnlyAttribute = 0x40;

    public const int StatusInvalidParameter = unchecked((int)0xC000000D);
    public const int StatusNotSupported = unchecked((int)0xC00000BB);
    public const int StatusInvalidInfoClass = unchecked((int)0xC0000003);
    public const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    public const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    public const int StatusNotADirectory = unchecked((int)0xC0000103);

    [LibraryImport("ntdll.dll")]
    public static partial int NtSetInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, byte* fileInformation, uint length, int fileInformationClass);

    [LibraryImport("ntdll.dll")]
    public static partial uint RtlNtStatusToDosError(int status);

    // NtCreateFile: dispositions, options and object attributes.
    public const uint FileOpen = 1;
    public const uint FileCreate = 2;
    public const uint FileOpenIf = 3;
    public const uint FileDirectoryFile = 0x1;
    public const uint FileSynchronousIoNonAlert = 0x20;
    public const uint FileNonDirectoryFile = 0x40;
    public const uint FileOpenForBackupIntent = 0x4000;
    public const uint FileOpenReparsePoint = 0x200000;
    public const uint ObjCaseInsensitive = 0x40;

    /// <summary>Opens or creates <paramref name="objectAttributes"/>'s name relative to its root directory handle.</summary>
    [LibraryImport("ntdll.dll")]
    public static partial int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess, ObjectAttributes* objectAttributes, out IoStatusBlock ioStatusBlock,
        long* allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, void* eaBuffer, uint eaLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public void* SecurityDescriptor;
        public void* SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }
}
