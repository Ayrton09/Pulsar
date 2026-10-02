using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// Challenge/response authentication (docs/architecture/03): the node signs the panel's nonce together with its ids and
/// the panel origin, and gets a short-lived session for the hub.
/// </summary>
public sealed class PanelAuthenticator(PanelTransport transport)
{
    public async Task<TokenResponse> AuthenticateAsync(AgentConfig config, INodeKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(key);
        var origin = PanelAddress.Origin(config.Panel);
        using var http = transport.CreateClient(config.Panel);

        var challenge = await PanelTransport.PostAsync<ChallengeRequest, ChallengeResponse>(http, AgentProtocol.ChallengePath,
            new ChallengeRequest(config.NodeId, config.CredentialId), cancellationToken);

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
