using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Contracts;

namespace Pulsar.Application.Nodes;

/// <summary>
/// Where agents find the agents' endpoint, from the panel's configuration only: its port, on the host each agent already
/// reaches the panel by, or <paramref name="Url"/> when the operator set one (<c>Agents:PublicUrl</c>). Never the host of a
/// request, which a proxy in front of the panel could choose and the panel would then sign.
/// </summary>
public sealed record AgentsEndpointAddress(int Port, string? Url);

/// <summary>
/// How an agent request reached the panel (Phase 16, docs/architecture/03 "Client certificates").
/// </summary>
/// <param name="ThroughAgentsEndpoint">It came through the agents' endpoint, which asks every client for a certificate.</param>
/// <param name="ClientKey">The SubjectPublicKeyInfo of the TLS client certificate, when one was presented.</param>
/// <param name="Agents">The panel's agents' endpoint, or null when it has none.</param>
public sealed record AgentTransport(bool ThroughAgentsEndpoint, byte[]? ClientKey, AgentsEndpointAddress? Agents)
{
    /// <summary>The main endpoint of a panel without an agents' endpoint (the v1 protocol).</summary>
    public static AgentTransport Main { get; } = new(false, null, null);

    /// <summary>The fingerprint of <see cref="ClientKey"/> (<see cref="ProtocolCrypto.Fingerprint"/>), or null.</summary>
    public string? ClientKeyFingerprint => ClientKey is null ? null : ProtocolCrypto.Fingerprint(ClientKey);

    /// <summary>
    /// Whether the client certificate is this node key. Both sides are re-encoded from the key itself, so two encodings
    /// of one key match and a certificate on another algorithm never does.
    /// </summary>
    public bool PresentsKey(byte[] subjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfo);
        if (ClientKey is null)
        {
            return false;
        }

        try
        {
            using var presented = ProtocolCrypto.ImportPublicKey(ClientKey);
            using var registered = ProtocolCrypto.ImportPublicKey(subjectPublicKeyInfo);
            return presented.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(registered.ExportSubjectPublicKeyInfo());
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// Whether agent sessions need a client certificate (the switch on the Nodes page), kept in memory for the request
/// paths and changed only by <see cref="AgentTransportManagement"/>; the panel is a single instance. It is read from
/// the database before the first sign-in needs it: a session without a certificate only comes from a sign-in, so the
/// checks of later requests always see it loaded.
/// </summary>
public sealed class AgentTransportPolicy
{
    private const int Unknown = 0;
    private const int Off = 1;
    private const int On = 2;

    private int _state;

    public bool RequireClientCertificates => Volatile.Read(ref _state) == On;

    /// <summary>One change at a time, so the switch in memory is the one saved last.</summary>
    internal SemaphoreSlim Changing { get; } = new(1, 1);

    /// <summary>
    /// The switch only holds while the panel has an agents' endpoint: without one, agents would have nowhere to go.
    /// </summary>
    public bool RequiresCertificateFor(AgentTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return RequireClientCertificates && transport.Agents is not null;
    }

    public async ValueTask EnsureLoadedAsync(IPulsarDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (Volatile.Read(ref _state) != Unknown)
        {
            return;
        }

        var settings = await db.AgentTransportSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        // A change saved meanwhile wins over what was read before it.
        Interlocked.CompareExchange(ref _state, settings?.RequireClientCertificates == true ? On : Off, Unknown);
    }

    // A full fence between changing the switch and ending the sessions without a certificate; a sign-in has the other one
    // between creating its session and reading the switch (AgentAccess.AuthenticateAsync). So either the sign-in sees
    // the switch on, or the switch's sweep sees its session.
    internal void Set(bool requireClientCertificates) => Interlocked.Exchange(ref _state, requireClientCertificates ? On : Off);

    /// <summary>A change whose outcome is unknown: the switch is read from the database again before the next sign-in.</summary>
    internal void Forget() => Interlocked.Exchange(ref _state, Unknown);
}
