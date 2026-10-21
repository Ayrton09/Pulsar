using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// What a server's host gives up as it starts (docs/architecture/08, "The pseudo console's host"): the console host a
/// pseudo console starts (<c>conhost.exe</c>) runs with the host's token, SYSTEM for the service, and reads what the game
/// writes, so a game that took it over would have what that token has. The host removes from its token, for good, every
/// privilege it never uses; what starting its one game needs stays (assigning the game's token and quotas, loading and
/// unloading its profile, which takes backup and restore).
/// </summary>
public static class HostPrivileges
{
    private const uint PrivilegeRemoved = 0x00000004; // SE_PRIVILEGE_REMOVED

    /// <summary>The privileges a host never uses (SYSTEM's that drivers, tokens, owners, audit and the machine need).</summary>
    internal static readonly string[] Unneeded =
    [
        "SeTcbPrivilege", "SeCreateTokenPrivilege", "SeDebugPrivilege", "SeLoadDriverPrivilege", "SeTakeOwnershipPrivilege",
        "SeSecurityPrivilege", "SeRelabelPrivilege", "SeManageVolumePrivilege", "SeSystemEnvironmentPrivilege",
        "SeCreatePermanentPrivilege", "SeCreatePagefilePrivilege", "SeLockMemoryPrivilege", "SeAuditPrivilege",
        "SeShutdownPrivilege", "SeRemoteShutdownPrivilege", "SeSystemtimePrivilege", "SeTimeZonePrivilege",
        "SeProfileSingleProcessPrivilege", "SeSystemProfilePrivilege", "SeUndockPrivilege", "SeCreateSymbolicLinkPrivilege",
        "SeDelegateSessionUserImpersonatePrivilege", "SeTrustedCredManAccessPrivilege", "SeEnableDelegationPrivilege",
        "SeMachineAccountPrivilege", "SeSyncAgentPrivilege",
    ];

    /// <summary>Removes <see cref="Unneeded"/> from this process's token: the ones it had.</summary>
    public static unsafe IReadOnlyList<string> RemoveFromProcess()
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken failed.");
        }

        using var handle = new SafeAccessTokenHandle((nint)token.Value);
        return Remove(handle);
    }

    /// <summary>Removes <see cref="Unneeded"/> from <paramref name="token"/> (opened to adjust privileges): the ones it had.</summary>
    internal static unsafe IReadOnlyList<string> Remove(SafeAccessTokenHandle token)
    {
        ArgumentNullException.ThrowIfNull(token);
        List<string> removed = [];
        foreach (var name in Unneeded)
        {
            if (!PInvoke.LookupPrivilegeValue(null, name, out var luid))
            {
                continue; // a privilege this Windows does not have
            }

            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            privileges.Privileges[0] = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = (TOKEN_PRIVILEGES_ATTRIBUTES)PrivilegeRemoved };
            if (!PInvoke.AdjustTokenPrivileges((HANDLE)token.DangerousGetHandle(), false, &privileges, 0, null, null))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"AdjustTokenPrivileges({name}) failed.");
            }

            // Success with ERROR_NOT_ALL_ASSIGNED: the token did not have it.
            if ((WIN32_ERROR)Marshal.GetLastPInvokeError() != WIN32_ERROR.ERROR_NOT_ALL_ASSIGNED)
            {
                removed.Add(name);
            }
        }

        return removed;
    }
}
