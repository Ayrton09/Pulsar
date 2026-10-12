using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// Challenge/response authentication (docs/architecture/03): the node signs the panel's nonce together with its ids and
/// the panel origin, and gets a short-lived session for the hub.
/// </summary>
public sealed class PanelAuthenticator(PanelTransport transport)
{
    /// <summary>Signs in at the configured panel address, the key presented as client certificate (<c>status --check</c>).</summary>
    public async Task<TokenResponse> AuthenticateAsync(AgentConfig config, INodeKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(key);
        using var certificate = NodeCertificate.TryCreate(key, out _);
        return await AuthenticateAsync(config.Panel, config, key, certificate, cancellationToken);
    }

    /// <summary>
    /// Signs in at <paramref name="panel"/> (the configured address, its fallback, or an agents' address being tried),
    /// presenting <paramref name="certificate"/> where the panel asks for a client certificate.
    /// </summary>
    public async Task<TokenResponse> AuthenticateAsync(Uri panel, AgentConfig config, INodeKey key, X509Certificate2? certificate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(key);
        var origin = PanelAddress.Origin(panel);
        using var http = transport.CreateClient(panel, certificate);

        // A refusal that sends the agent to the agents' address signs this back: an old refusal played back is not followed.
        var nonce = RandomNumberGenerator.GetBytes(32);
        ChallengeResponse challenge;
        try
        {
            challenge = await PanelTransport.PostAsync<ChallengeRequest, ChallengeResponse>(http, AgentProtocol.ChallengePath,
                new ChallengeRequest(config.NodeId, config.CredentialId, nonce), cancellationToken);
        }
        catch (CertificateRequiredException ex)
        {
            throw new CertificateRequiredException(ex.Message, ex.AgentsAddress, panel, nonce);
        }

        // Signing a nonce bound to another origin would hand a relaying panel a valid login here.
        if (!string.Equals(challenge.PanelOrigin, origin, StringComparison.Ordinal))
        {
            throw new AgentException($"The panel at {origin} issued a challenge for {challenge.PanelOrigin}; refusing to sign it. " +
                "Check the reverse proxy forwards the original host and scheme.");
        }

        var signature = key.Sign(ProtocolCrypto.SignedData(AgentProtocol.Purposes.Authenticate,
            ProtocolCrypto.GuidBytes(config.NodeId), ProtocolCrypto.GuidBytes(config.CredentialId), challenge.Nonce, ProtocolCrypto.Utf8(origin)));
        return await PanelTransport.PostAsync<TokenRequest, TokenResponse>(http, AgentProtocol.TokenPath,
            new TokenRequest(challenge.ChallengeId, signature), cancellationToken);
    }
}
