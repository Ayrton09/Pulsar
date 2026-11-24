using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pulsar.Agent.Linux;

/// <summary>
/// The C library calls the Linux agent needs that .NET has no API for: users, owners and link-safe file access. Only
/// x86_64 glibc (2.33 or later, which exports <c>lstat</c>) is supported: Debian 12+ and Ubuntu 24.04+.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class Libc
{
    public const int AtFdCwd = -100;
    public const int AtSymlinkNoFollow = 0x100;

    public const uint FileTypeMask = 0xF000;
    public const uint Directory = 0x4000;
    public const uint Regular = 0x8000;
    public const uint SymbolicLink = 0xA000;

    public const int SigInt = 2;
    public const int SigKill = 9;
    public const int SigTerm = 15;

    [LibraryImport("libc")]
    public static partial uint geteuid();

    /// <summary>Sends <paramref name="signal"/> to a process (a process group when <paramref name="pid"/> is negative); 0 when sent.</summary>
    [LibraryImport("libc", SetLastError = true)]
    public static partial int kill(int pid, int signal);

    [LibraryImport("libc")]
    public static partial uint getegid();

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LstatNative(string path, out Stat buffer);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenNative(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FstatNative(int descriptor, out Stat buffer);

    /// <summary>
    /// Opens a regular file to read without ever waiting: <c>O_NONBLOCK</c> (a named pipe answers at once instead of
    /// waiting for a writer), <c>O_NOFOLLOW</c> (never through a final link) and <c>O_CLOEXEC</c>; what was opened is
    /// then checked to be a regular file (<c>fstat</c> on the descriptor itself). Null when it does not exist, or is a folder.
    /// </summary>
    /// <exception cref="IOException">It is not a regular file (a pipe, a device, a link), or could not be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The calling thread's user may not read it.</exception>
    public static Microsoft.Win32.SafeHandles.SafeFileHandle? OpenRegularFile(string path)
    {
        const int ReadOnly = 0, NonBlocking = 0x800, NoFollow = 0x20000, CloseOnExec = 0x80000;
        var descriptor = OpenNative(path, ReadOnly | NonBlocking | NoFollow | CloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error switch
            {
                2 or 20 => null, // ENOENT, ENOTDIR
                1 or 13 => throw new UnauthorizedAccessException($"Access to {path} is denied."), // EPERM, EACCES: as .NET says it
                _ => throw Failure("open", path, error), // ELOOP: a link
            };
        }

        var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(descriptor, ownsHandle: true);
        if (FstatNative(descriptor, out var stat) != 0 || !stat.IsRegularFile)
        {
            handle.Dispose();
            // A folder is no file, as File.Exists says; a pipe or a device is refused.
            return stat.IsDirectory ? null : throw new IOException($"{path} is not a regular file: it is not read.");
        }

        return handle;
    }

    [LibraryImport("libc", EntryPoint = "lchown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LchownNative(string path, uint owner, uint group);

    [LibraryImport("libc", EntryPoint = "fchmodat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int FchmodatNative(int directory, string path, uint mode, int flags);

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetSockOptNative(int socket, int level, int option, out PeerCredentials value, ref uint length);

    /// <summary>
    /// Who is at the other end of a connected Unix socket (<c>SO_PEERCRED</c>): the process, user and group that connected
    /// it (or listen on it), as the kernel recorded them.
    /// </summary>
    public static PeerCredentials? PeerOf(int socket)
    {
        var length = (uint)Marshal.SizeOf<PeerCredentials>();
        return GetSockOptNative(socket, 1 /* SOL_SOCKET */, 17 /* SO_PEERCRED */, out var credentials, ref length) == 0 ? credentials : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PeerCredentials
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    /// <summary>The entry itself (a link is not followed); null when it does not exist.</summary>
    /// <exception cref="IOException">It could not be read.</exception>
    public static Stat? Lstat(string path)
    {
        if (LstatNative(path, out var stat) == 0)
        {
            return stat;
        }

        var error = Marshal.GetLastPInvokeError();
        return error is 2 or 20 ? null : throw Failure("lstat", path, error); // ENOENT, ENOTDIR
    }

    /// <summary>Gives the entry itself (never a link's target) this owner and group.</summary>
    public static void Lchown(string path, uint owner, uint group)
    {
        if (LchownNative(path, owner, group) != 0)
        {
            throw Failure("lchown", path, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Gives the entry this mode; a link is refused by the system rather than followed.</summary>
    public static void ChmodNoFollow(string path, uint mode)
    {
        if (FchmodatNative(AtFdCwd, path, mode, AtSymlinkNoFollow) != 0)
        {
            throw Failure("chmod", path, Marshal.GetLastPInvokeError());
        }
    }

    public static IOException Failure(string call, string path, int error) =>
        new($"{call} {path}: {new Win32Exception(error).Message} (errno {error})");

    /// <summary><c>struct stat</c> of x86_64 Linux (144 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Stat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint Uid;
        public uint Gid;
        private readonly int _padding;
        public ulong SpecialDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessSeconds;
        public long AccessNanoseconds;
        public long ModifySeconds;
        public long ModifyNanoseconds;
        public long ChangeSeconds;
        public long ChangeNanoseconds;
        private readonly long _reserved0;
        private readonly long _reserved1;
        private readonly long _reserved2;

        public readonly bool IsDirectory => (Mode & FileTypeMask) == Directory;

        public readonly bool IsRegularFile => (Mode & FileTypeMask) == Regular;

        public readonly bool IsSymbolicLink => (Mode & FileTypeMask) == SymbolicLink;

        /// <summary>The permission bits (with setuid, setgid and sticky).</summary>
        public readonly uint Permissions => Mode & 0xFFF;
    }
}
