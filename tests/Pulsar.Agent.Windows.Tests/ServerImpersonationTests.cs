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
    public void OnlyADevelopmentAgentActsAsItsOwnAccount()
    {
        // For the service that account is SYSTEM: a server without an account of its own gets nothing done with it.
        using var production = new ServerImpersonation(_ => throw new InvalidOperationException("No logon expected."), TimeProvider.System);
        var refused = Assert.Throws<System.ComponentModel.Win32Exception>(() => production.Run(ProcessIdentity.CurrentUser, () => 1));
        Assert.Equal(5, refused.NativeErrorCode); // access denied, as the file manager reports a logon that failed

        using var development = new ServerImpersonation(_ => throw new InvalidOperationException("No logon expected."), TimeProvider.System,
            development: true);
        Assert.Equal(1, development.Run(ProcessIdentity.CurrentUser, () => 1));
    }

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

    [WindowsFact]
    public void AnIdleTokenIsClosedByTheTimerWithoutAnyOtherCall()
    {
        var clock = new ManualClock();
        var tokens = new List<SafeAccessTokenHandle>();
        using var impersonation = new ServerImpersonation(_ => Track(tokens), clock);
        impersonation.Run(ProcessIdentity.Account("pulsar_idle"), () => 1);
        Assert.False(tokens[0].IsClosed); // kept for the next click

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(tokens[0].IsClosed);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.True(tokens[0].IsClosed); // nobody asked for anything: the timer closed it
    }

    [WindowsFact]
    public void AnotherAccountsFileOperationEndingClosesTheTokensThatWentIdle()
    {
        var clock = new ManualClock(startTimers: false);
        var tokens = new List<SafeAccessTokenHandle>();
        using var impersonation = new ServerImpersonation(_ => Track(tokens), clock);
        impersonation.Run(ProcessIdentity.Account("pulsar_first"), () => 1);

        // The second account's long operation: the first one's token goes idle meanwhile, and nothing acquires after that.
        impersonation.Run(ProcessIdentity.Account("pulsar_second"), () =>
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            return 2;
        });

        Assert.True(tokens[0].IsClosed);
        Assert.False(tokens[1].IsClosed); // just used
    }

    [WindowsFact]
    public void ADeletedAccountsTokenIsDroppedAtOnce()
    {
        var tokens = new List<SafeAccessTokenHandle>();
        using var accounts = new Scratch();
        using var impersonation = new ServerImpersonation(_ => Track(tokens), TimeProvider.System, accounts.Accounts);
        impersonation.Run(ProcessIdentity.Account("pulsar_gone1"), () => 1);
        impersonation.Run(ProcessIdentity.Account("pulsar_kept1"), () => 1);

        accounts.Accounts.Delete("pulsar_gone1"); // does not exist: nothing to delete, but its token must go all the same

        Assert.True(tokens[0].IsClosed);
        Assert.False(tokens[1].IsClosed);
        impersonation.Run(ProcessIdentity.Account("pulsar_gone1"), () => 1);
        Assert.Equal(3, tokens.Count); // a new logon, not the old token
    }

    [WindowsFact]
    public void ATokenInUseWhenItsAccountIsDeletedEndsWithTheOperation()
    {
        var tokens = new List<SafeAccessTokenHandle>();
        using var impersonation = new ServerImpersonation(_ => Track(tokens), TimeProvider.System);

        impersonation.Run(ProcessIdentity.Account("pulsar_busy1"), () =>
        {
            impersonation.Forget("pulsar_busy1");
            Assert.False(tokens[0].IsClosed); // still being used
            return 1;
        });

        Assert.True(tokens[0].IsClosed);
    }

    private static SafeAccessTokenHandle Track(List<SafeAccessTokenHandle> tokens)
    {
        var token = CurrentToken();
        tokens.Add(token);
        return token;
    }

    // A token of this test's own user stands in for a server account's (impersonating it changes nothing).
    private static SafeAccessTokenHandle CurrentToken() => WindowsIdentity.GetCurrent().AccessToken;

    /// <summary>Time the test moves; its timers fire when it passes.</summary>
    private sealed class ManualClock(bool startTimers = true) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, period);
            if (startTimers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.ToList())
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
        {
            private bool _disposed;

            public TimeSpan Period { get; } = period;

            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A WindowsServerAccounts with logon locks in a folder of its own (deleting an account that does not exist needs no rights).</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly string _locks = Directory.CreateTempSubdirectory("pulsar-logon-locks-").FullName;

        public Scratch() => Accounts = new WindowsServerAccounts(Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsServerAccounts>.Instance, _locks);

        public WindowsServerAccounts Accounts { get; }

        public void Dispose() => Directory.Delete(_locks, recursive: true);
    }
}
