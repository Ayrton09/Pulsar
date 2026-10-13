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
    /// <summary>The longest challenge nonce the agent signs (the panel's are 32 bytes).</summary>
    public const int MaxNonceLength = 64;

    /// <summary>The longest session token the agent keeps (the panel's are 43 characters).</summary>
    public const int MaxAccessTokenLength = 1024;

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

        // Never the panel's: it is not signed, whatever it is.
        if (challenge.Nonce is not { Length: > 0 and <= MaxNonceLength })
        {
            throw new PanelNotAnsweringException($"The address answered {AgentProtocol.ChallengePath} with a nonce of {challenge.Nonce?.Length ?? 0} bytes, " +
                "which is not the agent protocol: no panel answers it there now.");
        }

        // Signing a nonce bound to another origin would hand a relaying panel a valid login here.
        if (!string.Equals(challenge.PanelOrigin, origin, StringComparison.Ordinal))
        {
            throw new AgentException($"The panel at {origin} issued a challenge for {challenge.PanelOrigin}; refusing to sign it. " +
                "Check the reverse proxy forwards the original host and scheme.");
        }

        var signature = key.Sign(ProtocolCrypto.SignedData(AgentProtocol.Purposes.Authenticate,
            ProtocolCrypto.GuidBytes(config.NodeId), ProtocolCrypto.GuidBytes(config.CredentialId), challenge.Nonce, ProtocolCrypto.Utf8(origin)));
        var token = await PanelTransport.PostAsync<TokenRequest, TokenResponse>(http, AgentProtocol.TokenPath,
            new TokenRequest(challenge.ChallengeId, signature), cancellationToken);
        return token.AccessToken is { Length: > 0 and <= MaxAccessTokenLength }
            ? token
            : throw new PanelNotAnsweringException($"The address answered {AgentProtocol.TokenPath} with a session token of {token.AccessToken?.Length ?? 0} " +
                "characters, which is not the agent protocol: no panel answers it there now.");
    }
}
