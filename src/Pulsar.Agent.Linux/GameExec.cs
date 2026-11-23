using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pulsar.Agent.Linux;

/// <summary>
/// <c>PulsarAgent game-exec</c>: the last step of a game's start on Linux, run by the agent (or the game's host) as root
/// with the game's environment and pipes. In this one process, before any of the game's code runs, it leads a process
/// group of its own (the game's tree, which stopping kills), joins the game's cgroup when there is one (its limits and
/// usage), becomes the server's user (its groups, then its group, then its user, and checks it cannot go back), takes
/// <c>no_new_privs</c> (nothing it runs gains privileges through setuid or file capabilities), checks the executable is an
/// ELF program and replaces itself with it. Not meant to be run by hand: the help does not list it.
/// </summary>
[SupportedOSPlatform("linux")]
public static partial class GameExec
{
    public const string Command = "game-exec";

    /// <summary>Exit codes as a shell has them: the program could not be run (126), or was not found (127).</summary>
    public const int CannotRun = 126;
    public const int NotFound = 127;

    private const int PrSetNoNewPrivs = 38;
    private const int SigPipe = 13;
    private const int SigSetMask = 2;

    /// <summary>
    /// The arguments the agent passes:
    /// <c>game-exec --uid U --gid G --env-file FILE [--cgroup DIR] [--mkdir DIR]... --cwd DIR -- EXECUTABLE [ARG...]</c>.
    /// </summary>
    /// <param name="environmentFile">The game's environment (<see cref="WriteEnvironment"/>): never this process's own.</param>
    /// <param name="directories">Folders of the server made (when missing) as its user, before the game starts: its temporary folder, say.</param>
    public static IReadOnlyList<string> Arguments(uint uid, uint gid, string environmentFile, string? cgroup, string workingDirectory, string executable,
        IEnumerable<string> arguments, IEnumerable<string>? directories = null)
    {
        List<string> list =
            [Command, "--uid", uid.ToString(CultureInfo.InvariantCulture), "--gid", gid.ToString(CultureInfo.InvariantCulture), "--env-file", environmentFile];
        if (cgroup is not null)
        {
            list.AddRange(["--cgroup", cgroup]);
        }

        foreach (var directory in directories ?? [])
        {
            list.AddRange(["--mkdir", directory]);
        }

        list.AddRange(["--cwd", workingDirectory, "--", executable]);
        list.AddRange(arguments);
        return list;
    }

    /// <summary>
    /// Writes the game's environment where only this user (root, for the service) can read it, for <c>--env-file</c>:
    /// <c>game-exec</c> runs as root before it becomes the server's user, and a variable the server controls (its
    /// <c>DOTNET_ROOT</c>, its temporary folder) must never reach root's own runtime. Its values (passwords among them) are
    /// never on a command line, which every user can read. The file goes as it is read.
    /// </summary>
    public static string WriteEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var path = Path.Combine(Path.GetTempPath(), $"pulsar-game-environment-{Guid.NewGuid():N}");
        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        foreach (var (name, value) in environment)
        {
            if (name.Contains('\0', StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new IOException("An environment variable holds a NUL character.");
            }

            file.Write(System.Text.Encoding.UTF8.GetBytes($"{name}={value}\0"));
        }

        return path;
    }

