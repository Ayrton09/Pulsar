using System.ComponentModel;
using System.Diagnostics;
using Windows.Win32.Foundation;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// The lock around setting a server account's password and logging on with it (docs/architecture/06, "Server hosts"),
/// shared by the agent and its host processes: a file per account in the agent's state directory
/// (<c>state\logon-locks\&lt;account&gt;.lock</c>, SYSTEM and Administrators only), held open with no sharing. A server's
/// account can neither name nor reach it, unlike a named kernel object, which any account can create first and hold.
/// The handle closes with its process, so a process that dies holding the lock does not keep it.
/// </summary>
internal static class LogonLock
{
    private const int SharingViolation = 32; // ERROR_SHARING_VIOLATION
    private const int LockViolation = 33; // ERROR_LOCK_VIOLATION

    /// <summary>Waits up to <paramref name="timeout"/> for the lock of <paramref name="account"/>; disposing releases it.</summary>
    /// <exception cref="Win32Exception">ERROR_TIMEOUT: another logon of the account did not finish in time.</exception>
    public static IDisposable Acquire(string directory, string account, TimeSpan timeout)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, account + ".lock");
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is SharingViolation or LockViolation)
            {
                if (waited.Elapsed >= timeout)
                {
                    throw new Win32Exception((int)WIN32_ERROR.ERROR_TIMEOUT, $"Another logon of {account} did not finish in time.");
                }

                Thread.Sleep(50);
            }
        }
    }
}
