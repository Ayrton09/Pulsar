using System.Runtime.Versioning;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Panel;

namespace Pulsar.Agent.Linux;

/// <summary>
/// The agent's folders on Linux (docs/architecture/03): root's alone (mode 0700; the current user's in development, who
/// runs the agent without root). As on Windows, a symbolic link where a folder of the agent is (or in what its own
/// folders hold) is refused, a production folder must not lie in a folder another user could replace (the root service
/// trusts what it loads from there), and other content is never rewritten: a backups or servers root that already holds
/// someone else's files is refused.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxDirectorySecurity : IAgentDirectorySecurity
{
    private const uint OwnerOnly = 0x1C0; // 0700
    // Others may pass through the servers root to their own server's folder, never list it.
    private const uint Traversable = 0x1C9; // 0711

    private static readonly EnumerationOptions s_everything = new() { AttributesToSkip = 0, RecurseSubdirectories = false };

    public void SecureState(string directory, IReadOnlyCollection<string> ownFolders, bool development)
    {
        ArgumentNullException.ThrowIfNull(ownFolders);
        directory = Checked(directory, development);
        var existed = Libc.Lstat(directory) is not null;
        List<string> own = [];
        if (existed)
        {
            RefuseLink(directory);
            // What sits at the top is reached through the folder: none of it may point elsewhere. Only the agent's own
            // folders are walked and reset; anything else keeps its own modes.
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", s_everything))
            {
                RefuseLink(entry);
            }

            own = [.. ownFolders.Select(Path.GetFullPath).Where(f => Libc.Lstat(f) is not null)];
            foreach (var folder in own)
            {
                RefuseLinks(folder);
            }
        }

        var (uid, gid) = Owner(development);
        Apply(directory, OwnerOnly, uid, gid);
        foreach (var folder in own)
        {
            // Top down: once a folder is the agent's alone, nobody else can put something in place of what it holds.
            Reset(folder, uid, gid);
        }
    }

    public void SecureFolder(string directory, bool development)
    {
        directory = Checked(directory, development);
        var (uid, gid) = Owner(development);
        var existed = Libc.Lstat(directory) is not null;
        if (existed)
        {
            RefuseLink(directory);
            // The agent cannot tell its files from planted ones: a folder with content is taken only when it already is
            // the agent's alone (an earlier enrollment).
            if (Directory.EnumerateFileSystemEntries(directory, "*", s_everything).Any() && !IsAgentsAlone(directory, uid))
            {
                throw new AgentException($"""
                    {directory} already holds files other users can open. The agent keeps this folder to root alone, which would change who can open them, and it would trust whatever is there.
                    Choose an empty or new folder and enroll again.
                    """);
            }

            RefuseLinks(directory);
        }

        Apply(directory, OwnerOnly, uid, gid);
        if (existed)
        {
            Reset(directory, uid, gid);
        }
    }

    public void SecureServersRoot(string directory, bool development)
    {
        directory = Checked(directory, development);
        var (uid, gid) = Owner(development);
        if (Libc.Lstat(directory) is not null)
        {
            RefuseLink(directory);
            if (Directory.EnumerateFileSystemEntries(directory, "*", s_everything).Any() && !IsAgentsAlone(directory, uid))
            {
                throw new AgentException($"""
                    {directory} already holds files other users can open. The agent keeps its servers root to root alone (others may only pass through to their own server's folder), which would change who can open them.
                    Choose an empty or new folder (--servers-root) and enroll again.
                    """);
            }
        }

        // Only the folder itself: each server's folder in it is its own user's (docs/architecture/06, "Isolation").
        Apply(directory, Traversable, uid, gid);
    }

    /// <summary>
    /// Makes the copies of the agent's program (<paramref name="programs"/>) readable by everyone and writable by root
    /// alone, as an installed program is (directories 0755, files 0644 or 0755): a game's start runs the copy's own
    /// program as the server's user. Returns <paramref name="executable"/>.
    /// </summary>
    public static string Readable(string executable, string programs)
    {
        if (!IsElevated())
        {
            return executable;
        }

        Libc.Lchown(programs, 0, 0);
        Libc.ChmodNoFollow(programs, 0x1ED);
        foreach (var entry in Directory.EnumerateFileSystemEntries(programs, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true }))
        {
            if (Libc.Lstat(entry) is not { IsSymbolicLink: false } stat)
            {
                continue;
            }

            Libc.Lchown(entry, 0, 0);
            Libc.ChmodNoFollow(entry, stat.IsDirectory || (stat.Permissions & 0x40) != 0 ? 0x1EDu : 0x1A4u);
        }

        return executable;
    }

    /// <summary>True when this process runs as root: a production enrollment requires it.</summary>
    public static bool IsElevated() => Libc.geteuid() == 0;

    /// <summary>
    /// The folders above <paramref name="directory"/> another user than root could replace or change: owned by someone
    /// else, or writable by others (or by a group of users rather than of the system).
    /// </summary>
    public static IReadOnlyList<string> FindReplaceableAncestors(string directory)
    {
        var findings = new List<string>();
        for (var ancestor = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
             ancestor is not null;
             ancestor = Path.GetDirectoryName(ancestor))
        {
            if (Libc.Lstat(ancestor) is not { } stat)
            {
                continue;
            }

            if (stat.IsSymbolicLink)
            {
                findings.Add($"{ancestor} is a symbolic link");
            }
            else if (stat.Uid != 0)
            {
                findings.Add($"{ancestor} is owned by user {stat.Uid}, not root");
            }
            else if ((stat.Permissions & 0x2) != 0 || ((stat.Permissions & 0x10) != 0 && stat.Gid >= 1000))
            {
                findings.Add($"{ancestor} can be written by other users (mode {Convert.ToString(stat.Permissions & 0x1FF, 8)})");
            }
        }

        return findings;
    }

    /// <summary>
    /// Who other than root could replace or change a program root would run: the file itself (owned by someone else, or
    /// writable by others) and the folders above it.
    /// </summary>
    public static IReadOnlyList<string> FindUntrustedWriters(string executablePath)
    {
        var findings = new List<string>();
        if (Libc.Lstat(executablePath) is { } stat && (stat.Uid != 0 || (stat.Permissions & 0x12) != 0 || stat.IsSymbolicLink))
        {
            findings.Add($"{executablePath} is a link, owned by user {stat.Uid} or can be written by others (mode {Convert.ToString(stat.Permissions & 0x1FF, 8)})");
        }

        findings.AddRange(FindReplaceableAncestors(executablePath));
        return findings;
    }

    private static string Checked(string directory, bool development)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!development && FindReplaceableAncestors(directory) is { Count: > 0 } findings)
        {
            throw new AgentException($"""
                {directory} is inside folders other users could replace or change, so the state the service loads could be swapped:
                  {string.Join(Environment.NewLine + "  ", findings)}
                Make them root's and closed to others (sudo chown root:root <folder> && sudo chmod go-w <folder>) and enroll again.
                """);
        }

        return directory;
    }

    private static (uint Uid, uint Gid) Owner(bool development) => development ? (Libc.geteuid(), Libc.getegid()) : (0u, 0u);

    /// <summary>Creates the folder (or takes the one there) with this mode and owner, never through a link.</summary>
    private static void Apply(string directory, uint mode, uint uid, uint gid)
    {
        if (Libc.Lstat(directory) is null)
        {
            Directory.CreateDirectory(directory, (UnixFileMode)OwnerOnly);
        }

        RefuseLink(directory);
        Libc.Lchown(directory, uid, gid);
        Libc.ChmodNoFollow(directory, mode);
    }

    /// <summary>What the folder holds becomes the agent's alone: its owner's, and closed to group and others.</summary>
    private static void Reset(string folder, uint uid, uint gid)
    {
        Libc.Lchown(folder, uid, gid);
        Libc.ChmodNoFollow(folder, OwnerOnly);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", s_everything))
        {
            if (Libc.Lstat(entry) is not { } stat)
            {
                continue;
            }

            if (stat.IsSymbolicLink)
            {
                throw new AgentException($"{entry} is a symbolic link: the agent's folders hold none. Remove it and enroll again.");
            }

            Libc.Lchown(entry, uid, gid);
            if (stat.IsDirectory)
            {
                Reset(entry, uid, gid);
            }
            else
            {
                // The owner's own bits stay (the copies hosts run from are programs).
                Libc.ChmodNoFollow(entry, stat.Permissions & 0x1C0);
            }
        }
    }

    private static bool IsAgentsAlone(string directory, uint uid) =>
        Libc.Lstat(directory) is { IsDirectory: true } stat && stat.Uid == uid && (stat.Permissions & 0x3F & ~0x9u) == 0;

    private static void RefuseLink(string path)
    {
        if (Libc.Lstat(path) is { IsSymbolicLink: true })
        {
            throw new AgentException($"{path} is a symbolic link: the agent's folders are never links. Remove it and enroll again.");
        }
    }

    private static void RefuseLinks(string folder)
    {
        RefuseLink(folder);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true }))
        {
            RefuseLink(entry);
        }
    }
}
