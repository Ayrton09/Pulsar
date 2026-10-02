using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.StationsAndDesktops;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// A window station and desktop for one server, open only to SYSTEM, Administrators and the server's account. A
/// process created with another token from a service can fail to initialize user32 (0xC0000142) on a desktop it cannot
/// access, and game servers create console windows (spike 0002); a private station also keeps one server's windows,
/// atoms and clipboard away from the others.
/// </summary>
internal sealed class ServerWindowStation : IDisposable
{
    private const uint WinstaAllAccess = 0x0000037F;
    private const uint GenericAll = 0x10000000;
    private const uint SddlRevision1 = 1;

    // CreateDesktop works on the calling process's window station, which is switched for the call.
    private static readonly Lock s_switchLock = new();

    private readonly HWINSTA _station;
    private readonly HDESK _desktop;

    private ServerWindowStation(string name, HWINSTA station, HDESK desktop)
    {
        DesktopName = name;
        _station = station;
        _desktop = desktop;
    }

    /// <summary>Value for <c>STARTUPINFO.lpDesktop</c> ("station\desktop").</summary>
    public string DesktopName { get; }

    public static unsafe ServerWindowStation Create(string stationName, SecurityIdentifier account)
    {
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{account.Value})";
        PSECURITY_DESCRIPTOR descriptor;
        fixed (char* sddlPtr = sddl)
        {
            if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(sddlPtr, SddlRevision1, &descriptor, null))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Invalid window station security descriptor.");
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
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateWindowStation({stationName}) failed.");
            }

            HDESK desktop;
            lock (s_switchLock)
            {
                var original = PInvoke.GetProcessWindowStation();
                if (!PInvoke.SetProcessWindowStation(station))
                {
                    var error = Marshal.GetLastPInvokeError();
                    PInvoke.CloseWindowStation(station);
                    throw new Win32Exception(error, "SetProcessWindowStation failed.");
                }

                try
                {
                    fixed (char* desktopPtr = "Default")
                    {
                        desktop = PInvoke.CreateDesktop(desktopPtr, null, null, 0, GenericAll, &attributes);
                    }
                }
                finally
                {
                    PInvoke.SetProcessWindowStation(original);
                }
            }

            if (desktop.IsNull)
            {
                var error = Marshal.GetLastPInvokeError();
                PInvoke.CloseWindowStation(station);
                throw new Win32Exception(error, "CreateDesktop failed.");
            }

            return new ServerWindowStation($"{stationName}\\Default", station, desktop);
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
