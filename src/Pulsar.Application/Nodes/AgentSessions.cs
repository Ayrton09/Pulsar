using System.Collections.Concurrent;
using System.Security.Cryptography;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Nodes;

/// <summary>
/// An authenticated agent session. Commands sent in it carry increasing sequence numbers, which the agent's
/// anti-replay window checks; a new session (new authentication) starts a new sequence.
/// </summary>
/// <param name="certificateKey">
/// The fingerprint of the node key the agent presented as its TLS client certificate when it signed in (Phase 16):
/// every request of the session must present it again, so the token alone is worth nothing. Null: a session signed
/// in without one (the main endpoint), which the token alone carries.
/// </param>
public sealed class AgentSession(Guid sessionId, NodeId nodeId, Guid credentialId, DateTimeOffset expiresAt, string? certificateKey = null)
{
    private long _sequence;

    public Guid SessionId { get; } = sessionId;

    public NodeId NodeId { get; } = nodeId;

    public Guid CredentialId { get; } = credentialId;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public string? CertificateKey { get; } = certificateKey;

    public long NextSequence() => Interlocked.Increment(ref _sequence);
}

/// <summary>
/// Session tokens for agents: opaque random values, stored only as SHA-256, in memory. The panel is a single instance
/// in v1 and needs per-session state anyway (sequence, immediate revocation), so a stateless JWT would add key
/// management without removing the state. A panel restart ends every session; agents re-authenticate on reconnect.
/// </summary>
public sealed class AgentSessionStore(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, AgentSession> _byTokenHash = new(StringComparer.Ordinal);

    public (string Token, AgentSession Session) Create(NodeId node, Guid credentialId, string? certificateKey = null)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var session = new AgentSession(Guid.CreateVersion7(), node, credentialId, clock.GetUtcNow() + Lifetime, certificateKey);
        _byTokenHash[Hash(token)] = session;
        return (token, session);
    }

    /// <summary>The live session for a token, or null when unknown or expired.</summary>
    public AgentSession? Find(string token)
    {
        var hash = Hash(token);
        if (!_byTokenHash.TryGetValue(hash, out var session))
        {
            return null;
        }

        if (session.ExpiresAt <= clock.GetUtcNow())
        {
            _byTokenHash.TryRemove(hash, out _);
            return null;
        }

        return session;
    }

    /// <summary>The live session for an id, or null when unknown, expired or ended.</summary>
    public AgentSession? Find(Guid sessionId)
    {
        var now = clock.GetUtcNow();
        return _byTokenHash.Values.FirstOrDefault(s => s.SessionId == sessionId && s.ExpiresAt > now);
    }

    /// <summary>True while the session exists and has not expired or been ended.</summary>
    public bool IsActive(Guid sessionId) => Find(sessionId) is not null;

    /// <summary>Ends one session.</summary>
    public void End(Guid sessionId)
    {
        foreach (var (hash, session) in _byTokenHash)
        {
            if (session.SessionId == sessionId)
            {
                _byTokenHash.TryRemove(hash, out _);
            }
        }
    }

    /// <summary>Ends the sessions opened with one credential (it was superseded by a rotation).</summary>
    public void EndCredential(Guid credentialId)
    {
        foreach (var (hash, session) in _byTokenHash)
        {
            if (session.CredentialId == credentialId)
            {
                _byTokenHash.TryRemove(hash, out _);
            }
        }
    }

    /// <summary>Ends every session of a node (revocation, disabling, re-enrollment, deletion).</summary>
    public void EndAll(NodeId node)
    {
        foreach (var (hash, session) in _byTokenHash)
        {
            if (session.NodeId == node)
            {
                _byTokenHash.TryRemove(hash, out _);
            }
        }
    }

    /// <summary>
    /// Ends every session signed in without a client certificate (certificates became required) and returns their
    /// nodes, whose connections the caller closes.
    /// </summary>
    public IReadOnlySet<NodeId> EndWithoutCertificate()
    {
        var nodes = new HashSet<NodeId>();
        foreach (var (hash, session) in _byTokenHash)
        {
            if (session.CertificateKey is null && _byTokenHash.TryRemove(hash, out _))
            {
                nodes.Add(session.NodeId);
            }
        }

        return nodes;
    }

    /// <summary>Drops expired sessions; called opportunistically on authentication.</summary>
    public void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (hash, session) in _byTokenHash)
        {
            if (session.ExpiresAt <= now)
            {
                _byTokenHash.TryRemove(hash, out _);
            }
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Single-use authentication challenges (32-byte nonce, 60 s), in memory.</summary>
public sealed class AgentChallengeStore(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<Guid, PendingChallenge> _pending = new();

    /// <summary>The last <see cref="PendingChallenge.IssuedAt"/>, in microseconds (what the database keeps of a time).</summary>
    private long _lastIssuedMicroseconds;

    /// <param name="IssuedAt">
    /// When it was issued, and always after every challenge issued before it, by a microsecond at least, even when the
    /// clock steps back: a sign-in records no earlier time for its key (<see cref="AgentAccess"/>), so it counts as after
    /// a pending key's challenge issued before its own, whatever the clock did.
    /// </param>
    /// <param name="AgentNonce">What the agent sent with the challenge request, signed back in the session's grant.</param>
    public sealed record PendingChallenge(Guid ChallengeId, NodeId NodeId, Guid CredentialId, byte[] Nonce, string PanelOrigin, DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt, byte[]? AgentNonce = null);

    public PendingChallenge Issue(NodeId node, Guid credentialId, string panelOrigin, byte[]? agentNonce = null)
    {
        Prune();
        var now = clock.GetUtcNow();
        var challenge = new PendingChallenge(Guid.CreateVersion7(), node, credentialId, RandomNumberGenerator.GetBytes(32), panelOrigin, NextIssuedAt(now),
            now + Lifetime, agentNonce);
        _pending[challenge.ChallengeId] = challenge;
        return challenge;
    }

    private DateTimeOffset NextIssuedAt(DateTimeOffset now)
    {
        long last, next;
        do
        {
            last = Interlocked.Read(ref _lastIssuedMicroseconds);
            next = Math.Max(now.UtcTicks / 10, last + 1);
        }
        while (Interlocked.CompareExchange(ref _lastIssuedMicroseconds, next, last) != last);

        return new DateTimeOffset(next * 10, TimeSpan.Zero);
    }

    /// <summary>Removes and returns the challenge: it can be answered once, valid or not.</summary>
    public PendingChallenge? Consume(Guid challengeId) =>
        _pending.TryRemove(challengeId, out var challenge) && challenge.ExpiresAt > clock.GetUtcNow() ? challenge : null;

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (id, challenge) in _pending)
        {
            if (challenge.ExpiresAt <= now)
            {
                _pending.TryRemove(id, out _);
            }
        }
    }
}
