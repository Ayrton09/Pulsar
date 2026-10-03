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
/// kept for a minute after its last use so browsing a folder does not log the account on at every click. A development
/// agent's servers run as the agent's own user: their actions run as they are.
/// </summary>
public sealed class ServerImpersonation(TimeProvider clock) : IServerImpersonation, IDisposable
{
    private static readonly TimeSpan s_keep = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, CachedToken> _tokens = new(StringComparer.OrdinalIgnoreCase);

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

    public void Dispose()
    {
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
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            foreach (var (key, stale) in _tokens.Where(t => now - t.Value.LastUsed > s_keep).ToList())
            {
                _tokens.Remove(key);
                stale.Retired = true;
                if (stale.Users == 0)
                {
                    stale.Handle.Dispose();
                }
            }

            if (!_tokens.TryGetValue(name, out var token))
            {
                token = new CachedToken(WindowsServerAccounts.LogOnForImpersonation(name));
                _tokens[name] = token;
            }

            token.Users++;
            token.LastUsed = now;
            return token;
        }
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
        }
    }

    private sealed class CachedToken(SafeAccessTokenHandle handle)
    {
        public SafeAccessTokenHandle Handle { get; } = handle;

        public DateTimeOffset LastUsed { get; set; }

        public int Users { get; set; }

        public bool Retired { get; set; }
    }
}
