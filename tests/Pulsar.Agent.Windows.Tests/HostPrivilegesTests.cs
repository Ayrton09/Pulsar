using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Windows.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>A host removes, for good, the privileges it never uses, before its game's console host starts with its token.</summary>
public sealed class HostPrivilegesTests
{
    [WindowsFact]
    public void TheUnneededPrivilegesAreRemovedNotJustDisabled()
    {
        // A copy of this process's token: what any account has (shutting down, the time zone) is among them.
        using var token = Copy();

        var removed = HostPrivileges.Remove(token);

        Assert.Contains("SeShutdownPrivilege", removed);
        Assert.Contains("SeTimeZonePrivilege", removed);
        // Removed, not disabled: nothing could enable them again, so a second pass finds none.
        Assert.Empty(HostPrivileges.Remove(token));
        // What starting a game takes is kept.
        Assert.DoesNotContain("SeChangeNotifyPrivilege", HostPrivileges.Unneeded);
        Assert.DoesNotContain("SeAssignPrimaryTokenPrivilege", HostPrivileges.Unneeded);
        Assert.DoesNotContain("SeIncreaseQuotaPrivilege", HostPrivileges.Unneeded);
        Assert.DoesNotContain("SeBackupPrivilege", HostPrivileges.Unneeded);
        Assert.DoesNotContain("SeRestorePrivilege", HostPrivileges.Unneeded);
    }

    private static unsafe SafeAccessTokenHandle Copy()
    {
        HANDLE process;
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), TOKEN_ACCESS_MASK.TOKEN_DUPLICATE | TOKEN_ACCESS_MASK.TOKEN_QUERY, &process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using var own = new SafeAccessTokenHandle((nint)process.Value);
        HANDLE copy;
        if (!PInvoke.DuplicateTokenEx((HANDLE)own.DangerousGetHandle(), TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, null,
            SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, &copy))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new SafeAccessTokenHandle((nint)copy.Value);
    }
}
