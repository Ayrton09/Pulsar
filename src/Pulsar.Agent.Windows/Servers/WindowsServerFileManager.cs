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
/// really has is checked against the server directory (layer 3); the operation then works on that handle: listing,
/// reading, renaming and deleting never go back to a path a server could have swapped. Opening, creating, renaming
/// and deleting run as the server's account (layer 4), so Windows itself refuses anything outside what that account
/// may touch, whatever a hard link or a race points at. Reading and writing content happens on the opened handles,
/// outside the impersonation.
/// </summary>
public sealed class WindowsServerFileManager(IServerImpersonation impersonation) : IServerFileManager
{
    private const uint ListDirectory = 0x0001;
    private const uint AddFile = 0x0002;
    private const uint AddSubdirectory = 0x0004;
    private const uint ReadAttributes = 0x0080;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint NameSurrogate = 0x20000000;
    private const int MaxDepth = 256;

    public FileListResult List(ProvisionedServer server, string path)
    {
        ServerFileRules.Validate(path, allowRoot: true);
        return Run(server, root =>
        {
            using var directory = root.OpenDirectory(path, ListDirectory | ReadAttributes | Synchronize);
            return ServerFileRules.Listing(Enumerate(directory, path));
        });
    }

    public void CreateDirectory(ProvisionedServer server, string path) => MakeDirectories(server, path, mustBeNew: true);

    public void EnsureDirectory(ProvisionedServer server, string path) => MakeDirectories(server, path, mustBeNew: false);

    public IReadOnlyList<TreeEntry> Walk(ProvisionedServer server) => Run(server, root =>
    {
        var entries = new List<TreeEntry>();
        WalkInto(root, ServerFilePath.Root, entries, 0);
        return entries;
    });

