using System.Text.Json;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// The agents' address in a refusal of a panel that requires client certificates (Phase 16, docs/architecture/03 "Client
/// certificates"). The agent follows it only when a panel key it pins signed it for this node and for the nonce of the
/// challenge request it answers, and it puts the port on the host it reached the panel by unless the address names the
/// operator's own.
/// </summary>
public static class AgentsAddressHint
{
    /// <summary>Where to sign in instead of <paramref name="refusedAt"/>, or null when the refusal says nowhere it can trust.</summary>
    /// <param name="refusal">The signed agents' address that came with the refusal.</param>
    /// <param name="nonce">What the agent sent with the challenge request that was refused.</param>
    public static Uri? Verify(SignedMessage? refusal, AgentConfig config, Uri refusedAt, byte[]? nonce)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(refusedAt);
        if (refusal is null || nonce is null || config.PanelKeys.FirstOrDefault(k => k.KeyId == refusal.KeyId) is not { } signer
            || !ProtocolCrypto.VerifyMessage(refusal, AgentProtocol.Purposes.AgentsAddress, signer.PublicKey))
        {
            return null;
        }

        AgentsAddress address;
        try
        {
            address = ProtocolJson.Deserialize<AgentsAddress>(refusal.Payload);
        }
        catch (JsonException)
        {
            return null;
        }

        // Another node's refusal, or one recorded earlier and played back: neither answers this request.
        if (address.NodeId != config.NodeId || address.Nonce is null || !address.Nonce.AsSpan().SequenceEqual(nonce))
        {
            return null;
        }

        var target = PanelAddress.AgentsAddress(refusedAt, address.Port, address.Url);
        return target == refusedAt ? null : target;
    }
}
