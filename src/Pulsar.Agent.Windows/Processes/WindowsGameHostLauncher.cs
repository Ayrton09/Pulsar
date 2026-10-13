using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;
using Windows.Wdk.System.SystemInformation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authorization;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Threading;
using Windows.Win32.System.WindowsProgramming;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// Starts <c>PulsarAgent host</c> processes (docs/architecture/06, "Server hosts"), as the agent's own account and out
/// of any Job the agent is in, so they outlive it: without a console (a development agent's Ctrl+C is not theirs) and
/// inheriting no handle. Administrators may end one (an unenroll does when the agent is not running); nobody else may
/// touch it. A host is identified by its process id and start time, so a reused process id is never mistaken for it.
/// </summary>
/// <param name="program">
/// <c>PulsarAgent.exe</c>, in a copy made by <see cref="GameHostProgram.Prepare"/> when it is the service's. Asked for
/// when first needed, and again at each start while it fails (a full disk, say): until then, starts fail with the reason
/// and the hosts that run are still found, watched and ended.
/// </param>
/// <param name="hostPrograms">Where those copies are (null: the executable is not a copy, as in tests).</param>
/// <param name="development">The agent was enrolled with <c>--dev</c>: its hosts may start games as the agent's own account (<c>--dev</c> on their command line).</param>
public sealed partial class WindowsGameHostLauncher(Func<string> program, string dataDirectory, string? hostPrograms, ILogger<WindowsGameHostLauncher> logger,
    bool development = false)
    : IGameHostLauncher
{
    private const uint SddlRevision1 = 1;
    private const uint ReadControl = 0x00020000;

    // The creator (SYSTEM, or the developer) owns it; Administrators may end it and wait for it (PROCESS_TERMINATE |
    // PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE).
    private static readonly string s_processSddl =
        $"D:P(A;;GA;;;SY)(A;;GA;;;{WindowsIdentity.GetCurrent().User!.Value})(A;;0x00101001;;;BA)";

    private readonly Lock _lock = new();
    private string? _executable;

    public WindowsGameHostLauncher(string executable, string dataDirectory, string? hostPrograms, ILogger<WindowsGameHostLauncher> logger,
        bool development = false)
        : this(() => executable, dataDirectory, hostPrograms, logger, development)
    {
    }

    /// <summary>The program hosts start from.</summary>
    /// <exception cref="IOException">Its copy could not be made.</exception>
    /// <exception cref="UnauthorizedAccessException">Its copy could not be made.</exception>
    public string Executable
    {
        get
        {
            lock (_lock)
            {
                if (_executable is null)
                {
                    try
                    {
                        _executable = Path.GetFullPath(program());
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogProgramUnavailable(ex.Message);
                        throw;
                    }
                }

                return _executable;
            }
        }
    }

    public unsafe GameHostReference Launch(Guid serverId, string pipe)
    {
        if (!pipe.StartsWith("pulsar-host-", StringComparison.Ordinal) || pipe.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException("Not a host pipe name.", nameof(pipe));
        }

        var executable = Executable;
        string[] arguments = ["host", "--server", serverId.ToString("D"), "--pipe", pipe, "--data-dir", dataDirectory];
        var commandLine = WindowsCommandLine.Build(executable, development ? [.. arguments, "--dev"] : arguments);
        PSECURITY_DESCRIPTOR descriptor;
        fixed (char* sddl = s_processSddl)
        {
            if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision1, &descriptor, null))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Invalid host process security descriptor.");
            }
        }

        try
        {
            var security = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = descriptor.Value };
            var flags = PROCESS_CREATION_FLAGS.DETACHED_PROCESS | PROCESS_CREATION_FLAGS.CREATE_NEW_PROCESS_GROUP
                | PROCESS_CREATION_FLAGS.CREATE_BREAKAWAY_FROM_JOB;
            if (!TryCreate(executable, commandLine, flags, &security, out var info, out var error))
            {
                if (error != (int)WIN32_ERROR.ERROR_ACCESS_DENIED)
                {
                    throw new Win32Exception(error, "CreateProcess(host) failed.");
                }

                // The agent runs in a Job that forbids leaving it (some service wrappers, test runners): this host then ends
                // with that Job, as games did before hosts. Only this one: access denied may be anything else too (a
                // transient failure, a filter driver), and breakaway is tried again at the next launch.
                LogNoBreakaway(error);
                flags &= ~PROCESS_CREATION_FLAGS.CREATE_BREAKAWAY_FROM_JOB;
                if (!TryCreate(executable, commandLine, flags, &security, out info, out error))
                {
                    throw new Win32Exception(error, "CreateProcess(host) failed.");
                }
            }

            using var process = new SafeFileHandle((nint)info.hProcess.Value, ownsHandle: true);
            PInvoke.CloseHandle(info.hThread);
            var started = StartTime(process) ?? throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetProcessTimes(host) failed.");
            LogLaunched(serverId, (int)info.dwProcessId);
            return new GameHostReference(pipe, (int)info.dwProcessId, started);
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)descriptor.Value);
        }
    }

    public bool IsAlive(GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(host);
        using var process = Open(host, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE);
        return process is not null && PInvoke.WaitForSingleObject(process, 0) == WAIT_EVENT.WAIT_TIMEOUT;
    }

    public bool Kill(GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(host);
        SafeFileHandle? process;
        try
        {
            process = Open(host,
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE);
        }
        catch (GameProcessException ex) when (ex.Code == GameHostManager.HostUnverifiable)
        {
            LogNotChecked(host.ProcessId, ex.Message);
            return false;
        }

        if (process is null)
        {
            return true; // gone already
        }

        using (process)
        {
            LogKilled(host.ProcessId);
            var terminated = PInvoke.TerminateProcess(process, 1);
            var error = terminated ? 0 : Marshal.GetLastPInvokeError();
            // Gone (and its Job closed, so its game too) before the caller goes on.
            var gone = PInvoke.WaitForSingleObject(process, 10_000) == WAIT_EVENT.WAIT_OBJECT_0;
            if (!gone)
            {
                LogNotKilled(host.ProcessId, terminated, error);
            }

            return gone;
        }
    }

    public bool Serves(PipeStream pipe, GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(host);
        if (!PInvoke.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcess) || serverProcess != (uint)host.ProcessId)
        {
            return false;
        }

        try
        {
            return IsAlive(host);
        }
        catch (GameProcessException ex) when (ex.Code == GameHostManager.HostUnverifiable)
        {
            return false; // not proven to be the host: not trusted
        }
    }

    public void CleanUpPrograms(IEnumerable<GameHostReference> running)
    {
        ArgumentNullException.ThrowIfNull(running);
        if (hostPrograms is null)
        {
            return;
        }

        string current;
        try
        {
            current = Executable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // logged; the copies wait for the next start of the agent
        }

        GameHostProgram.CleanUp(hostPrograms, current, running.Select(ProgramOf).OfType<string>().ToList());
    }

    /// <summary>The program a running host was started from (null when it is gone).</summary>
    public static string? ProgramOf(GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(host);
        SafeFileHandle? process;
        try
        {
            process = Open(host, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        }
        catch (GameProcessException ex) when (ex.Code == GameHostManager.HostUnverifiable)
        {
            return null; // cannot be told; its program's copy is in use anyway and cannot be deleted
        }

        using var _ = process;
        if (process is null)
        {
            return null;
        }

        Span<char> buffer = new char[32 * 1024];
        var length = (uint)buffer.Length;
        return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length)
            ? new string(buffer[..(int)length])
            : null;
    }

    private static unsafe bool TryCreate(string executable, string commandLine, PROCESS_CREATION_FLAGS flags, SECURITY_ATTRIBUTES* security,
        out PROCESS_INFORMATION info, out int error)
    {
        var command = (commandLine + '\0').ToCharArray();
        var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        // Started from the program's folder, never from wherever the agent's current directory is.
        var directory = Path.GetDirectoryName(executable)!;
        fixed (char* application = executable)
        fixed (char* commandPtr = command)
        fixed (char* directoryPtr = directory)
        {
            PROCESS_INFORMATION created;
            if (PInvoke.CreateProcess(application, commandPtr, security, null, false, flags, null, directoryPtr, &startup, &created))
            {
                info = created;
                error = 0;
                return true;
            }
        }

        info = default;
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>The process if it is the host (same id and start time); null when that process is gone.</summary>
    /// <exception cref="GameProcessException">
    /// <see cref="GameHostManager.HostUnverifiable"/>: the process could not be opened or read for any reason but being
    /// gone (access denied, say). Only ERROR_INVALID_PARAMETER means there is no such process: anything else is "could
    /// not check", and a host that runs must never be taken for one that does not.
    /// </exception>
    private static SafeFileHandle? Open(GameHostReference host, PROCESS_ACCESS_RIGHTS access)
    {
        var process = PInvoke.OpenProcess_SafeHandle(access, false, (uint)host.ProcessId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            process.Dispose();
            return IsGoneAfterFailedOpen(host, error, MayBeRunning) ? null : throw Unverifiable(host, error);
        }

        if (StartTime(process) is not { } started)
        {
            var error = Marshal.GetLastPInvokeError();
            process.Dispose();
            return IsGoneAfterFailedOpen(host, error, MayBeRunning) ? null : throw Unverifiable(host, error);
        }

        if (started != host.ProcessStartedAt)
        {
            process.Dispose(); // the id was reused by another process
            return null;
        }

        return process;
    }

    /// <summary>
    /// What a process that cannot be opened (or read) means. ERROR_INVALID_PARAMETER: there is no such process. Any other
    /// failure (access denied: after a reboot a stale record's id may belong to a protected or SYSTEM process) says nothing
    /// about our host, so tests that need no process handle decide (<see cref="MayBeRunning(GameHostReference)"/>).
    /// </summary>
    /// <returns>True when the host is gone.</returns>
    /// <exception cref="GameProcessException"><see cref="GameHostManager.HostUnverifiable"/>: the host may be running.</exception>
    internal static bool IsGoneAfterFailedOpen(GameHostReference host, int error, Func<GameHostReference, bool> mayBeRunning) =>
        error == (int)WIN32_ERROR.ERROR_INVALID_PARAMETER || !mayBeRunning(host)
            ? true
            : throw Unverifiable(host, error);

    /// <summary>
    /// Whether a host whose process cannot be opened may still run, told by its named pipe: it creates the pipe first
    /// thing and keeps it while it lives. Any account can create a pipe under a name it saw in the pipe namespace, so the
    /// name alone proves nothing: a host's pipe lets the agent's account in and is owned by the agent's default owner (the
    /// owner the agent's end requires, <see cref="GameHostPipes.CreateClient"/>; for SYSTEM the Administrators group),
    /// which no game server's account can give a pipe of its own. When the owner cannot be read (every instance busy, as
    /// anyone can keep a pipe of their own), the system's process list decides: a process with the host's id and start
    /// time, which no other process can have. When neither can tell, the host may be running.
    /// </summary>
    internal static bool MayBeRunning(GameHostReference host)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return MayBeRunning(host, ProbePipe, identity.Owner, IsListed);
    }

    internal static bool MayBeRunning(GameHostReference host, Func<string, (PipeProbe Probe, SecurityIdentifier? Owner)> probe,
        SecurityIdentifier? defaultOwner, Func<GameHostReference, bool?> listed)
    {
        var (found, owner) = probe(host.Pipe);
        return found switch
        {
            PipeProbe.Missing or PipeProbe.Refused => false,
            PipeProbe.Owned => defaultOwner is null || owner == defaultOwner,
            _ => listed(host) ?? true,
        };
    }

    /// <summary>
    /// Whether the system's process list has a process with the host's id and start time, read without a handle to any
    /// process. Null when the list cannot be read.
    /// </summary>
    internal static unsafe bool? IsListed(GameHostReference host)
    {
        const int InfoLengthMismatch = unchecked((int)0xC0000004);
        // The process's creation time, in winternl.h's Reserved1 (CreateTime in the full structure, the same on every
        // architecture: two 64-bit fields and two 32-bit ones come before it).
        const int CreateTimeOffset = 24;
        var size = 1024u * 1024;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                uint needed = 0;
                var status = global::Windows.Wdk.PInvoke.NtQuerySystemInformation(SYSTEM_INFORMATION_CLASS.SystemProcessInformation, buffer, size, &needed);
                if (status.Value == InfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (64 * 1024));
                    continue;
                }

                if (status.Value < 0)
                {
                    return null;
                }

                for (var entry = buffer; ; entry += ((SYSTEM_PROCESS_INFORMATION*)entry)->NextEntryOffset)
                {
                    var process = (SYSTEM_PROCESS_INFORMATION*)entry;
                    if ((nint)process->UniqueProcessId.Value == host.ProcessId)
                    {
                        var created = *(long*)((byte*)&process->Reserved1 + CreateTimeOffset);
                        return DateTimeOffset.FromFileTime(created).ToUniversalTime() == host.ProcessStartedAt;
                    }

                    if (process->NextEntryOffset == 0)
                    {
                        return false;
                    }
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return null;
    }

    /// <summary>
    /// Opens a pipe by name to read its owner, and nothing else: no data access, and anonymous to whoever serves it (which
    /// cannot then act as the agent). An instance that is free is connected to for that moment; when every instance is
    /// busy, the owner cannot be read.
    /// </summary>
    internal static unsafe (PipeProbe Probe, SecurityIdentifier? Owner) ProbePipe(string name)
    {
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            return (PipeProbe.Unknown, null);
        }

        using var handle = PInvoke.CreateFile(@"\\.\pipe\" + name, ReadControl, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.SECURITY_SQOS_PRESENT | FILE_FLAGS_AND_ATTRIBUTES.SECURITY_ANONYMOUS, null);
        if (handle.IsInvalid)
        {
            return (WIN32_ERROR)Marshal.GetLastPInvokeError() switch
            {
                WIN32_ERROR.ERROR_FILE_NOT_FOUND or WIN32_ERROR.ERROR_PATH_NOT_FOUND => (PipeProbe.Missing, null),
                // A host's pipe always lets the agent's account in: one that refuses it is someone else's.
                WIN32_ERROR.ERROR_ACCESS_DENIED => (PipeProbe.Refused, null),
                _ => (PipeProbe.Unknown, null), // every instance busy, or anything else
            };
        }

        PSID owner;
        PSECURITY_DESCRIPTOR descriptor;
        if (PInvoke.GetSecurityInfo((HANDLE)handle.DangerousGetHandle(), SE_OBJECT_TYPE.SE_KERNEL_OBJECT, OBJECT_SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION,
                &owner, null, null, null, &descriptor) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return (PipeProbe.Unknown, null);
        }

        try
        {
            return owner.Value is null ? (PipeProbe.Unknown, null) : (PipeProbe.Owned, new SecurityIdentifier((nint)owner.Value));
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)descriptor.Value);
        }
    }

    /// <summary>What opening a pipe for its owner found.</summary>
    internal enum PipeProbe
    {
        Missing,
        Refused,
        Owned,
        Unknown,
    }

    private static GameProcessException Unverifiable(GameHostReference host, int error) =>
        new(GameHostManager.HostUnverifiable,
            $"The host process {host.ProcessId} could not be opened to check it, and it may be running: {new Win32Exception(error).Message} (Win32 error {error}).");

    private static DateTimeOffset? StartTime(SafeHandle process)
    {
        if (!PInvoke.GetProcessTimes(process, out var created, out _, out _, out _))
        {
            return null;
        }

        var ticks = (long)(((ulong)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime);
        return DateTimeOffset.FromFileTime(ticks).ToUniversalTime();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: started its host process {HostProcessId}")]
    private partial void LogLaunched(Guid serverId, int hostProcessId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The copy of the agent's program that host processes run from could not be made ({Reason}): game servers cannot start until it can")]
    private partial void LogProgramUnavailable(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killing the host process {HostProcessId}")]
    private partial void LogKilled(int hostProcessId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "CreateProcess refused to start a host outside the agent's Job (Win32 error {Error}): if the Job forbids leaving it, this game " +
            "server ends with the agent instead of outliving it; breakaway is tried again at the next start")]
    private partial void LogNoBreakaway(int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host process {HostProcessId} could not be ended (TerminateProcess: {Terminated}, Win32 error {Error}) and is still running")]
    private partial void LogNotKilled(int hostProcessId, bool terminated, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not tell whether the host process {HostProcessId} runs ({Reason})")]
    private partial void LogNotChecked(int hostProcessId, string reason);
}
