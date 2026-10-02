using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// The Job Object that holds one server's process tree (docs/architecture/06, ADR-0006). Unnamed, so nothing else can
/// open it; KILL_ON_JOB_CLOSE, so the tree dies with the agent; no breakaway, so no child can leave it.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private readonly SafeFileHandle _handle;

    private JobObject(SafeFileHandle handle) => _handle = handle;

    public SafeHandle Handle => _handle;

    public static unsafe JobObject Create()
    {
        var handle = PInvoke.CreateJobObject(null, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        var job = new JobObject(handle);
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                // A crash ends the process instead of showing a Windows Error Reporting dialog nobody can close.
                | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
            job.Set(JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));

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
            if (_handle.IsClosed)
            {
                return 0;
            }

            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info;
            if (!PInvoke.QueryInformationJobObject((HANDLE)_handle.DangerousGetHandle(), JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                    &info, (uint)sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), null))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject failed.");
            }

            return (int)info.ActiveProcesses;
        }
    }

    public unsafe bool Contains(SafeHandle process)
    {
        BOOL result;
        if (!PInvoke.IsProcessInJob((HANDLE)process.DangerousGetHandle(), (HANDLE)_handle.DangerousGetHandle(), &result))
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

    public void Dispose() => _handle.Dispose();

    private unsafe void Set(JOBOBJECTINFOCLASS type, void* info, uint size)
    {
        if (!PInvoke.SetInformationJobObject((HANDLE)_handle.DangerousGetHandle(), type, info, size))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"SetInformationJobObject({type}) failed.");
        }
    }
}
