using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Servers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Console;
using Windows.Win32.System.Threading;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// Starts server processes following docs/architecture/06 ("Start sequence"): validated paths, a Job the process is
/// born into (job list attribute, created suspended), the server's account with a restricted token and its own profile,
/// window station and environment, and a pseudo console or pipes that hand the child only the handles it needs.
/// </summary>
public sealed partial class WindowsGameProcessManager(WindowsServerAccounts accounts, TimeProvider clock, ILogger<WindowsGameProcessManager> logger)
    : IGameProcessManager
{
    // PROC_THREAD_ATTRIBUTE_* are macros in the SDK headers, so CsWin32 does not generate them.
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const nuint ProcThreadAttributePseudoConsole = 0x00020016;

    /// <summary>Pseudo console size: wide and fixed, never resized per viewer (docs/architecture/06).</summary>
    private static readonly COORD s_consoleSize = new() { X = 220, Y = 50 };

    private static readonly WindowsServerFiles s_files = new();

    // Variables the server cannot override: the agent owns them (docs/architecture/04, "Environment").
    private static readonly HashSet<string> s_reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "ComSpec", "SystemRoot", "windir", "PSModulePath", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
    };

    static WindowsGameProcessManager()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // "Ignore Ctrl+C" is inherited by child processes: a parent started in a new process group (a test host, some
        // service wrappers) would otherwise pass it on, and Ctrl+C would never reach a server. Handlers the agent
        // registered (the host's own) keep working; a service has no console for it to matter.
        _ = PInvoke.SetConsoleCtrlHandler(null, false);
    }

    /// <summary>The server's temporary directory, inside its own directory (docs/architecture/06).</summary>
    public static string TempDirectory(string serverRoot) => Path.Combine(serverRoot, ".pulsar", "temp");

    public Task<IGameProcess> StartAsync(ProcessLaunchSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();
        var root = ServerPaths.NormalizeRoot(spec.ServerRoot);
        var executable = ServerPaths.ResolveFile(root, spec.Executable);
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new GameProcessException("process.invalid_executable", "Only .exe files can be started.");
        }

        var workingDirectory = ServerPaths.ResolveDirectory(root, spec.WorkingDirectory);
        var temp = TempDirectory(root);
        // The server's account controls .pulsar: created level by level without following a junction (doc 06).
        s_files.CreateDirectory(root, ".pulsar/temp");

        var resources = new List<IDisposable>();
        try
        {
            AccountLogon? logon = null;
            string? desktop = null;
            IReadOnlyDictionary<string, string> baseEnvironment;
            if (spec.Identity is ProcessIdentity.AccountIdentity account)
            {
                logon = accounts.LogOn(account.Name);
                resources.Add(logon);
                var station = ServerWindowStation.Create($"Pulsar-{spec.ServerId:N}", logon.Sid);
                resources.Add(station);
                desktop = station.DesktopName;
                baseEnvironment = logon.Environment;
            }
            else
            {
                baseEnvironment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                    .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
            }

            var environment = BuildEnvironment(baseEnvironment, spec.Environment, temp);
            var commandLine = WindowsCommandLine.Build(executable, spec.Arguments);
            var process = Launch(spec, executable, workingDirectory, commandLine, environment, logon, desktop, [.. resources]);
            LogStarted(spec.ServerId, process.ProcessId, spec.ConsoleMode, spec.Identity is ProcessIdentity.AccountIdentity a ? a.Name : Environment.UserName);
            return Task.FromResult<IGameProcess>(process);
        }
        catch (Exception ex)
        {
            foreach (var resource in resources)
            {
                resource.Dispose();
            }

            if (ex is Win32Exception win32)
            {
                throw new GameProcessException("process.start_failed", $"The process could not be started: {win32.Message} ({win32.NativeErrorCode})", win32);
            }

            throw;
        }
    }

    internal static string BuildEnvironment(IReadOnlyDictionary<string, string> baseEnvironment, IReadOnlyDictionary<string, string> overrides, string temp)
    {
        var variables = new SortedDictionary<string, string>(baseEnvironment.ToDictionary(), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in overrides)
        {
            if (s_reserved.Contains(name) || name.Length == 0 || name.Contains('=', StringComparison.Ordinal)
                || name.Contains('\0', StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new GameProcessException("process.invalid_environment", $"The environment variable '{name}' cannot be set.");
            }

            variables[name] = value;
        }

        variables["TEMP"] = temp;
        variables["TMP"] = temp;

        // Sorted, NUL-separated, double-NUL terminated (CREATE_UNICODE_ENVIRONMENT).
        var block = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    private unsafe WindowsGameProcess Launch(ProcessLaunchSpec spec, string executable, string workingDirectory, string commandLine,
        string environment, AccountLogon? logon, string? desktop, IDisposable[] resources)
    {
        var pty = spec.ConsoleMode == ConsoleMode.Pty;
        JobObject job;
        try
        {
            job = JobObject.Create(spec.Limits);
        }
        catch (Win32Exception ex)
        {
            throw new GameProcessException("process.limits_failed", $"The server's limits could not be applied: {ex.Message}", ex);
        }
        HANDLE inRead = default, inWrite = default, outRead = default, outWrite = default, errRead = default, errWrite = default;
        HPCON pseudoConsole = default;
        SafeFileHandle? process = null;
        try
        {
            // With a pseudo console nothing is inherited (it duplicates its ends); with pipes only the child ends are.
            var security = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), bInheritHandle = !pty };
            Check(PInvoke.CreatePipe(&inRead, &inWrite, &security, 0), "CreatePipe(input)");
            Check(PInvoke.CreatePipe(&outRead, &outWrite, &security, 0), "CreatePipe(output)");
            if (pty)
            {
                Marshal.ThrowExceptionForHR(PInvoke.CreatePseudoConsole(s_consoleSize, inRead, outWrite, 0, &pseudoConsole).Value);
            }
            else
            {
                Check(PInvoke.CreatePipe(&errRead, &errWrite, &security, 0), "CreatePipe(error)");
                NotInheritable(inWrite);
                NotInheritable(outRead);
                NotInheritable(errRead);
            }

            var info = CreateProcess(executable, workingDirectory, commandLine, environment, logon, desktop, job, pty, pseudoConsole,
                inRead, outWrite, errWrite);
            process = new SafeFileHandle((nint)info.hProcess.Value, ownsHandle: true);
            using (var thread = new SafeFileHandle((nint)info.hThread.Value, ownsHandle: true))
            {
                if (!job.Contains(process))
                {
                    // Never resumed outside the job: the attribute should have placed it there already.
                    job.Assign(process);
                }

                if (PInvoke.ResumeThread(thread) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "ResumeThread failed.");
                }
            }

            // The child (or the pseudo console) holds its own ends now.
            Close(ref inRead);
            Close(ref outWrite);
            Close(ref errWrite);

            var outputEncoding = pty ? Encoding.UTF8 : PipeEncoding(spec.Encoding);
            var input = new FileStream(new SafeFileHandle((nint)inWrite.Value, ownsHandle: true), FileAccess.Write, 1);
            inWrite = default;
            var result = new WindowsGameProcess(job, process, (int)info.dwProcessId, input, pty ? new UTF8Encoding(false) : outputEncoding,
                pseudoConsole, clock, resources);
            result.StartReader(new SafeFileHandle((nint)outRead.Value, ownsHandle: true), outputEncoding, "out");
            outRead = default;
            if (!pty)
            {
                result.StartReader(new SafeFileHandle((nint)errRead.Value, ownsHandle: true), outputEncoding, "err");
                errRead = default;
            }

            result.WatchExit();
            return result;
        }
        catch
        {
            job.Terminate(1);
            job.Dispose();
            process?.Dispose();
            if (pseudoConsole != default)
            {
                PInvoke.ClosePseudoConsole(pseudoConsole);
            }

            Close(ref inRead);
            Close(ref inWrite);
            Close(ref outRead);
            Close(ref outWrite);
            Close(ref errRead);
            Close(ref errWrite);
            throw;
        }
    }

    private static unsafe PROCESS_INFORMATION CreateProcess(string executable, string workingDirectory, string commandLine, string environment,
        AccountLogon? logon, string? desktop, JobObject job, bool pty, HPCON pseudoConsole, HANDLE inRead, HANDLE outWrite, HANDLE errWrite)
    {
        nuint size = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 2, 0, &size); // fails by design and returns the size
        var buffer = Marshal.AllocHGlobal((nint)size);
        var attributes = new LPPROC_THREAD_ATTRIBUTE_LIST((void*)buffer);
        try
        {
            Check(PInvoke.InitializeProcThreadAttributeList(attributes, 2, 0, &size), "InitializeProcThreadAttributeList");
            try
            {
                // Born inside the job: no instruction runs outside it (Windows 10+).
                var jobHandle = (HANDLE)job.Handle.DangerousGetHandle();
                Check(PInvoke.UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeJobList, &jobHandle, (nuint)sizeof(HANDLE), null, null),
                    "UpdateProcThreadAttribute(job list)");

                var inherited = stackalloc HANDLE[] { inRead, outWrite, errWrite };
                if (pty)
                {
                    Check(PInvoke.UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, (void*)pseudoConsole.Value,
                        (nuint)sizeof(HPCON), null, null), "UpdateProcThreadAttribute(pseudo console)");
                }
                else
                {
                    // Only these three handles are inherited, whatever else the agent has open as inheritable.
                    Check(PInvoke.UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, inherited, (nuint)(3 * sizeof(HANDLE)), null, null),
                        "UpdateProcThreadAttribute(handle list)");
                }

                var startup = new STARTUPINFOEXW { lpAttributeList = attributes };
                startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
                // In pty mode the null standard handles make the child use the pseudo console even when the agent has
                // standard handles of its own (a development agent in a console; spike 0001, F3).
                startup.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
                if (!pty)
                {
                    startup.StartupInfo.hStdInput = inRead;
                    startup.StartupInfo.hStdOutput = outWrite;
                    startup.StartupInfo.hStdError = errWrite;
                }

                var flags = PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED
                    | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT;
                if (!pty)
                {
                    flags |= PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW;
                }

                var command = (commandLine + '\0').ToCharArray();
                var desktopName = desktop is null ? null : (desktop + '\0').ToCharArray();
                PROCESS_INFORMATION info;
                fixed (char* application = executable)
                fixed (char* commandPtr = command)
                fixed (char* directory = workingDirectory)
                fixed (char* desktopPtr = desktopName)
                fixed (char* environmentPtr = environment)
                {
                    startup.StartupInfo.lpDesktop = desktopPtr;
                    if (logon is null)
                    {
                        Check(PInvoke.CreateProcess(application, commandPtr, null, null, !pty, flags, environmentPtr, directory,
                            (STARTUPINFOW*)&startup, &info), "CreateProcess");
                    }
                    else
                    {
                        Check(PInvoke.CreateProcessAsUser(logon.Token, application, commandPtr, null, null, !pty, flags, environmentPtr, directory,
                            (STARTUPINFOW*)&startup, &info), "CreateProcessAsUser");
                    }
                }

                return info;
            }
            finally
            {
                PInvoke.DeleteProcThreadAttributeList(attributes);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Encoding PipeEncoding(ConsoleEncoding encoding) => encoding switch
    {
        ConsoleEncoding.Oem => Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
        ConsoleEncoding.Ansi => Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage),
        _ => new UTF8Encoding(false),
    };

    private static void NotInheritable(HANDLE handle) =>
        Check(PInvoke.SetHandleInformation(handle, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0), "SetHandleInformation");

    private static void Close(ref HANDLE handle)
    {
        if (!handle.IsNull)
        {
            PInvoke.CloseHandle(handle);
            handle = default;
        }
    }

    private static void Check(BOOL result, string call)
    {
        if (!result)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{call} failed.");
        }
    }

    public int TerminateStrayProcesses(ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity is not ProcessIdentity.AccountIdentity account || !WindowsServerAccounts.Exists(account.Name))
        {
            return 0;
        }

        var sid = (SecurityIdentifier)new NTAccount(account.Name).Translate(typeof(SecurityIdentifier));
        var killed = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (ProcessUser(process.Id) == sid)
                    {
                        process.Kill(entireProcessTree: false);
                        killed++;
                        LogStrayKilled(account.Name, process.Id, process.ProcessName);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Gone already, or a protected process (never a server account's).
                }
            }
        }

        return killed;
    }

    private static unsafe SecurityIdentifier? ProcessUser(int processId)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsInvalid || !PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return null;
        }

        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            return identity.User;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killed process {ProcessId} ({ProcessName}) running as {Account} outside its server's job")]
    private partial void LogStrayKilled(string account, int processId, string processName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: started process {ProcessId} ({ConsoleMode}) as {Account}")]
    private partial void LogStarted(Guid serverId, int processId, ConsoleMode consoleMode, string account);
}
