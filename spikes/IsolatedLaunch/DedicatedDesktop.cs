using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.StationsAndDesktops;

namespace IsolatedLaunch;

/// <summary>
/// A window station + desktop whose DACL lets the server account initialize user32 from session 0
/// (otherwise processes launched with another token can fail with STATUS_DLL_INIT_FAILED, 0xC0000142).
/// </summary>
internal sealed class DedicatedDesktop : IDisposable
{
    private const uint WinstaAllAccess = 0x0000037F;
    private const uint GenericAll = 0x10000000;
    private const uint SddlRevision1 = 1;

    private readonly HWINSTA _station;
    private readonly HDESK _desktop;

    private DedicatedDesktop(string name, HWINSTA station, HDESK desktop)
    {
        Name = name;
        _station = station;
        _desktop = desktop;
    }

    /// <summary>Value for STARTUPINFO.lpDesktop ("station\desktop").</summary>
    public string Name { get; }

    public static unsafe DedicatedDesktop Create(string stationName, SecurityIdentifier allowed, Action<string> log)
    {
        var sddl = $"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{allowed.Value})";
        PSECURITY_DESCRIPTOR descriptor;
        fixed (char* sddlPtr = sddl)
        {
            if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(sddlPtr, SddlRevision1, &descriptor, null))
            {
                throw new InvalidOperationException($"ConvertStringSecurityDescriptorToSecurityDescriptor failed: {Marshal.GetLastPInvokeErrorMessage()}");
            }
        }

        try
        {
            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = (uint)sizeof(SECURITY_ATTRIBUTES),
                lpSecurityDescriptor = descriptor.Value,
                bInheritHandle = false,
            };

            HWINSTA station;
            fixed (char* stationPtr = stationName)
            {
                station = PInvoke.CreateWindowStation(stationPtr, 0, WinstaAllAccess, &attributes);
            }

            if (station.IsNull)
            {
                throw new InvalidOperationException($"CreateWindowStation failed: {Marshal.GetLastPInvokeErrorMessage()}");
            }

            // CreateDesktop creates the desktop in the calling process's window station.
            var original = PInvoke.GetProcessWindowStation();
            HDESK desktop;
            try
            {
                if (!PInvoke.SetProcessWindowStation(station))
                {
                    throw new InvalidOperationException($"SetProcessWindowStation failed: {Marshal.GetLastPInvokeErrorMessage()}");
                }

                fixed (char* desktopPtr = "Default")
                {
                    desktop = PInvoke.CreateDesktop(desktopPtr, null, null, 0, GenericAll, &attributes);
                }

                if (desktop.IsNull)
                {
                    throw new InvalidOperationException($"CreateDesktop failed: {Marshal.GetLastPInvokeErrorMessage()}");
                }
            }
            finally
            {
                PInvoke.SetProcessWindowStation(original);
            }

            log($"created window station {stationName} with desktop Default for {allowed}");
            return new DedicatedDesktop($"{stationName}\\Default", station, desktop);
        }
        finally
        {
            PInvoke.LocalFree(new HLOCAL(descriptor.Value));
        }
    }

    public void Dispose()
    {
        PInvoke.CloseDesktop(_desktop);
        PInvoke.CloseWindowStation(_station);
    }
}
