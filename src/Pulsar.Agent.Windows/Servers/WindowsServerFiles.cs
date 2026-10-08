using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// The agent's own file access inside server directories. Each file is opened without following a final link, and the
/// path the opened handle really has is checked to be under the server directory: whatever junctions the server's
/// account plants or swaps, the agent only ever touches the file it meant to. Writes replace the file (delete, then
/// create a new one) so a hard link never turns a write into someone else's file.
/// </summary>
public sealed class WindowsServerFiles : IServerFiles
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributesAccess = 0x00000080;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint SynchronizeAccess = 0x00100000;

    // List, add files and folders, traverse, read attributes: what opening and creating below a folder needs.
    private const uint DirectoryAccess = 0x1 | 0x2 | 0x4 | 0x20 | ReadAttributesAccess | SynchronizeAccess;

    /// <summary>Tests: runs just before a written file is created (to swap folders in the meantime).</summary>
    internal Action? BeforeCreate { get; init; }

    public Stream? OpenRead(string serverRoot, string relativePath)
    {
        var (root, path) = Paths(serverRoot, relativePath);
        var handle = Open(path, GenericRead, FILE_CREATION_DISPOSITION.OPEN_EXISTING, allowMissing: true);
        if (handle is null)
        {
            return null;
        }

        try
        {
            Check(handle, root, relativePath);
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Write(string serverRoot, string relativePath, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (root, _) = Paths(serverRoot, relativePath);
        var separator = relativePath.LastIndexOf('/');
        using var directory = OpenDirectories(serverRoot, root, separator < 0 ? "" : relativePath[..separator]);
        Delete(serverRoot, relativePath);
        BeforeCreate?.Invoke();

        // Created through the handle of the folder just checked, not by its path: a folder on the path swapped for a
        // junction since then is not followed. Should the file still end up elsewhere (the checked folder itself moved
        // away), it is deleted through its own handle.
        using var handle = OpenRelative(directory, relativePath[(separator + 1)..], GenericWrite | DeleteAccess | ReadAttributesAccess | SynchronizeAccess, NtFile.FileCreate,
            NtFile.FileNonDirectoryFile | NtFile.FileSynchronousIoNonAlert | NtFile.FileOpenReparsePoint, relativePath);
        try
        {
            Check(handle, root, relativePath);
        }
        catch
        {
            MarkDeleted(handle);
            throw;
        }

        using var stream = new FileStream(handle, FileAccess.Write);
        content.CopyTo(stream);
    }

    public void Delete(string serverRoot, string relativePath)
    {
        var (root, path) = Paths(serverRoot, relativePath);
        using var handle = Open(path, DeleteAccess, FILE_CREATION_DISPOSITION.OPEN_EXISTING, allowMissing: true, directory: true);
        if (handle is null)
        {
            return;
        }

        // The handle is the link itself when the name is one (backup semantics open a directory junction too): deleting
        // it never touches a target.
        CheckLocation(handle, root, relativePath);
        var disposition = new FILE_DISPOSITION_INFO { DeleteFile = true };
        unsafe
        {
            if (!PInvoke.SetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfo, &disposition, (uint)sizeof(FILE_DISPOSITION_INFO)))
            {
                throw Failure("files.delete_failed", relativePath);
            }
        }
    }

    public bool DeleteEmptyDirectory(string serverRoot, string relativePath)
    {
        var (root, path) = Paths(serverRoot, relativePath);
        using var handle = Open(path, DeleteAccess | ReadAttributesAccess, FILE_CREATION_DISPOSITION.OPEN_EXISTING, allowMissing: true, directory: true);
        if (handle is null)
        {
            return false;
        }

        FILE_ATTRIBUTE_TAG_INFO info;
        unsafe
        {
            if (!PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
            {
                throw Failure("files.open_failed", relativePath);
            }
        }

        // Only a real directory: a link in its place is left alone.
        if ((info.FileAttributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT) != 0
            || (info.FileAttributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            return false;
        }

        CheckLocation(handle, root, relativePath);
        var disposition = new FILE_DISPOSITION_INFO { DeleteFile = true };
        unsafe
        {
            if (PInvoke.SetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfo, &disposition, (uint)sizeof(FILE_DISPOSITION_INFO)))
            {
                return true;
            }
        }

        return (WIN32_ERROR)Marshal.GetLastPInvokeError() == WIN32_ERROR.ERROR_DIR_NOT_EMPTY ? false : throw Failure("files.delete_failed", relativePath);
    }

    public void CreateDirectory(string serverRoot, string relativePath)
    {
        var (root, _) = Paths(serverRoot, relativePath);
        using var directory = OpenDirectories(serverRoot, root, relativePath);
    }

    private static (string Root, string Path) Paths(string serverRoot, string relativePath)
    {
        if (!Pulsar.Manifests.ManifestValidator.IsValidRelativePath(relativePath))
        {
            throw new GameProcessException("files.invalid_path", $"'{relativePath}' is not a valid path inside the server directory.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverRoot));
        return (FinalPath(root), Path.Combine(root, relativePath.Replace('/', '\\')));
    }

    /// <summary>
    /// Opens the folders of <paramref name="relativeDirectory"/> one below the other, creating the missing ones, each
    /// through the handle of the one above it and checked before going below it (a junction would take the next level
    /// elsewhere). Returns the last one ("" is the server directory).
    /// </summary>
    private static SafeFileHandle OpenDirectories(string serverRoot, string root, string relativeDirectory)
    {
        var current = OpenRoot(serverRoot);
        try
        {
            var walked = "";
            foreach (var part in relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                walked = walked.Length == 0 ? part : walked + "/" + part;
                var next = OpenRelative(current, part, DirectoryAccess, NtFile.FileOpenIf,
                    NtFile.FileDirectoryFile | NtFile.FileOpenForBackupIntent | NtFile.FileOpenReparsePoint, walked);
                current.Dispose();
                current = next;
                Check(current, root, walked);
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>The server directory itself, following a junction an administrator may have put on its path.</summary>
    private static unsafe SafeFileHandle OpenRoot(string serverRoot, uint access = DirectoryAccess)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverRoot));
        var handle = PInvoke.CreateFile(path, access,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new GameProcessException("files.open_failed", $"Cannot open the server directory: {new Win32Exception(error).Message}");
        }

        return handle;
    }

    /// <summary>Opens or creates <paramref name="name"/> (one path segment) inside the directory <paramref name="directory"/> is.</summary>
    private static unsafe SafeFileHandle OpenRelative(SafeFileHandle directory, string name, uint access, uint disposition, uint options, string relativePath)
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
                var status = NtFile.NtCreateFile(out var handle, access, &attributes, out _, null, (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                    (uint)(FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE), disposition, options, null, 0);
                if (status >= 0)
                {
                    return handle;
                }

                handle.Dispose();
                var error = (int)NtFile.RtlNtStatusToDosError(status);
                throw new GameProcessException("files.open_failed", $"Cannot open '{relativePath}': {new Win32Exception(error).Message}");
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

    /// <summary>Deletes what the handle is, at once (the file a refused write had just created).</summary>
    private static unsafe void MarkDeleted(SafeFileHandle handle)
    {
        var disposition = new FILE_DISPOSITION_INFO { DeleteFile = true };
        _ = PInvoke.SetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfo, &disposition,
            (uint)sizeof(FILE_DISPOSITION_INFO));
    }

    private static unsafe SafeFileHandle? Open(string path, uint access, FILE_CREATION_DISPOSITION disposition, bool allowMissing, bool directory = false)
    {
        var flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT | (directory ? FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS : 0);
        var handle = PInvoke.CreateFile(path, access,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null, disposition, flags, null);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (allowMissing && error is WIN32_ERROR.ERROR_FILE_NOT_FOUND or WIN32_ERROR.ERROR_PATH_NOT_FOUND)
        {
            return null;
        }

        throw new GameProcessException("files.open_failed", $"Cannot open {Path.GetFileName(path)}: {new Win32Exception((int)error).Message}");
    }

    /// <summary>The opened object is a plain file or directory under the server directory.</summary>
    private static unsafe void Check(SafeFileHandle handle, string root, string relativePath)
    {
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            throw Failure("files.open_failed", relativePath);
        }

        if ((info.FileAttributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            throw new GameProcessException("files.reparse_point", $"'{relativePath}' is a junction or link; the agent does not go through one.");
        }

        CheckLocation(handle, root, relativePath);
    }

    private static void CheckLocation(SafeFileHandle handle, string root, string relativePath)
    {
        var final = FinalPath(handle);
        if (!(final.Length > root.Length + 1 && final.StartsWith(root, StringComparison.OrdinalIgnoreCase) && final[root.Length] == '\\'))
        {
            throw new GameProcessException("files.outside_server", $"'{relativePath}' resolves outside the server directory.");
        }
    }

    private static string FinalPath(string directory)
    {
        // The server directory itself may be reached through a junction an administrator set up (the servers root on
        // another drive, say): it is followed, and only what lies below where it leads is the server's.
        using var handle = OpenRoot(directory, ReadAttributesAccess);
        return FinalPath(handle);
    }

    private static unsafe string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint length;
        fixed (char* pointer = buffer)
        {
            length = PInvoke.GetFinalPathNameByHandle((HANDLE)handle.DangerousGetHandle(), pointer, (uint)buffer.Length,
                GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
        }

        if (length == 0 || length >= buffer.Length)
        {
            throw new GameProcessException("files.open_failed", "Cannot resolve the final path of a server file.");
        }

        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return Path.TrimEndingDirectorySeparator(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
    }

    private static GameProcessException Failure(string code, string relativePath) =>
        new(code, $"'{relativePath}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
}
