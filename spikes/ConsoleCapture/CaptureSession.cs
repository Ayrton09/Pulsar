using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Console;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace ConsoleCapture;

/// <summary>
/// Launches one process inside a new Job Object (KILL_ON_JOB_CLOSE), created suspended and placed in the job
/// through PROC_THREAD_ATTRIBUTE_JOB_LIST, with its console attached either to a ConPTY or to redirected pipes.
/// </summary>
internal sealed class CaptureSession : IDisposable
{
    // PROC_THREAD_ATTRIBUTE_* are macros in the SDK headers, so CsWin32 does not emit them.
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const nuint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly CaptureOptions _options;
    private readonly Recorder _recorder;
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _process;
    private readonly FileStream _input;
    private readonly List<Thread> _readers = [];
    private HPCON _pseudoConsole;

    private CaptureSession(CaptureOptions options, Recorder recorder, SafeFileHandle job, SafeFileHandle process,
        int processId, bool isInJob, FileStream input, HPCON pseudoConsole)
    {
        _options = options;
        _recorder = recorder;
        _job = job;
        _process = process;
        ProcessId = processId;
        IsInJob = isInJob;
        _input = input;
        _pseudoConsole = pseudoConsole;
    }

    public int ProcessId { get; }
    public bool IsInJob { get; }

    public static unsafe CaptureSession Start(CaptureOptions options, Recorder recorder)
    {
        if (options.DetachStdHandles)
        {
            // Our own console echo stops working from here on; the JSONL capture is unaffected.
            PInvoke.SetStdHandle(STD_HANDLE.STD_INPUT_HANDLE, HANDLE.Null);
            PInvoke.SetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE, HANDLE.Null);
            PInvoke.SetStdHandle(STD_HANDLE.STD_ERROR_HANDLE, HANDLE.Null);
        }

        var job = CreateKillOnCloseJob();
        var pty = options.Mode == CaptureMode.Pty;

