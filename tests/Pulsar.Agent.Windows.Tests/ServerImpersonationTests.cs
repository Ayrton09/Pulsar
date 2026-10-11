using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// The file manager's impersonation tokens (docs/architecture/08, "File manager", layer 4): one account's logon, which
/// may wait up to 30 seconds for its logon lock, never holds up the file operations of another server.
/// </summary>
public sealed class ServerImpersonationTests
{
    [WindowsFact]
    public async Task OneAccountsSlowLogonDoesNotHoldUpAnotherAccount()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var impersonation = new ServerImpersonation(name =>
        {
            if (name == "pulsar_slow")
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return CurrentToken();
        }, TimeProvider.System);

        var slow = Task.Run(() => impersonation.Run(ProcessIdentity.Account("pulsar_slow"), () => 1), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        try
        {
            var other = Task.Run(() => impersonation.Run(ProcessIdentity.Account("pulsar_other"), () => 2), TestContext.Current.CancellationToken);
            Assert.Equal(2, await other.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(1, await slow.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [WindowsFact]
    public async Task CallsForTheSameAccountShareOneLogon()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var logons = 0;
        using var impersonation = new ServerImpersonation(_ =>
        {
            Interlocked.Increment(ref logons);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return CurrentToken();
        }, TimeProvider.System);

        var first = Task.Run(() => impersonation.Run(ProcessIdentity.Account("pulsar_same"), () => 1), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var second = Task.Run(() => impersonation.Run(ProcessIdentity.Account("pulsar_same"), () => 2), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        release.Set();

        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, logons);
    }

    [WindowsFact]
    public void AFailedLogonIsNotCachedAndTheNextCallTriesAgain()
    {
        var logons = 0;
        using var impersonation = new ServerImpersonation(_ =>
            Interlocked.Increment(ref logons) == 1 ? throw new System.ComponentModel.Win32Exception(1326) : CurrentToken(), TimeProvider.System);

        Assert.Throws<System.ComponentModel.Win32Exception>(() => impersonation.Run(ProcessIdentity.Account("pulsar_flaky"), () => 1));
        Assert.Equal(1, impersonation.Run(ProcessIdentity.Account("pulsar_flaky"), () => 1));
        Assert.Equal(2, logons);
    }

    // A token of this test's own user stands in for a server account's (impersonating it changes nothing).
    private static SafeAccessTokenHandle CurrentToken() => WindowsIdentity.GetCurrent().AccessToken;
}
