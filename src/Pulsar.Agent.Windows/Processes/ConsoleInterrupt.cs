using System.Runtime.InteropServices;
using Pulsar.Agent.Core.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// Ctrl+C for a game run with pipes that has a console of its own (an Unreal Engine server started with <c>-log</c>
/// opens a console window; a console program run with pipes gets one without a window): attach to that console, signal
/// every process on it, detach. Only a game's host does it (docs/architecture/06, "Server hosts"): it has no console to
/// leave (created detached), and it starts no other process after its game, so it can ignore Ctrl+C from then on, which
/// the signal it sends needs (a handler returning true does not keep Windows from ending the sender) and which its
/// children would inherit.
/// </summary>
internal static class ConsoleInterrupt
{
    private const uint CtrlCEvent = 0;

    private static readonly Lock s_lock = new();

    /// <summary>Signals the console of <paramref name="processId"/>, whose handle the caller holds (so the id is still its).</summary>
    public static void Send(int processId)
    {
        lock (s_lock)
        {
            // The signal goes to this process too: it must not end it.
            Check(PInvoke.SetConsoleCtrlHandler(null, true), "SetConsoleCtrlHandler");
            if (!PInvoke.AttachConsole((uint)processId))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new GameProcessException("process.no_console", error switch
                {
                    (int)WIN32_ERROR.ERROR_INVALID_HANDLE => "The game has no console to send Ctrl+C to (Unreal Engine servers open one with -log).",
                    (int)WIN32_ERROR.ERROR_ACCESS_DENIED => "Ctrl+C reaches a game run with pipes only from a process without a console of its own.",
                    _ => $"AttachConsole failed (Win32 error {error}).",
                });
            }

            try
            {
                // Every process attached to the console: the game, its children and this one.
                Check(PInvoke.GenerateConsoleCtrlEvent(CtrlCEvent, 0), "GenerateConsoleCtrlEvent");
            }
            finally
            {
                _ = PInvoke.FreeConsole();
            }
        }
    }

    private static void Check(BOOL result, string function)
    {
        if (!result)
        {
            throw new GameProcessException("process.interrupt_failed", $"{function} failed (Win32 error {Marshal.GetLastPInvokeError()}).");
        }
    }
}
