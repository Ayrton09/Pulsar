using System.Net;
using Microsoft.DevTunnels.Ssh;
using Pulsar.Api.Http;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Sftp;

/// <summary>One TCP connection to the SFTP server, from its arrival to its end.</summary>
internal sealed class SftpConnection : IDisposable
{
    private readonly CancellationTokenSource _ended = new();
    private readonly Lock _contextLock = new();
    private long _lastActivity;
    private int _closing;
    private int _channels;
    private string? _clientVersion;

    public SftpConnection(IPAddress? address, ClientAddress? client, long now)
    {
        Address = address;
        Client = client;
        ConnectedAt = now;
        _lastActivity = now;
        CorrelationId = "sftp-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        Context = SftpRequestContext.Anonymous(address, null, CorrelationId);
    }

    public IPAddress? Address { get; }

    /// <summary>Where the connection counts for the per-address limits (null: an address that cannot be read).</summary>
    public ClientAddress? Client { get; }

    public long ConnectedAt { get; }

    public string CorrelationId { get; }

    /// <summary>Who the connection acts as; anonymous until the sign-in.</summary>
    public SftpRequestContext Context { get; private set; }

    public SshServerSession? Session { get; set; }

    /// <summary>Set once the sign-in succeeded, and never changed afterwards.</summary>
    public SftpIdentity? Identity { get; private set; }

    /// <summary>The keyboard-interactive sign-in in progress (between the password and the code).</summary>
    public SftpInteractiveState? Interactive { get; set; }

    public int PasswordChecks { get; set; }

    /// <summary>
    /// Questions whether a key would do (<c>publickey</c> without a signature), and <c>publickey</c> requests for an
    /// algorithm the server does not take.
    /// </summary>
    public int KeyQuestions { get; set; }

    /// <summary>Signed public key attempts checked.</summary>
    public int KeyChecks { get; set; }

    /// <summary>Sftp subsystems started on the connection's channels.</summary>
    public int Subsystems { get; set; }

    /// <summary>Cancelled when the connection ends: what it was doing stops.</summary>
    public CancellationToken Ended => _ended.Token;

    public long LastActivity => Interlocked.Read(ref _lastActivity);

    public bool IsClosing => Volatile.Read(ref _closing) != 0;

    public void Touch(long now) => Interlocked.Exchange(ref _lastActivity, now);

    /// <summary>The client's SSH version, for the audit log; whoever signed in stays signed in.</summary>
    public void SetClientVersion(string? version)
    {
        lock (_contextLock)
        {
            _clientVersion = version;
            Context = ContextFor(Identity);
        }
    }

    public void SignIn(SftpIdentity identity)
    {
        lock (_contextLock)
        {
            if (Identity is not null)
            {
                throw new InvalidOperationException("The connection is already signed in.");
            }

            Identity = identity;
            Context = ContextFor(identity);
            Interactive = null;
        }
    }

    /// <summary>A channel opens: false when the connection holds <see cref="SftpHost.MaxChannelsPerConnection"/> already.</summary>
    public bool TryOpenChannel()
    {
        if (Interlocked.Increment(ref _channels) <= SftpHost.MaxChannelsPerConnection)
        {
            return true;
        }

        Interlocked.Decrement(ref _channels);
        return false;
    }

    public void ChannelClosed() => Interlocked.Decrement(ref _channels);

    /// <summary>Ends the connection (once): the client is told why, and what it was doing stops.</summary>
    public void Close(SshDisconnectReason reason, string message)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        try
        {
            _ended.Cancel();
        }
        catch (AggregateException)
        {
            // A callback failed; the connection still ends.
        }

        if (Session is { } session)
        {
            _ = CloseSessionAsync(session, reason, message);
        }
    }

    public void Dispose() => _ended.Dispose();

    private SftpRequestContext ContextFor(SftpIdentity? identity)
    {
        var anonymous = SftpRequestContext.Anonymous(Address, _clientVersion, CorrelationId);
        return identity is null ? anonymous : anonymous.SignedIn(identity.UserId, identity.UserName);
    }

    private static async Task CloseSessionAsync(SshSession session, SshDisconnectReason reason, string message)
    {
        try
        {
            await session.CloseAsync(reason, message).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Closed already, or the client is gone: the connection's end disposes of it either way.
        }
    }
}

/// <summary>
/// Who signed in on a connection, and as which root (<see cref="ServerShortId"/>: one server's folder); <see cref="Key"/>:
/// the SSH key it signed in with (null: the password).
/// </summary>
internal sealed record SftpIdentity(UserId UserId, string UserName, string? SecurityStamp, string? ServerShortId, SshKeyId? Key = null);

/// <summary>Keyboard-interactive sign-in: the password was right, the code is asked next.</summary>
internal sealed record SftpInteractiveState(string Login, UserId? AwaitingCodeFor, string? ServerShortId);

