using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// Resolves a path a manifest gives relative to the server directory (docs/architecture/06, start sequence step 1): it
/// must stay under the directory, no component below it may be a reparse point (junction or symbolic link), and the
/// file the OS finally opens must be the one under the directory.
/// </summary>
internal static class ServerPaths
{
    public static string ResolveFile(string serverRoot, string relativePath)
    {
        var root = NormalizeRoot(serverRoot);
        var full = Combine(root, relativePath);
        RejectReparsePoints(root, full);
        if (!File.Exists(full))
        {
            throw new GameProcessException("process.executable_missing", $"{relativePath} does not exist in the server directory.");
        }

        var rootFinal = FinalPath(root, directory: true);
        var fileFinal = FinalPath(full, directory: false);
        if (!IsUnder(rootFinal, fileFinal))
        {
            throw new GameProcessException("process.path_outside_server", $"{relativePath} resolves outside the server directory.");
        }

        return full;
    }

    /// <summary>A directory under the server directory (the root itself for an empty path); it must exist.</summary>
    public static string ResolveDirectory(string serverRoot, string relativePath)
    {
        var root = NormalizeRoot(serverRoot);
        var full = string.IsNullOrEmpty(relativePath) ? root : Combine(root, relativePath);
        RejectReparsePoints(root, full);
        if (!Directory.Exists(full))
        {
            throw new GameProcessException("process.directory_missing", $"{relativePath} does not exist in the server directory.");
        }

        return full;
    }

    public static string NormalizeRoot(string serverRoot)
    {
        if (!Path.IsPathFullyQualified(serverRoot))
        {
            throw new GameProcessException("process.invalid_path", "The server directory must be an absolute path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverRoot));
    }

    private static string Combine(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new GameProcessException("process.invalid_path", $"'{relativePath}' is not a relative path.");
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsUnder(root, full))
        {
            throw new GameProcessException("process.path_outside_server", $"'{relativePath}' leaves the server directory.");
        }

        return full;
    }

    private static bool IsUnder(string root, string path) =>
        path.Length > root.Length + 1
        && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && path[root.Length] == Path.DirectorySeparatorChar;

    /// <summary>Every existing component from the server directory (excluded: it may be a junction set up by an administrator) down to the path.</summary>
    private static void RejectReparsePoints(string root, string full)
    {
        if (full.Length == root.Length)
        {
            return;
        }

        var current = root;
        foreach (var part in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new GameProcessException("process.reparse_point",
                    $"'{Path.GetRelativePath(root, current)}' is a junction or symbolic link; server paths cannot go through one.");
            }
        }
    }

    private static unsafe string FinalPath(string path, bool directory)
    {
        using var handle = PInvoke.CreateFile(path, 0, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            directory ? FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, null);
        if (handle.IsInvalid)
        {
            throw new GameProcessException("process.invalid_path", $"Cannot open {path}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        return FinalPath(handle);
    }

    private static unsafe string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint length;
        fixed (char* pointer = buffer)
        {
            length = PInvoke.GetFinalPathNameByHandle((HANDLE)handle.DangerousGetHandle(), pointer, (uint)buffer.Length, GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
        }

        if (length == 0 || length >= buffer.Length)
        {
            throw new GameProcessException("process.invalid_path", "Cannot resolve the final path of a server file.");
        }

        var path = new string(buffer, 0, (int)length);
        // \\?\D:\x → D:\x; \\?\UNC\server\share\x → \\server\share\x.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return Path.TrimEndingDirectorySeparator(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
    }
}
