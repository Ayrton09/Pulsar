using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pulsar.Agent.Linux;

/// <summary>
/// A pseudo terminal for a game that reads its commands only from a terminal (Unturned on Linux): the agent (or the
/// game's host) keeps its master side, reading the game's output and writing its input there, and <see cref="GameExec"/>
/// makes its slave side the game's controlling terminal and standard streams. Input is not echoed (the console shows
/// the command itself), the window is wide, and every descriptor is closed on exec: the game gets only its slave, from
/// <c>game-exec</c>. The slave is kept open here too until the game ends, so reading the master waits for output instead
/// of failing before the game opened it.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class PseudoTerminal : IDisposable
{
    private const int ReadWrite = 2, NoControllingTerminal = 0x100, CloseOnExec = 0x80000;
    private const int DuplicateCloseOnExec = 1030; // F_DUPFD_CLOEXEC
    private const ulong SetWindowSize = 0x5414; // TIOCSWINSZ
    private const uint Echo = 0x8, EchoNewline = 0x40; // ECHO, ECHONL

    private readonly SafeFileHandle _master;
    private SafeFileHandle? _slave;

    private PseudoTerminal(SafeFileHandle master, SafeFileHandle slave, string slavePath)
    {
        _master = master;
        _slave = slave;
        SlavePath = slavePath;
    }

    /// <summary>The slave's device (<c>/dev/pts/N</c>), root's, which <c>game-exec</c> opens while it is still root.</summary>
    public string SlavePath { get; }

    /// <exception cref="IOException">The system has no pseudo terminal to give.</exception>
    public static PseudoTerminal Open(ushort columns = 220, ushort rows = 50)
    {
        var master = new SafeFileHandle(posix_openpt(ReadWrite | NoControllingTerminal | CloseOnExec), ownsHandle: true);
        if (master.IsInvalid)
        {
            throw Failure("posix_openpt");
        }

        try
        {
            var descriptor = (int)master.DangerousGetHandle();
            if (grantpt(descriptor) != 0)
            {
                throw Failure("grantpt");
            }

            if (unlockpt(descriptor) != 0)
            {
                throw Failure("unlockpt");
            }

            // ptsname_r returns its error rather than setting errno.
            var name = new byte[128];
            if (ptsname_r(descriptor, name, (nuint)name.Length) is var error and not 0)
            {
                throw new IOException($"ptsname_r: {new System.ComponentModel.Win32Exception(error).Message}");
            }

            var path = Encoding.ASCII.GetString(name, 0, Array.IndexOf(name, (byte)0));
            var slave = new SafeFileHandle(open(path, ReadWrite | NoControllingTerminal | CloseOnExec), ownsHandle: true);
            if (slave.IsInvalid)
            {
                throw Failure($"open {path}");
            }

            var slaveDescriptor = (int)slave.DangerousGetHandle();
            // Root's alone, whatever mode the machine's devpts gives its terminals (mode=620 lets the tty group write).
            if (fchmod(slaveDescriptor, 0x180 /* 0600 */) != 0 || tcgetattr(slaveDescriptor, out var settings) != 0)
            {
                slave.Dispose();
                throw Failure("tcgetattr");
            }

            settings.LocalFlags &= ~(Echo | EchoNewline);
            var size = new WindowSize { Rows = rows, Columns = columns };
            if (tcsetattr(slaveDescriptor, 0 /* TCSANOW */, ref settings) != 0 || ioctl(descriptor, SetWindowSize, ref size) != 0)
            {
                slave.Dispose();
                throw Failure("tcsetattr");
            }

            return new PseudoTerminal(master, slave, path);
        }
        catch
        {
            master.Dispose();
            throw;
        }
    }

    /// <summary>A stream of the master side to read the game's output from (its own descriptor).</summary>
    public FileStream OpenReader() => new(Duplicate(), FileAccess.Read, bufferSize: 0);

    /// <summary>A stream of the master side to write the game's input to (its own descriptor).</summary>
    public FileStream OpenWriter() => new(Duplicate(), FileAccess.Write, bufferSize: 0);

    /// <summary>The game has ended: once whatever it left running closes the slave too, reading the master ends.</summary>
    public void CloseSlave() => Interlocked.Exchange(ref _slave, null)?.Dispose();

    public void Dispose()
    {
        CloseSlave();
        _master.Dispose();
    }

    private SafeFileHandle Duplicate()
    {
        var copy = new SafeFileHandle(fcntl((int)_master.DangerousGetHandle(), DuplicateCloseOnExec, 0), ownsHandle: true);
        return copy.IsInvalid ? throw Failure("fcntl(F_DUPFD_CLOEXEC)") : copy;
    }

    private static IOException Failure(string call) =>
        new($"{call}: {new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    /// <summary><c>struct termios</c> of x86_64 glibc (60 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Termios
    {
        public uint InputFlags;
        public uint OutputFlags;
        public uint ControlFlags;
        public uint LocalFlags;
        public byte Line;
        public fixed byte ControlCharacters[32];
        public uint InputSpeed;
        public uint OutputSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort Width;
        public ushort Height;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int grantpt(int descriptor);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int unlockpt(int descriptor);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ptsname_r(int descriptor, [Out] byte[] name, nuint length);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int tcgetattr(int descriptor, out Termios settings);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int tcsetattr(int descriptor, int when, ref Termios settings);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int descriptor, ulong request, ref WindowSize size);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int descriptor, int command, int argument);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fchmod(int descriptor, uint mode);
}
