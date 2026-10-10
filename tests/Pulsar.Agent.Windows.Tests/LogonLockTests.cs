using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// The lock around a server account's password change and logon (docs/architecture/06, "Server hosts"): shared by the
/// agent and its hosts, and never left held by a process that died.
/// </summary>
public sealed class LogonLockTests : IDisposable
{
    private readonly string _locks = Directory.CreateTempSubdirectory("pulsar-logon-locks-").FullName;

    public void Dispose() => Directory.Delete(_locks, recursive: true);

    [WindowsFact]
    public async Task LogonsOfTheSameAccountTakeTurns()
    {
        var first = LogonLock.Acquire(_locks, "pulsar_abcd2345", TimeSpan.FromSeconds(5));
        var error = Assert.Throws<Win32Exception>(() => LogonLock.Acquire(_locks, "pulsar_abcd2345", TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1460, error.NativeErrorCode); // ERROR_TIMEOUT

        using (LogonLock.Acquire(_locks, "pulsar_other123", TimeSpan.FromMilliseconds(200)))
        {
            // Another account's logon does not wait.
        }

        var second = Task.Run(() => LogonLock.Acquire(_locks, "pulsar_abcd2345", TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Dispose();
    }

    [WindowsFact]
    public void DeletingAnAccountThatIsAlreadyGoneDeletesItsLockFile()
    {
        // A delete retried after the account went (the agent stopped right after removing it): its lock goes too.
        var file = Path.Combine(_locks, "pulsar_gone2345.lock");
        File.WriteAllText(file, string.Empty);
        var accounts = new WindowsServerAccounts(NullLogger<WindowsServerAccounts>.Instance, _locks);

        Assert.False(accounts.Delete("pulsar_gone2345"));

        Assert.False(File.Exists(file));
    }

    [WindowsFact]
    public async Task ALockFileLeftBehindOrHeldByAProcessThatDiedDoesNotBlock()
    {
        var file = Path.Combine(_locks, "pulsar_abcd2345.lock");
        File.WriteAllText(file, string.Empty); // left by an earlier logon
        LogonLock.Acquire(_locks, "pulsar_abcd2345", TimeSpan.FromSeconds(1)).Dispose();

        // Another process holds it, then dies without letting go.
        using var holder = Process.Start(new ProcessStartInfo("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command",
                $"$f = [IO.File]::Open('{file}', 'OpenOrCreate', 'ReadWrite', 'None'); 'held'; Start-Sleep -Seconds 120"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            Assert.Equal("held", await holder.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
            Assert.Throws<Win32Exception>(() => LogonLock.Acquire(_locks, "pulsar_abcd2345", TimeSpan.FromMilliseconds(200)));

            holder.Kill();
            await holder.WaitForExitAsync(TestContext.Current.CancellationToken);

            LogonLock.Acquire(_locks, "pulsar_abcd2345", TimeSpan.FromSeconds(5)).Dispose();
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill();
            }
        }
    }
}