    /// <summary>Lists each directory from its own checked handle; links are recorded, never opened as directories.</summary>
    private static void WalkInto(ServerRoot root, string path, List<TreeEntry> entries, int depth)
    {
        List<FileEntry> children;
        using (var directory = root.OpenDirectory(path, ListDirectory | ReadAttributes | Synchronize))
        {
            children = Enumerate(directory, path);
        }

        foreach (var child in children)
        {
            var childPath = ServerFilePath.Join(path, child.Name);
            if (!ServerFilePath.IsValid(childPath))
            {
                continue;
            }

            entries.Add(new TreeEntry(childPath, child.Kind, child.Size, child.ModifiedAt));
            ServerFileRules.CheckWalk(entries.Count, depth);
            if (child.Kind == FileEntryKind.Directory)
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

    public void Move(ProvisionedServer server, string path, string destination)
    {
        ServerFileRules.CheckMove(path, destination);
        Run(server, root =>
        {
            using var source = root.TryOpen(path, DeleteAccess | ReadAttributes | Synchronize) ?? throw NotFound(path);
            using var directory = root.OpenDirectory(ServerFilePath.Parent(destination), ListDirectory | AddFile | AddSubdirectory | ReadAttributes | Synchronize);
            Rename(source.Handle, directory, ServerFilePath.Name(destination), replace: false, destination);
            return 0;
        });
    }

    public void Delete(ProvisionedServer server, string path)
    {
        ServerFileRules.Validate(path);
        Run(server, root =>
        {
            using var entry = root.TryOpen(path, DeleteAccess | ReadAttributes | Synchronize) ?? throw NotFound(path);
            DeleteTree(root, entry.Handle, path, 0);
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
        });
    }

    public PendingFile Create(ProvisionedServer server, string path, long size, bool overwrite, string? expectedVersion)
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
                var handle = root.Create(temporary, GenericWrite | DeleteAccess | ReadAttributes | Synchronize);
                return new Pending(this, server, path, size, overwrite, expectedVersion, directory, handle);
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        });
    }

    private T Run<T>(ProvisionedServer server, Func<ServerRoot, T> action)
    {
        ArgumentNullException.ThrowIfNull(server);
        try
        {
            return impersonation.Run(server.Identity, () =>
            {
                using var root = ServerRoot.Open(server.Directory);
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
    /// Deletes a directory tree an untrusted account controls (a server's), with the agent's own rights: as the file
    /// manager deletes, every entry is opened without following a link, checked to still be below the directory's
    /// parent, and deleted through that handle, so a folder swapped for a junction meanwhile never takes the deletion
    /// outside. Links are removed themselves. Nothing to do when it does not exist.
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
        using var entry = root.TryOpen(name, DeleteAccess | ReadAttributes | Synchronize);
        if (entry is not null)
        {
            DeleteTree(root, entry.Handle, name, 0, beforeListing);
        }
    }

    private static void DeleteTree(ServerRoot root, SafeFileHandle handle, string path, int depth, Action<string>? beforeListing = null)
    {
        if (depth > MaxDepth)
        {
            throw new FileManagerException(FileErrors.Failed, $"'{path}' is nested too deeply to delete.");
        }

        if (Describe(handle).Kind == FileEntryKind.Directory)
        {
            beforeListing?.Invoke(path);
            // Deleting needs only the right to delete each entry; listing a folder also needs the right to read it.
            List<FileEntry> children;
            using (var listing = root.TryOpen(path, ListDirectory | Synchronize) ?? throw NotFound(path))
            {
                children = Enumerate(listing.Handle, path);
            }

            foreach (var child in children)
            {
                var childPath = ServerFilePath.Join(path, child.Name);
                using var entry = root.TryOpen(childPath, DeleteAccess | ReadAttributes | Synchronize);
                if (entry is not null)
                {
                    DeleteTree(root, entry.Handle, childPath, depth + 1, beforeListing);
                }
            }
        }

        MarkDeleted(handle, path);
    }

    /// <summary>Deletes what the handle is (a link, never its target) as soon as possible, read-only or not.</summary>
    private static unsafe void MarkDeleted(SafeFileHandle handle, string path)
    {
        var flags = FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
            | FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
        var extended = new FILE_DISPOSITION_INFO_EX { Flags = flags };
        if (PInvoke.SetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfoEx, &extended,
            (uint)sizeof(FILE_DISPOSITION_INFO_EX)))
        {
            return;
        }

        // File systems without POSIX deletes (FAT, older ReFS): the classic delete, when the last handle closes.
        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        if (error is not (WIN32_ERROR.ERROR_INVALID_PARAMETER or WIN32_ERROR.ERROR_NOT_SUPPORTED or WIN32_ERROR.ERROR_INVALID_FUNCTION))
        {
            throw Failure(path, error);
        }

        var classic = new FILE_DISPOSITION_INFO { DeleteFile = true };
        if (!PInvoke.SetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfo, &classic,
            (uint)sizeof(FILE_DISPOSITION_INFO)))
        {
            throw Win32Failure(path);
        }
    }

    /// <summary>
    /// Renames what the handle is to <paramref name="name"/> inside the directory <paramref name="directory"/> is: the
    /// target folder is the one already checked, not a path looked up again. The Win32 call refuses a root directory,
    /// so this goes to the native one (FILE_RENAME_INFORMATION, the same structure).
    /// </summary>
    /// <remarks>
    /// POSIX semantics, as deletes: a file replaced while another program reads it (a virus scanner, the search
    /// indexer, a game reading its configuration) is replaced anyway, where a classic rename refuses with access
    /// denied; that program keeps reading the old content. A read-only file is replaced as it would be deleted.
    /// </remarks>
    private static void Rename(SafeFileHandle handle, SafeFileHandle directory, string name, bool replace, string path)
    {
        var flags = NtFile.FileRenamePosixSemantics | NtFile.FileRenameIgnoreReadOnlyAttribute | (replace ? NtFile.FileRenameReplaceIfExists : 0u);
        var status = SetRename(handle, directory, name, flags, NtFile.FileRenameInformationEx);

        // File systems without POSIX renames (FAT, older ReFS): the classic rename.
        if (status is NtFile.StatusInvalidParameter or NtFile.StatusNotSupported or NtFile.StatusInvalidInfoClass)
        {
            status = SetRename(handle, directory, name, replace ? 1u : 0u, NtFile.FileRenameInformation);
        }

        if (status < 0)
        {
            throw Failure(path, (WIN32_ERROR)NtFile.RtlNtStatusToDosError(status));
        }
    }

    private static unsafe int SetRename(SafeFileHandle handle, SafeFileHandle directory, string name, uint flags, int informationClass)
    {
        // FILE_RENAME_INFORMATION: ReplaceIfExists (a BOOLEAN) or, for the extended class, Flags (a ULONG) in the same
        // place; the root directory handle; the name's length in bytes; the name.
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var buffer = new byte[nameOffset + ((name.Length + 1) * sizeof(char))];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, flags);
        var added = false;
        directory.DangerousAddRef(ref added);
        try
        {
            if (IntPtr.Size == 8)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(rootOffset), directory.DangerousGetHandle());
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(rootOffset), (int)directory.DangerousGetHandle());
            }

            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lengthOffset), (uint)(name.Length * sizeof(char)));
            MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(buffer.AsSpan(nameOffset));
            fixed (byte* info = buffer)
            {
                return NtFile.NtSetInformationFile(handle, out _, info, (uint)buffer.Length, informationClass);
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

    /// <summary>The entries of an opened directory, read from its handle.</summary>
    private static unsafe List<FileEntry> Enumerate(SafeFileHandle directory, string path)
    {
        var entries = new List<FileEntry>();
        var buffer = new byte[64 * 1024];
        var infoClass = FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryRestartInfo;
        while (true)
        {
            fixed (byte* pointer = buffer)
            {
                if (!PInvoke.GetFileInformationByHandleEx((HANDLE)directory.DangerousGetHandle(), infoClass, pointer, (uint)buffer.Length))
                {
                    var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
                    if (error == WIN32_ERROR.ERROR_NO_MORE_FILES)
                    {
                        return entries;
                    }

                    throw Failure(path, error);
                }
            }

            infoClass = FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryInfo;

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
                    break;
                }

                offset += next;
            }
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

    private static DateTimeOffset Time(long fileTime) => new(DateTime.FromFileTimeUtc(Math.Max(0, fileTime)), TimeSpan.Zero);

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

        public static ServerRoot Open(string directory)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

            // The server directory itself may be reached through a junction an administrator set up (the servers root on
            // another drive, say): it is followed; only what lies below it is the server's.
            var handle = OpenRaw(path, ListDirectory | AddFile | AddSubdirectory | ReadAttributes | Synchronize, FILE_CREATION_DISPOSITION.OPEN_EXISTING, follow: true)
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

            var full = Path.Join(_path, path.Replace('/', '\\'));
            if (!string.Equals(Path.GetFullPath(full), full, StringComparison.Ordinal)
                || !full.StartsWith(_path + "\\", StringComparison.OrdinalIgnoreCase))
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

        /// <summary>Creates a new file (never opening an existing one or a link) and checks where it is.</summary>
        public SafeFileHandle Create(string path, uint access)
        {
            var handle = OpenRaw(Full(path), access, FILE_CREATION_DISPOSITION.CREATE_NEW, follow: false)!;
            try
            {
                Check(handle, path);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void Dispose() => _handle.Dispose();

        private void Check(SafeFileHandle handle, string path)
        {
            var final = FinalPath(handle);
            if (!(final.Length > _final.Length + 1 && final.StartsWith(_final, StringComparison.OrdinalIgnoreCase) && final[_final.Length] == '\\'))
            {
                throw new FileManagerException(FileErrors.OutsideServer, $"'{path}' resolves outside the server directory.");
            }
        }

        private static string Long(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

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
        private readonly long _size;
        private readonly bool _overwrite;
        private readonly string? _expectedVersion;
        private readonly SafeFileHandle _directory;
        private readonly FileStream _stream;
        private bool _committed;

        public Pending(WindowsServerFileManager owner, ProvisionedServer server, string path, long size, bool overwrite, string? expectedVersion,
            SafeFileHandle directory, SafeFileHandle handle)
        {
            (_owner, _server, _path, _size, _overwrite, _expectedVersion, _directory) = (owner, server, path, size, overwrite, expectedVersion, directory);
            _stream = new FileStream(handle, FileAccess.Write, bufferSize: 0);
            try
            {
                // Reserves the space now: a full disk fails before the transfer, not after most of it.
                _stream.SetLength(size);
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
            if (_stream.Position != _size)
            {
                throw new FileManagerException(FileErrors.TransferFailed, $"Received {_stream.Position} of {_size} bytes.");
            }

            _stream.Flush();
            var result = _owner.Run(_server, root =>
            {
                ServerFileRules.CheckTarget(_path, Existing(root, _path), _overwrite, _expectedVersion);
                Rename(_stream.SafeFileHandle, _directory, ServerFilePath.Name(_path), replace: _overwrite || _expectedVersion is not null, _path);
                return Result(_path, Describe(_stream.SafeFileHandle));
            });
            _committed = true;
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

    [LibraryImport("ntdll.dll")]
    public static partial int NtSetInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, byte* fileInformation, uint length, int fileInformationClass);

    [LibraryImport("ntdll.dll")]
    public static partial uint RtlNtStatusToDosError(int status);

    // NtCreateFile: dispositions, options and object attributes.
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
