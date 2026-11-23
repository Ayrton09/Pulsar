using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Runs the agent's file access to a server's folder as the server's user (docs/architecture/08, "File manager", layer 4),
/// as Windows' impersonation does: on the calling thread, its file system user and group become the server's
/// (<c>setfsuid</c>, <c>setfsgid</c>, and no supplementary groups), and the kernel then checks every file it opens or
/// changes against that user's rights, root's ways around them gone from the thread meanwhile (a file system user other
/// than root drops <c>CAP_DAC_OVERRIDE</c>, <c>CAP_DAC_READ_SEARCH</c>, <c>CAP_FOWNER</c> and the like). A link the
/// server put there leads only where its user may go, and what the agent creates is the server's. The thread is root's
/// again before the action's result is returned, whatever it did. A development agent's servers run as its own user: their
/// actions run as they are.
/// </summary>
/// <remarks>
/// Only the calling thread changes (the C library's <c>setgroups</c> would change every thread: the system call is made
/// directly), so <paramref name="action"/> must be short and synchronous: no await, nothing that hops threads.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class ServerFileIdentity(LinuxServerAccounts accounts, bool development, TimeProvider clock)
{
    private const long SetGroupsSyscall = 116; // x86_64
    private static readonly TimeSpan s_cacheFor = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (LinuxAccount Account, DateTimeOffset Until)> _accounts = new(StringComparer.Ordinal);

    public T Run<T>(ProcessIdentity identity, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(action);
        if (identity is ProcessIdentity.CurrentUserIdentity)
        {
            return development && Libc.geteuid() != 0
                ? action()
                : throw new FileManagerException(FileErrors.AccessDenied,
                    "A server without its own account would be reached as the agent's user (root for the service). Only an agent enrolled with --dev may do that.");
        }

        var account = Account(((ProcessIdentity.AccountIdentity)identity).Name);
        if (Libc.geteuid() != 0)
        {
            throw new FileManagerException(FileErrors.AccessDenied, "Only root can act as a server's user.");
        }

        Become(account.Uid, account.Gid);
        try
        {
            return action();
        }
        finally
        {
            Restore();
        }
    }

    public void Run(ProcessIdentity identity, Action action) => Run(identity, () =>
    {
        action();
        return 0;
    });

    /// <summary>
    /// Runs <paramref name="action"/> as the owner of <paramref name="serverFolder"/> (its user and group): a server's
    /// folder is its user's. One of this process's own user is reached as it is; a folder of root's (no server's) or a
    /// link in its place is refused.
    /// </summary>
    public static T RunAsOwnerOf<T>(string serverFolder, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Libc.Lstat(serverFolder) is not { IsDirectory: true } stat)
        {
            throw new DirectoryNotFoundException($"The server's folder {serverFolder} does not exist (or is not a folder).");
        }

        if (stat.Uid == Libc.geteuid())
        {
            return stat.Uid == 0
                ? throw new UnauthorizedAccessException($"{serverFolder} is root's, not a server's: the agent does not act on it.")
                : action();
        }

        if (Libc.geteuid() != 0)
        {
            throw new UnauthorizedAccessException($"Only root can act as the owner of {serverFolder}.");
        }

        Become(stat.Uid, stat.Gid);
        try
        {
            return action();
        }
        finally
        {
            Restore();
        }
    }

    /// <summary>Forgets an account (it was deleted): the next one of that name is looked up again.</summary>
    public void Forget(string name) => _accounts.TryRemove(name, out _);

    private LinuxAccount Account(string name)
    {
        var now = clock.GetUtcNow();
        if (_accounts.TryGetValue(name, out var cached) && cached.Until > now)
        {
            return cached.Account;
        }

        var account = accounts.Lookup(name)
            ?? throw new FileManagerException(FileErrors.AccessDenied, $"The server's account {name} does not exist: install the server again.");
        _accounts[name] = (account, now + s_cacheFor);
        return account;
    }

    private static void Become(uint uid, uint gid)
    {
        if (Syscall(SetGroupsSyscall, 0, IntPtr.Zero) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            Restore();
            throw new FileManagerException(FileErrors.AccessDenied, $"Cannot act as the server's user (setgroups: errno {error}).");
        }

        _ = setfsgid(gid);
        _ = setfsuid(uid);
        // setfsuid and setfsgid say nothing of a failure: asking again (with an id that changes nothing) tells what took.
        if (setfsuid(uint.MaxValue) != uid || setfsgid(uint.MaxValue) != gid)
        {
            Restore();
            throw new FileManagerException(FileErrors.AccessDenied, "Cannot act as the server's user (setfsuid).");
        }
    }

    private static void Restore()
    {
        _ = setfsuid(0);
        _ = setfsgid(0);
        // Root's own supplementary groups: the service's (root's) has none but root's group.
        var groups = new uint[] { 0 };
        unsafe
        {
            fixed (uint* list = groups)
            {
                _ = Syscall(SetGroupsSyscall, 1, (IntPtr)list);
            }
        }

        if (setfsuid(uint.MaxValue) != 0)
        {
            // Never leave a thread of the agent's as someone else: better the agent than a wrong file access.
            Environment.FailFast("A thread of the agent could not become root's again after acting as a server's user.");
        }
    }

    [LibraryImport("libc")]
    private static partial uint setfsuid(uint uid);

    [LibraryImport("libc")]
    private static partial uint setfsgid(uint gid);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long Syscall(long number, long count, IntPtr list);
}