        // Pipes: in pty mode the pseudo console duplicates the child ends; in pipes mode the child inherits them.
        var inheritable = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), bInheritHandle = !pty };
        HANDLE inRead, inWrite, outRead, outWrite, errRead = default, errWrite = default;
        Check(PInvoke.CreatePipe(&inRead, &inWrite, &inheritable, 0), "CreatePipe(stdin)");
        Check(PInvoke.CreatePipe(&outRead, &outWrite, &inheritable, 0), "CreatePipe(stdout)");
        if (!pty)
        {
            Check(PInvoke.CreatePipe(&errRead, &errWrite, &inheritable, 0), "CreatePipe(stderr)");
            // Our ends must never be inherited.
            Check(PInvoke.SetHandleInformation(inWrite, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0), "SetHandleInformation");
            Check(PInvoke.SetHandleInformation(outRead, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0), "SetHandleInformation");
            Check(PInvoke.SetHandleInformation(errRead, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0), "SetHandleInformation");
        }

        HPCON pseudoConsole = default;
        if (pty)
        {
            var hr = PInvoke.CreatePseudoConsole(new COORD { X = 220, Y = 50 }, inRead, outWrite, 0, &pseudoConsole);
            Marshal.ThrowExceptionForHR(hr.Value);
        }

        nuint size = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 2, 0, &size); // fails by design, returns the size
        var attributeBuffer = Marshal.AllocHGlobal((nint)size);
        var attributes = new LPPROC_THREAD_ATTRIBUTE_LIST((void*)attributeBuffer);
        PROCESS_INFORMATION processInfo;
        try
        {
            Check(PInvoke.InitializeProcThreadAttributeList(attributes, 2, 0, &size), "InitializeProcThreadAttributeList");

            var jobHandle = (HANDLE)job.DangerousGetHandle();
            Check(PInvoke.UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeJobList, &jobHandle, (nuint)sizeof(HANDLE), null, null),
                "UpdateProcThreadAttribute(JOB_LIST)");

            var inheritedHandles = stackalloc HANDLE[] { inRead, outWrite, errWrite };
            if (pty)
            {
                Check(PInvoke.UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole,
                    (void*)pseudoConsole.Value, (nuint)sizeof(HPCON), null, null), "UpdateProcThreadAttribute(PSEUDOCONSOLE)");
            }
            else
            {
                Check(PInvoke.UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList,
                    inheritedHandles, (nuint)(3 * sizeof(HANDLE)), null, null), "UpdateProcThreadAttribute(HANDLE_LIST)");
            }

            var startup = new STARTUPINFOEXW { lpAttributeList = attributes };
            startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
            if (!pty || options.UseStdHandlesInPty)
            {
                startup.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
                if (!pty)
                {
                    startup.StartupInfo.hStdInput = inRead;
                    startup.StartupInfo.hStdOutput = outWrite;
                    startup.StartupInfo.hStdError = errWrite;
                }
            }

            var flags = PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT
                | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED
                | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT;
            if (!pty)
            {
                flags |= PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW;
            }

            var commandLine = (CommandLine.Build(options.Executable, options.Arguments) + '\0').ToCharArray();
            var workingDirectory = options.WorkingDirectory ?? Path.GetDirectoryName(options.Executable)!;
            var desktopName = options.Desktop is null ? null : (options.Desktop + '\0').ToCharArray();
            fixed (char* application = options.Executable)
            fixed (char* command = commandLine)
            fixed (char* directory = workingDirectory)
            fixed (char* desktop = desktopName)
            {
                startup.StartupInfo.lpDesktop = desktop;
                if (options.UserToken == 0)
                {
                    Check(PInvoke.CreateProcess(application, command, null, null, !pty, flags, (void*)options.EnvironmentBlock,
                        directory, (STARTUPINFOW*)&startup, &processInfo), "CreateProcess");
                }
                else
                {
                    Check(PInvoke.CreateProcessAsUser((HANDLE)options.UserToken, application, command, null, null, !pty, flags,
                        (void*)options.EnvironmentBlock, directory, (STARTUPINFOW*)&startup, &processInfo), "CreateProcessAsUser");
                }
            }
        }
        finally
        {
            PInvoke.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributeBuffer);
        }

        var process = new SafeFileHandle((nint)processInfo.hProcess.Value, ownsHandle: true);
        BOOL inJob;
        Check(PInvoke.IsProcessInJob(processInfo.hProcess, (HANDLE)job.DangerousGetHandle(), &inJob), "IsProcessInJob");
        if (PInvoke.ResumeThread(processInfo.hThread) == uint.MaxValue) { throw new InvalidOperationException($"ResumeThread failed: {Marshal.GetLastPInvokeErrorMessage()}"); }
        PInvoke.CloseHandle(processInfo.hThread);

        // The child (or the pseudo console) owns its ends now.
        PInvoke.CloseHandle(inRead);
        PInvoke.CloseHandle(outWrite);
        if (!pty)
        {
            PInvoke.CloseHandle(errWrite);
        }

        var input = new FileStream(new SafeFileHandle((nint)inWrite.Value, true), FileAccess.Write, 1);
        var session = new CaptureSession(options, recorder, job, process, (int)processInfo.dwProcessId, inJob, input, pseudoConsole);
        session.StartReader("out", outRead);
        if (!pty)
        {
            session.StartReader("err", errRead);
        }

        return session;
    }

    public Task<uint> WaitForExitAsync() => Task.Factory.StartNew(() =>
    {
        PInvoke.WaitForSingleObject(_process, Infinite);
        PInvoke.GetExitCodeProcess(_process, out var code);
        return code;
    }, TaskCreationOptions.LongRunning);

    public void WriteInput(string text)
    {
        var bytes = (_options.Mode == CaptureMode.Pty ? Encoding.UTF8 : _options.PipeEncoding).GetBytes(text);
        _input.Write(bytes);
        _input.Flush();
    }

    public async Task<CaptureSummary> FinishAsync(Task<uint> exitTask)
    {
        var killed = false;
        double? exitMs = null;
        if (exitTask.IsCompleted)
        {
            exitMs = _recorder.Elapsed.TotalMilliseconds;
            _recorder.Event("exit", $"process exited with code {exitTask.Result}");
        }

        var accounting = QueryAccounting();
        if (!exitTask.IsCompleted || accounting.ActiveProcesses > 0)
        {
            _recorder.Event("kill", $"terminating job ({accounting.ActiveProcesses} active processes)");
            PInvoke.TerminateJobObject(_job, 1);
            killed = !exitTask.IsCompleted;
        }

        var exitCode = await exitTask;
        exitMs ??= _recorder.Elapsed.TotalMilliseconds;
        _input.Dispose();

        double? closeMs = null;
        if (_pseudoConsole != default)
        {
            // Measured because ClosePseudoConsole is known to block on older builds while output is undrained.
            var started = Stopwatch.StartNew();
            await Task.Run(() => PInvoke.ClosePseudoConsole(_pseudoConsole));
            closeMs = started.Elapsed.TotalMilliseconds;
            _pseudoConsole = default;
        }

        foreach (var reader in _readers)
        {
            reader.Join(TimeSpan.FromSeconds(5));
        }

        var final = QueryAccounting();
        return new CaptureSummary(
            _options.Mode.ToString(),
            _recorder.FirstOutputMs,
            _recorder.Chunks,
            _recorder.Bytes,
            _recorder.EscapeBytes,
            exitCode,
            Math.Round(exitMs.Value, 1),
            killed,
            final.TotalProcesses,
            final.ActiveProcesses,
            TimeSpan.FromTicks(final.TotalUserTime + final.TotalKernelTime).TotalSeconds,
            closeMs is null ? null : Math.Round(closeMs.Value, 1));
    }

    public void Dispose()
    {
        _input.Dispose();
        _process.Dispose();
        _job.Dispose(); // KILL_ON_JOB_CLOSE: anything still alive dies here.
    }

    private unsafe void StartReader(string stream, HANDLE handle)
    {
        var file = new FileStream(new SafeFileHandle((nint)handle.Value, true), FileAccess.Read, 1);
        var thread = new Thread(() =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = file.Read(buffer)) > 0)
                {
                    _recorder.Output(stream, buffer.AsSpan(0, read));
                }
            }
            catch (IOException ex)
            {
                _recorder.Event("reader", $"{stream} closed: {ex.Message}");
            }
            finally
            {
                file.Dispose();
            }
        })
        { IsBackground = true, Name = $"reader-{stream}" };
        _readers.Add(thread);
        thread.Start();
    }

    /// <summary>Lists the processes currently in the job with their image name and main window, if any.</summary>
    public unsafe IReadOnlyList<string> DescribeJobProcesses()
    {
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counters followed by an array of ULONG_PTR process ids.
        var buffer = stackalloc byte[4096];
        Check(PInvoke.QueryInformationJobObject((HANDLE)_job.DangerousGetHandle(),
            JOBOBJECTINFOCLASS.JobObjectBasicProcessIdList, buffer, 4096, null), "QueryInformationJobObject(ProcessIdList)");
        var count = *(uint*)(buffer + 4);
        var ids = (nuint*)(buffer + 8);
        var result = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var pid = (int)ids[i];
            try
            {
                using var process = Process.GetProcessById(pid);
                result.Add($"{pid}:{process.ProcessName} window=0x{process.MainWindowHandle:X} title='{process.MainWindowTitle}' path={process.MainModule?.FileName}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                result.Add($"{pid}:<{ex.GetType().Name}>");
            }
        }

        return result;
    }

    private unsafe JOBOBJECT_BASIC_ACCOUNTING_INFORMATION QueryAccounting()
    {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info;
        Check(PInvoke.QueryInformationJobObject((HANDLE)_job.DangerousGetHandle(),
            JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation, &info, (uint)sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), null),
            "QueryInformationJobObject");
        return info;
    }

    private static unsafe SafeFileHandle CreateKillOnCloseJob()
    {
        var job = PInvoke.CreateJobObject(null, null);
        if (job.IsInvalid)
        {
            throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        Check(PInvoke.SetInformationJobObject((HANDLE)job.DangerousGetHandle(), JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
            &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)), "SetInformationJobObject");
        return job;
    }

    private static void Check(BOOL result, string call)
    {
        if (!result)
        {
            throw new InvalidOperationException($"{call} failed: {Marshal.GetLastPInvokeErrorMessage()} ({Marshal.GetLastPInvokeError()})");
        }
    }
}