    /// <summary>Reads (and deletes) an environment file: one this user wrote, that nobody else could read or replace.</summary>
    internal static IReadOnlyList<string> ReadEnvironment(string path)
    {
        try
        {
            if (Libc.Lstat(path) is not { IsRegularFile: true } stat || stat.Uid != Libc.geteuid() || (stat.Permissions & 0x3F) != 0)
            {
                throw new IOException($"{path} is not the agent's own environment file.");
            }

            var text = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return [.. text.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Runs the command; returns only when the game could not be started (its exit code), writing why to standard error.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!TryParse(args, out var request, out var usage))
        {
            Console.Error.WriteLine($"PulsarAgent {Command}: {usage}");
            return CannotRun;
        }

        // What saying a failure needs is loaded while the process is still root's: the server's user may not read the
        // agent's folder (a development agent's), and the runtime would fail loading it later.
        var errors = Console.Error;
        errors.Flush();
        _ = Describe(2);
        _ = CheckProgram("/");

        IReadOnlyList<string> environment;
        try
        {
            environment = ReadEnvironment(request.EnvironmentFile);
            Prepare(request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Pulsar could not start the game: {ex.Message}");
            return CannotRun;
        }

        // From here on the process is the server's user's: what fails is said in the game's console.
        if (CheckProgram(request.Executable) is { } refusal)
        {
            Console.Error.WriteLine($"Pulsar did not start the game: {refusal.Message}");
            return refusal.Code;
        }

        int error;
        try
        {
            error = Execute(request.Executable, [request.Executable, .. request.Arguments], environment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Pulsar could not start the game {request.Executable}: {ex.Message}");
            return CannotRun;
        }

        Console.Error.WriteLine($"Pulsar could not start the game {request.Executable}: {Describe(error)}");
        return error == 2 ? NotFound : CannotRun;
    }

    internal sealed record Request(uint Uid, uint Gid, string? Cgroup, string WorkingDirectory, string Executable, IReadOnlyList<string> Arguments)
    {
        public IReadOnlyList<string> Directories { get; init; } = [];

        public string EnvironmentFile { get; init; } = "";
    }

    internal static bool TryParse(IReadOnlyList<string> args, out Request request, out string usage)
    {
        request = null!;
        usage = "usage: game-exec --uid U --gid G --env-file FILE [--cgroup DIR] [--mkdir DIR]... --cwd DIR -- EXECUTABLE [ARG...]";
        uint? uid = null, gid = null;
        string? cgroup = null, cwd = null, environmentFile = null;
        var directories = new List<string>();
        var i = args.Count > 0 && args[0] == Command ? 1 : 0;
        for (; i < args.Count && args[i] != "--"; i += 2)
        {
            if (i + 1 >= args.Count)
            {
                return false;
            }

            var value = args[i + 1];
            switch (args[i])
            {
                case "--uid" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var u):
                    uid = u;
                    break;
                case "--gid" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var g):
                    gid = g;
                    break;
                case "--cgroup" when Path.IsPathFullyQualified(value):
                    cgroup = value;
                    break;
                case "--cwd" when Path.IsPathFullyQualified(value):
                    cwd = value;
                    break;
                case "--mkdir" when Path.IsPathFullyQualified(value):
                    directories.Add(value);
                    break;
                case "--env-file" when Path.IsPathFullyQualified(value):
                    environmentFile = value;
                    break;
                default:
                    return false;
            }
        }

        if (uid is not { } user || gid is not { } group || cwd is null || environmentFile is null || i + 1 >= args.Count
            || !Path.IsPathFullyQualified(args[i + 1]))
        {
            return false;
        }

        // Never root: a server always has a user of its own.
        if (user == 0 || group == 0)
        {
            usage = "a game never runs as root.";
            return false;
        }

        request = new Request(user, group, cgroup, cwd, args[i + 1], [.. args.Skip(i + 2)]) { Directories = directories, EnvironmentFile = environmentFile };
        return true;
    }

    private static void Prepare(Request request)
    {
        // The game's tree: a group of processes the agent kills at once (setsid fails only for a group leader, which a
        // process the agent just started is not).
        _ = setsid();
        if (request.Cgroup is { } cgroup)
        {
            // Into the game's cgroup before anything of the game runs: everything it starts is born there.
            File.WriteAllText(Path.Combine(cgroup, "cgroup.procs"), "0\n");
        }

        Check(setgroups(0, IntPtr.Zero), "setgroups");
        Check(setresgid(request.Gid, request.Gid, request.Gid), "setresgid");
        Check(setresuid(request.Uid, request.Uid, request.Uid), "setresuid");
        // Root's way back must be gone: a failed or partial change never runs the game with root's rights.
        if (getuid() != request.Uid || geteuid() != request.Uid || getgid() != request.Gid || setresuid(0, 0, 0) == 0)
        {
            throw new IOException("the server's user could not be taken for good.");
        }

        Check(prctl(PrSetNoNewPrivs, 1, 0, 0, 0), "prctl(PR_SET_NO_NEW_PRIVS)");
        // As the server's user: a link it put there leads only where it may go anyway.
        foreach (var directory in request.Directories)
        {
            Directory.CreateDirectory(directory);
        }

        Check(chdir(request.WorkingDirectory), $"chdir {request.WorkingDirectory}");
        // .NET ignores SIGPIPE and may block signals; the game starts with the defaults a program expects.
        _ = signal(SigPipe, IntPtr.Zero);
        var empty = new byte[128];
        unsafe
        {
            fixed (byte* set = empty)
            {
                _ = sigprocmask(SigSetMask, (IntPtr)set, IntPtr.Zero);
            }
        }
    }

    /// <summary>The executable is an ELF program the server's user may run; null when it is.</summary>
    internal static (string Message, int Code)? CheckProgram(string executable)
    {
        Span<byte> magic = stackalloc byte[4];
        try
        {
            using var file = File.OpenHandle(executable, FileMode.Open, FileAccess.Read);
            if (RandomAccess.Read(file, magic, 0) != 4 || magic[0] != 0x7F || magic[1] != (byte)'E' || magic[2] != (byte)'L' || magic[3] != (byte)'F')
            {
                return ($"{executable} is not a program (scripts are not started: the manifest names the game's own program).", CannotRun);
            }
        }
        catch (FileNotFoundException)
        {
            return ($"{executable} was not found (is the server installed?).", NotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return ($"{executable} was not found (is the server installed?).", NotFound);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"{executable} cannot be read by the server's user: {ex.Message}", CannotRun);
        }

        return null;
    }

    /// <summary>Replaces the process with the program and the game's environment; returns the error when it could not.</summary>
    private static unsafe int Execute(string executable, IReadOnlyList<string> argv, IReadOnlyList<string> environment)
    {
        var allocated = new List<IntPtr>();
        try
        {
            var argvPointers = new IntPtr[argv.Count + 1];
            for (var i = 0; i < argv.Count; i++)
            {
                argvPointers[i] = Utf8(argv[i], allocated);
            }

            var envPointers = new IntPtr[environment.Count + 1];
            for (var i = 0; i < environment.Count; i++)
            {
                envPointers[i] = Utf8(environment[i], allocated);
            }

            fixed (IntPtr* argvBlock = argvPointers)
            fixed (IntPtr* envBlock = envPointers)
            {
                _ = execve(Utf8(executable, allocated), (IntPtr)argvBlock, (IntPtr)envBlock);
                return Marshal.GetLastPInvokeError();
            }
        }
        finally
        {
            foreach (var pointer in allocated)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    private static IntPtr Utf8(string text, List<IntPtr> allocated)
    {
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new IOException("an argument or environment value holds a NUL character.");
        }

        var pointer = Marshal.StringToCoTaskMemUTF8(text);
        allocated.Add(pointer);
        return pointer;
    }

    private static void Check(int result, string call)
    {
        if (result != 0)
        {
            throw new IOException($"{call} failed: {Describe(Marshal.GetLastPInvokeError())}");
        }
    }

    private static string Describe(int error) => $"{new System.ComponentModel.Win32Exception(error).Message} (errno {error})";

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setgroups(nuint size, IntPtr list);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setresgid(uint real, uint effective, uint saved);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setresuid(uint real, uint effective, uint saved);

    [LibraryImport("libc")]
    private static partial uint getuid();

    [LibraryImport("libc")]
    private static partial uint geteuid();

    [LibraryImport("libc")]
    private static partial uint getgid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chdir(string path);

    [LibraryImport("libc")]
    private static partial IntPtr signal(int signal, IntPtr handler);

    [LibraryImport("libc")]
    private static partial int sigprocmask(int how, IntPtr set, IntPtr old);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int execve(IntPtr path, IntPtr argv, IntPtr envp);
}