/// <summary>
/// The SFTP server's connections and their limits (docs/architecture/08, T35): connections at once, those not signed in,
/// per client address, and signed-in sessions per user. What ends a user's browser sessions ends their SFTP sessions
/// too (<see cref="Realtime.BrowserConnections"/>).
/// </summary>
public sealed class SftpConnections(Microsoft.Extensions.Options.IOptions<SftpOptions> options, TimeProvider clock)
{
    private readonly SftpOptions _options = options.Value;
    private readonly Lock _lock = new();
    private readonly HashSet<SftpConnection> _all = [];
    private readonly Dictionary<ClientAddress, int> _perAddress = [];
    private readonly Dictionary<UserId, int> _perUser = [];
    private int _unauthenticated;

    /// <summary>Signed-in sessions now (tests, diagnostics).</summary>
    public int SignedIn
    {
        get
        {
            lock (_lock)
            {
                return _all.Count - _unauthenticated;
            }
        }
    }

    /// <summary>The user's sessions end (signed out, disabled, deleted, a new password or two-factor setup, other roles).</summary>
    public void EndUser(UserId user) => End(c => c.Identity?.UserId == user, "Your panel session ended. Sign in again.");

    /// <summary>A role changed: any user's permissions may have.</summary>
    public void EndAll() => End(c => c.Identity is not null, "Panel roles changed. Sign in again.");

    /// <summary>The key was deleted: the sessions it signed in end (the user's others stay).</summary>
    public void EndKey(UserId user, SshKeyId key) =>
        End(c => c.Identity is { } identity && identity.UserId == user && identity.Key == key, "The SSH key you signed in with was deleted.");

    /// <summary>All of the user's keys were deleted: every session one of them signed in ends.</summary>
    public void EndKeys(UserId user) =>
        End(c => c.Identity is { Key: not null } identity && identity.UserId == user, "The SSH key you signed in with was deleted.");

    /// <summary>The server stops or moves: every connection ends.</summary>
    internal void CloseEverything(string message) => End(_ => true, message, SshDisconnectReason.ServiceNotAvailable);

    /// <summary>A new connection, or null when a limit is reached (the caller closes the socket).</summary>
    internal SftpConnection? TryAdmit(IPAddress? address)
    {
        var client = ClientAddress.Of(address);
        lock (_lock)
        {
            if (_all.Count >= _options.MaxConnections || _unauthenticated >= _options.MaxUnauthenticatedConnections
                || (client is { } c && _perAddress.GetValueOrDefault(c) >= _options.MaxConnectionsPerAddress))
            {
                return null;
            }

            var connection = new SftpConnection(address, client, clock.GetTimestamp());
            _all.Add(connection);
            _unauthenticated++;
            if (client is { } counted)
            {
                _perAddress[counted] = _perAddress.GetValueOrDefault(counted) + 1;
            }

            return connection;
        }
    }

    /// <summary>The sign-in succeeded: counts the session for its user, or false when the user has as many as allowed.</summary>
    internal bool TrySignIn(SftpConnection connection, SftpIdentity identity)
    {
        lock (_lock)
        {
            if (!_all.Contains(connection) || connection.IsClosing || connection.Identity is not null
                || _perUser.GetValueOrDefault(identity.UserId) >= _options.MaxSessionsPerUser)
            {
                return false;
            }

            connection.SignIn(identity);
            _unauthenticated--;
            _perUser[identity.UserId] = _perUser.GetValueOrDefault(identity.UserId) + 1;
            return true;
        }
    }

    internal void Remove(SftpConnection connection)
    {
        lock (_lock)
        {
            if (!_all.Remove(connection))
            {
                return;
            }

            if (connection.Identity is { } identity)
            {
                Decrement(_perUser, identity.UserId);
            }
            else
            {
                _unauthenticated--;
            }

            if (connection.Client is { } client)
            {
                Decrement(_perAddress, client);
            }
        }
    }

    /// <summary>Closes connections past the sign-in deadline or idle too long.</summary>
    internal void Sweep()
    {
        var now = clock.GetTimestamp();
        List<(SftpConnection Connection, string Message)> expired = [];
        lock (_lock)
        {
            foreach (var connection in _all)
            {
                if (connection.Identity is null && clock.GetElapsedTime(connection.ConnectedAt, now) > _options.AuthenticationTimeout)
                {
                    expired.Add((connection, "Sign-in timed out."));
                }
                else if (connection.Identity is not null && clock.GetElapsedTime(connection.LastActivity, now) > _options.IdleTimeout)
                {
                    expired.Add((connection, "Idle for too long."));
                }
            }
        }

        foreach (var (connection, message) in expired)
        {
            connection.Close(SshDisconnectReason.ByApplication, message);
        }
    }

    private void End(Func<SftpConnection, bool> which, string message, SshDisconnectReason reason = SshDisconnectReason.ByApplication)
    {
        List<SftpConnection> ending;
        lock (_lock)
        {
            ending = [.. _all.Where(which)];
        }

        foreach (var connection in ending)
        {
            connection.Close(reason, message);
        }
    }

    private static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key)
        where TKey : notnull
    {
        if (counts.TryGetValue(key, out var count) && count > 1)
        {
            counts[key] = count - 1;
        }
        else
        {
            counts.Remove(key);
        }
    }
}
