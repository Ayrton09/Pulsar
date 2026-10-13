using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.System.Threading;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// The Job Object that holds one server's process tree (docs/architecture/06, ADR-0006). Unnamed, so nothing else can
/// open it; KILL_ON_JOB_CLOSE, so the tree dies with the agent; no breakaway, so no child can leave it.
/// </summary>
internal sealed class JobObject : IDisposable
{
    // Job notifications that a process was refused memory because of the job's (or a process's) memory limit (winnt.h).
    private const uint JobMessageProcessMemoryLimit = 9;
    private const uint JobMessageJobMemoryLimit = 10;

    // Always set: the tree dies with the agent, and a crash ends the process instead of showing a Windows Error
    // Reporting dialog nobody can close.
    private const JOB_OBJECT_LIMIT BaseLimits = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;

    private readonly SafeFileHandle _handle;
    private SafeFileHandle? _port;
    private volatile bool _reachedMemoryLimit;

    private JobObject(SafeFileHandle handle) => _handle = handle;

    public SafeHandle Handle => _handle;

    /// <summary>Whether a process of the job was refused memory because of the job's memory limit.</summary>
    public bool ReachedMemoryLimit => _reachedMemoryLimit;

    public static unsafe JobObject Create(ProcessLimits? limits = null)
    {
        var handle = PInvoke.CreateJobObject(null, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        var job = new JobObject(handle);
        try
        {
            job.SetLimits(limits);
            job.WatchNotifications();

            var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS
            {
                UIRestrictionsClass = JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_EXITWINDOWS
                    | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_GLOBALATOMS
                    | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_HANDLES
                    | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_READCLIPBOARD
                    | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_WRITECLIPBOARD
                    | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS
                    | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_DESKTOP
                    | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_DISPLAYSETTINGS,
            };
            job.Set(JOBOBJECTINFOCLASS.JobObjectBasicUIRestrictions, &ui, (uint)sizeof(JOBOBJECT_BASIC_UI_RESTRICTIONS));
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    /// <summary>Processes alive in the job.</summary>
    public unsafe int ActiveProcesses
    {
        get
        {
            Lease lease;
            try
            {
                lease = new Lease(_handle);
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }

            using (lease)
            {
                JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info;
                if (!PInvoke.QueryInformationJobObject(lease.Value, JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                        &info, (uint)sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), null))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject failed.");
                }

                return (int)info.ActiveProcesses;
            }
        }
    }

    /// <summary>
    /// Keeps the job's handle open for one call that needs the raw value: a <see cref="Dispose"/> meanwhile waits for the
    /// last lease to end, so the value is never used after the handle was closed (when another job may have been given
    /// the same value).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The job is disposed.</exception>
    private readonly struct Lease : IDisposable
    {
        private readonly SafeHandle _handle;

        public Lease(SafeHandle handle)
        {
            var added = false;
            handle.DangerousAddRef(ref added);
            _handle = handle;
            Value = (HANDLE)handle.DangerousGetHandle();
        }

        public HANDLE Value { get; }

        public void Dispose() => _handle.DangerousRelease();
    }

    /// <summary>
    /// CPU time from the job's accounting (every process it ever held), and the working set of the processes alive in it.
    /// A process that exits between listing and opening it simply does not count.
    /// </summary>
    public unsafe ProcessUsage Usage()
    {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting;
        using (var lease = new Lease(_handle))
        {
            if (!PInvoke.QueryInformationJobObject(lease.Value, JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                    &accounting, (uint)sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), null))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject failed.");
            }
        }

        var cpu = TimeSpan.FromTicks(accounting.TotalUserTime + accounting.TotalKernelTime);
        var memory = 0L;
        var committed = 0L;
        foreach (var processId in ProcessIds())
        {
            using var process = PInvoke.OpenProcess_SafeHandle(
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, processId);
            PROCESS_MEMORY_COUNTERS counters;
            if (!process.IsInvalid
                && PInvoke.GetProcessMemoryInfo((HANDLE)process.DangerousGetHandle(), &counters, (uint)sizeof(PROCESS_MEMORY_COUNTERS)))
            {
                memory += (long)counters.WorkingSetSize;
                committed += (long)counters.PagefileUsage; // the process's commit charge
            }
        }

