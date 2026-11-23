using System.Runtime.Versioning;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Resolves a path a manifest gives relative to the server's folder (docs/architecture/06, start sequence step 1): it
/// stays under the folder, and no part of it below the folder is a symbolic link. (The game is started as the server's
/// user anyway, so a link swapped in afterwards only reaches what that user may.)
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxServerPaths
{
    public static string NormalizeRoot(string serverRoot)
    {
        if (!Path.IsPathFullyQualified(serverRoot))
        {
            throw new GameProcessException("process.invalid_path", "The server directory must be an absolute path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverRoot));
    }

    /// <summary>A file under the server's folder; it must exist and be a file.</summary>
    public static string ResolveFile(string serverRoot, string relativePath)
    {
        var root = NormalizeRoot(serverRoot);
        var full = Combine(root, relativePath);
        RejectLinks(root, full);
        if (Libc.Lstat(full) is not { IsRegularFile: true })
        {
            throw new GameProcessException("process.executable_missing", $"{relativePath} does not exist in the server directory.");
        }

        return full;
    }

    /// <summary>A folder under the server's folder (the folder itself for an empty path); it must exist.</summary>
    public static string ResolveDirectory(string serverRoot, string relativePath)
    {
        var root = NormalizeRoot(serverRoot);
        var full = string.IsNullOrEmpty(relativePath) ? root : Combine(root, relativePath);
        RejectLinks(root, full);
        if (Libc.Lstat(full) is not { IsDirectory: true })
        {
            throw new GameProcessException("process.directory_missing", $"{relativePath} does not exist in the server directory.");
        }

        return full;
    }

    /// <summary>The full path of a relative one under the root, refused when it would leave it.</summary>
    public static string Combine(string root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var segments = relativePath.Split('/');
        if (relativePath.Length == 0 || relativePath.StartsWith('/') || relativePath.Contains('\0', StringComparison.Ordinal)
            || segments.Any(s => s is "" or "." or ".."))
        {
            throw new GameProcessException("process.invalid_path", $"'{relativePath}' is not a path in the server directory.");
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        return full.StartsWith(root + "/", StringComparison.Ordinal)
            ? full
            : throw new GameProcessException("process.path_outside_server", $"{relativePath} is outside the server directory.");
    }

    private static void RejectLinks(string root, string full)
    {
        for (var current = full; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
        {
            if (Libc.Lstat(current) is { IsSymbolicLink: true })
            {
                throw new GameProcessException("process.path_outside_server",
                    $"{current[(root.Length + 1)..]} is a symbolic link: the server's programs and folders are its own.");
            }
        }
    }
}
