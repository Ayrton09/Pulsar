using System.Net.Http.Headers;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// Registers a new node key with the panel within the current session (docs/architecture/03, "Rotation"). The panel's
/// answer is accepted only if a pinned panel key signed it for this node.
/// </summary>
public sealed class CredentialRotationClient(PanelTransport transport)
{
    /// <returns>The pending credential id of <paramref name="newKey"/>.</returns>
    public async Task<Guid> RegisterAsync(AgentConfig config, string accessToken, Guid sessionId, INodeKey newKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(newKey);
        var publicKey = newKey.PublicKey;
        var proof = newKey.Sign(ProtocolCrypto.SignedData(AgentProtocol.Purposes.RotateCredential, ProtocolCrypto.GuidBytes(config.NodeId),
            ProtocolCrypto.GuidBytes(config.CredentialId), ProtocolCrypto.GuidBytes(sessionId), publicKey));

        using var http = transport.CreateClient(config.Panel);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var signed = await PanelTransport.PostAsync<RotateCredentialRequest, SignedMessage>(http, AgentProtocol.RotateCredentialPath,
            new RotateCredentialRequest(publicKey, newKey.Reference.Storage, proof), cancellationToken);

        var signer = config.PanelKeys.FirstOrDefault(k => k.KeyId == signed.KeyId);
        if (signer is null || !ProtocolCrypto.VerifyMessage(signed, AgentProtocol.Purposes.RotationResult, signer.PublicKey))
        {
            throw new AgentException("The rotation answer is not signed by a pinned panel key; the new key is discarded.");
        }

        var result = ProtocolJson.Deserialize<CredentialRotation>(signed.Payload);
        return result.NodeId == config.NodeId
            ? result.CredentialId
            : throw new AgentException("The rotation answer is for another node; the new key is discarded.");
    }
}