        return new ProcessUsage(cpu, memory, (int)accounting.ActiveProcesses, committed);
    }

    private unsafe List<uint> ProcessIds()
    {
        // The list is a header followed by the ids; grown when the job holds more processes than fit.
        for (var capacity = 32; ; capacity *= 4)
        {
            var size = sizeof(JOBOBJECT_BASIC_PROCESS_ID_LIST) + ((capacity - 1) * sizeof(nuint));
            var buffer = new byte[size];
            fixed (byte* pointer = buffer)
            {
                var list = (JOBOBJECT_BASIC_PROCESS_ID_LIST*)pointer;
                bool queried;
                var error = 0;
                using (var lease = new Lease(_handle))
                {
                    queried = PInvoke.QueryInformationJobObject(lease.Value, JOBOBJECTINFOCLASS.JobObjectBasicProcessIdList, pointer, (uint)size, null);
                    if (!queried)
                    {
                        error = Marshal.GetLastPInvokeError(); // before the lease ends: releasing a handle may close it
                    }
                }

                if (!queried)
                {
                    if (error == (int)WIN32_ERROR.ERROR_MORE_DATA)
                    {
                        continue;
                    }

                    throw new Win32Exception(error, "QueryInformationJobObject failed.");
                }

                var ids = new List<uint>((int)list->NumberOfProcessIdsInList);
                var first = (nuint*)&list->ProcessIdList;
                for (var i = 0; i < list->NumberOfProcessIdsInList; i++)
                {
                    ids.Add((uint)first[i]);
                }

                return ids;
            }
        }
    }

    public unsafe bool Contains(SafeHandle process)
    {
        BOOL result;
        using var lease = new Lease(_handle);
        if (!PInvoke.IsProcessInJob((HANDLE)process.DangerousGetHandle(), lease.Value, &result))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "IsProcessInJob failed.");
        }

        return result;
    }

    /// <summary>Second line of defense if the job list attribute did not apply: called before the process resumes.</summary>
    public void Assign(SafeHandle process)
    {
        if (!PInvoke.AssignProcessToJobObject(_handle, process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed.");
        }
    }

    /// <summary>Kills every process in the job at once. Harmless when the job is already empty.</summary>
    public void Terminate(uint exitCode)
    {
        if (!_handle.IsClosed && !PInvoke.TerminateJobObject(_handle, exitCode))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "TerminateJobObject failed.");
        }
    }

    /// <summary>
    /// Sets (or removes, for null fields) the CPU and memory caps (docs/architecture/06, "Resource limits"). The CPU cap
    /// is a hard cap on the share of every processor; the memory cap counts the memory the whole tree commits, and a
    /// process asking for more is refused it (most games then crash).
    /// </summary>
    public unsafe void SetLimits(ProcessLimits? limits)
    {
        var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        extended.BasicLimitInformation.LimitFlags = BaseLimits;
        if (limits?.MemoryBytes is { } memory)
        {
            extended.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_MEMORY;
            extended.JobMemoryLimit = (nuint)memory;
        }

        Set(JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &extended, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));

        // Every job has a hard cap, the whole machine when there is no limit, so a cap can always be changed or removed
        // later (Windows refuses to turn rate control off on a job that never had it). Programs that size their threads
        // by the processors they may use (.NET does) see the cap in force when they start.
        var cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
        };
        // Cycles per 10,000 of all processors in each scheduling interval.
        cpu.Anonymous.CpuRate = (uint)Math.Clamp(limits?.CpuPercent ?? 100, 1, 100) * 100;
        Set(JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation, &cpu, (uint)sizeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION));
    }

    public void Dispose()
    {
        _handle.Dispose();
        // Ends the notification thread (its wait fails with ERROR_ABANDONED_WAIT_0).
        _port?.Dispose();
    }

    /// <summary>
    /// Associates a completion port with the job and reads its notifications on a thread of its own, to learn when a
    /// process is refused memory by the memory cap (the only notification used; the others are read and dropped).
    /// </summary>
    private unsafe void WatchNotifications()
    {
        var port = PInvoke.CreateIoCompletionPort(new SafeFileHandle(-1, ownsHandle: false), null, 0, 1);
        if (port.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort failed.");
        }

        _port = port;
        var association = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT { CompletionKey = null, CompletionPort = (HANDLE)port.DangerousGetHandle() };
        Set(JOBOBJECTINFOCLASS.JobObjectAssociateCompletionPortInformation, &association, (uint)sizeof(JOBOBJECT_ASSOCIATE_COMPLETION_PORT));
        // A handle that does not own (or reference) the port: closing the port is what ends the wait.
        var handle = new SafeFileHandle(port.DangerousGetHandle(), ownsHandle: false);
        new Thread(() => ReadNotifications(handle)) { IsBackground = true, Name = "pulsar-job-notifications" }.Start();
    }

    private unsafe void ReadNotifications(SafeFileHandle port)
    {
        while (true)
        {
            if (!PInvoke.GetQueuedCompletionStatus(port, out var message, out _, out _, uint.MaxValue))
            {
                return; // the port was closed: the job is gone
            }

            if (message is JobMessageJobMemoryLimit or JobMessageProcessMemoryLimit)
            {
                _reachedMemoryLimit = true;
            }
        }
    }

    private unsafe void Set(JOBOBJECTINFOCLASS type, void* info, uint size)
    {
        using var lease = new Lease(_handle);
        if (!PInvoke.SetInformationJobObject(lease.Value, type, info, size))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"SetInformationJobObject({type}) failed.");
        }
    }
}
