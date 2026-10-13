using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>Runs code in the agent as a server's identity (docs/architecture/08, "File manager", layer 4).</summary>
public interface IServerImpersonation
{
    /// <summary>
    /// Runs <paramref name="action"/> on this thread as <paramref name="identity"/>: the operating system checks every
    /// file it opens against that account's rights. Keep it short and synchronous (no awaits, no I/O on handles that
    /// do not need it).
    /// </summary>
    T Run<T>(ProcessIdentity identity, Func<T> action);
}

/// <summary>
/// Acts as each server's local account with a token from <see cref="WindowsServerAccounts.LogOnForImpersonation"/>,
/// kept for a minute after its last use so browsing a folder does not log the account on at every click. Callers for
/// one account share one logon, and it never holds up other accounts' file operations. A development agent's servers run
/// as the agent's own user: their actions run as they are.
/// </summary>
public sealed class ServerImpersonation : IServerImpersonation, IDisposable
{
    private static readonly TimeSpan s_keep = TimeSpan.FromMinutes(1);

    private readonly Func<string, SafeAccessTokenHandle> _logOn;
    private readonly TimeProvider _clock;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, CachedToken> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LogonGate> _gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly WindowsServerAccounts? _accounts;
    private readonly ITimer _sweep;

    public ServerImpersonation(WindowsServerAccounts accounts, TimeProvider clock)
        : this(accounts.LogOnForImpersonation, clock, accounts)
    {
    }

    /// <param name="logOn">Tests: logs an account on in place of <see cref="WindowsServerAccounts.LogOnForImpersonation"/>.</param>
    /// <param name="accounts">The accounts whose deletion drops their cached token.</param>
    internal ServerImpersonation(Func<string, SafeAccessTokenHandle> logOn, TimeProvider clock, WindowsServerAccounts? accounts = null)
    {
        (_logOn, _clock, _accounts) = (logOn, clock, accounts);
        if (accounts is not null)
        {
            accounts.Deleted += Forget;
        }

        // A token nobody asks for again is closed too, not just when the next call comes along.
        _sweep = clock.CreateTimer(_ => EvictStale(), null, s_keep, s_keep);
    }

    public T Run<T>(ProcessIdentity identity, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        switch (identity)
        {
            case ProcessIdentity.CurrentUserIdentity:
                return action();
            case ProcessIdentity.AccountIdentity account:
                var token = Acquire(account.Name);
                try
                {
                    return WindowsIdentity.RunImpersonated(token.Handle, action);
                }
                finally
                {
                    Release(token);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(identity), identity, "Unknown identity.");
        }
    }

    /// <summary>
    /// Drops the account's cached token (it was deleted: a token of a deleted account is worth nothing, and a new account
    /// with the same name must log on afresh). One in use is closed when its operation ends.
    /// </summary>
    public void Forget(string accountName)
    {
        lock (_lock)
        {
            if (_tokens.Remove(accountName, out var token))
            {
                Retire(token);
            }
        }
    }

    public void Dispose()
    {
        if (_accounts is not null)
        {
            _accounts.Deleted -= Forget;
        }

        _sweep.Dispose();
        lock (_lock)
        {
            foreach (var token in _tokens.Values)
            {
                token.Retired = true;
                if (token.Users == 0)
                {
                    token.Handle.Dispose();
                }
            }

            _tokens.Clear();
        }
    }

    private CachedToken Acquire(string name)
    {
        LogonGate gate;
        lock (_lock)
        {
            if (TakeCached(name) is { } cached)
            {
                return cached;
            }

            gate = _gates.TryGetValue(name, out var existing) ? existing : _gates[name] = new LogonGate();
            gate.Waiting++;
        }

        try
        {
            // One logon per account at a time, outside the node-wide lock: it may wait up to 30 seconds for the account's
            // logon lock, and other servers' file operations go on meanwhile.
            lock (gate.Lock)
            {
                lock (_lock)
                {
                    if (TakeCached(name) is { } cached)
                    {
                        return cached;
                    }
                }

                var handle = _logOn(name);
                lock (_lock)
                {
                    var token = new CachedToken(handle) { Users = 1, LastUsed = _clock.GetUtcNow() };
                    _tokens[name] = token;
                    return token;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                if (--gate.Waiting == 0)
                {
                    _gates.Remove(name);
                }
            }
        }
    }

    /// <summary>The account's cached token, taken for one use; stale tokens are retired on the way. Under the lock.</summary>
    private CachedToken? TakeCached(string name)
    {
        var now = _clock.GetUtcNow();
        EvictStaleLocked(now);

        if (!_tokens.TryGetValue(name, out var token))
        {
            return null;
        }

        token.Users++;
        token.LastUsed = now;
        return token;
    }

    private void Release(CachedToken token)
    {
        lock (_lock)
        {
            token.Users--;
            if (token.Retired && token.Users == 0)
            {
                token.Handle.Dispose();
            }

            // Kept for a minute after its last use, which is now; and what went idle meanwhile (other accounts', during a
            // long operation) is closed here, not only when somebody acquires a token next.
            var now = _clock.GetUtcNow();
            token.LastUsed = now;
            EvictStaleLocked(now);
        }
    }

    private void EvictStale()
    {
        lock (_lock)
        {
            EvictStaleLocked(_clock.GetUtcNow());
        }
    }

    /// <summary>Retires the tokens unused for longer than they are kept. Under the lock.</summary>
    private void EvictStaleLocked(DateTimeOffset now)
    {
        foreach (var (key, stale) in _tokens.Where(t => t.Value.Users == 0 && now - t.Value.LastUsed > s_keep).ToList())
        {
            _tokens.Remove(key);
            Retire(stale);
        }
    }

    private static void Retire(CachedToken token)
    {
        token.Retired = true;
        if (token.Users == 0)
        {
            token.Handle.Dispose();
        }
    }

    /// <summary>Makes callers for one account wait for the logon in progress rather than log it on again.</summary>
    private sealed class LogonGate
    {
        public Lock Lock { get; } = new();

        public int Waiting { get; set; }
    }

    private sealed class CachedToken(SafeAccessTokenHandle handle)
    {
        public SafeAccessTokenHandle Handle { get; } = handle;

        public DateTimeOffset LastUsed { get; set; }

        public int Users { get; set; }

        public bool Retired { get; set; }
    }
}
