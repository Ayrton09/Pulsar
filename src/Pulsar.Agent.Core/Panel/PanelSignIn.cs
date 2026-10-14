using System.Text.Json;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// A session the panel granted (docs/architecture/03, "Authentication on every connection"): its answer
/// (<see cref="Token"/>), the <see cref="Grant"/> that verified, and the panel's clock offset measured from that grant.
/// </summary>
/// <param name="PanelClockOffset">
/// Panel time minus local time: the grant's <see cref="SessionGrant.IssuedAt"/> against this machine's clock when the
/// answer arrived. Off by at most the time the token request took, since the grant answers this sign-in's fresh nonce.
/// </param>
public sealed record PanelSignIn(TokenResponse Token, SessionGrant Grant, TimeSpan PanelClockOffset)
{
    /// <summary>
    /// The grant of <paramref name="token"/>, or null when there is none the agent can trust: it must be signed by a panel
    /// key the agent pins, for this node, the credential that signed in, this session and <paramref name="nonce"/> (what
    /// the agent sent with this sign-in's challenge request), and carry the panel's time. Whatever sits between the agent
    /// and the panel (a TLS-terminating proxy) can answer a sign-in without the panel ever seeing it, and can play back an
    /// earlier answer, but cannot make either of them pass this.
    /// </summary>
    public static SessionGrant? VerifyGrant(TokenResponse token, AgentConfig config, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(nonce);
        if (token.Grant is not { } signed || config.PanelKeys.FirstOrDefault(k => k.KeyId == signed.KeyId) is not { } signer
            || !ProtocolCrypto.VerifyMessage(signed, AgentProtocol.Purposes.SessionGrant, signer.PublicKey))
        {
            return null;
        }

        SessionGrant grant;
        try
        {
            grant = ProtocolJson.Deserialize<SessionGrant>(signed.Payload);
        }
        catch (JsonException)
        {
            return null;
        }

        return grant.NodeId == config.NodeId && grant.CredentialId == config.CredentialId && grant.SessionId == token.SessionId
            && grant.Nonce is not null && grant.Nonce.AsSpan().SequenceEqual(nonce) && grant.IssuedAt is not null
            ? grant
            : null;
    }
}
