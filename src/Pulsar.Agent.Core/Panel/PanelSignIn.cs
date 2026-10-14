using System.Text.Json;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// A sign-in's answer (<see cref="Token"/>) with the <see cref="Nonce"/> the agent sent with the challenge request it began
/// with, which the panel signs back in <see cref="TokenResponse.Grant"/> (docs/architecture/03, "Authentication").
/// </summary>
public sealed record PanelSignIn(TokenResponse Token, byte[] Nonce)
{
    /// <summary>
    /// Whether the panel itself opened this session: its grant is signed by a panel key the agent pins, for this node,
    /// <paramref name="credentialId"/>, this session and this sign-in's nonce. Whatever sits between the agent and the panel
    /// (a TLS-terminating proxy) can answer a sign-in without the panel ever seeing it, and can play back an earlier answer,
    /// but cannot make either of them pass this.
    /// </summary>
    public bool IsGranted(AgentConfig config, Guid credentialId)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (Token.Grant is not { } signed || config.PanelKeys.FirstOrDefault(k => k.KeyId == signed.KeyId) is not { } signer
            || !ProtocolCrypto.VerifyMessage(signed, AgentProtocol.Purposes.SessionGrant, signer.PublicKey))
        {
            return false;
        }

        SessionGrant grant;
        try
        {
            grant = ProtocolJson.Deserialize<SessionGrant>(signed.Payload);
        }
        catch (JsonException)
        {
            return false;
        }

        return grant.NodeId == config.NodeId && grant.CredentialId == credentialId && grant.SessionId == Token.SessionId
            && grant.Nonce is not null && grant.Nonce.AsSpan().SequenceEqual(Nonce);
    }
}
