using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <param name="Panel">Panel URL as typed by the administrator.</param>
/// <param name="Token">Single-use enrollment token shown by the panel.</param>
/// <param name="PanelKeyFingerprint">Fingerprint of the panel key, shown next to the token: the agent pins it.</param>
/// <param name="BackupsRoot">Where backups go (null: inside the state directory); restricted like the state directory.</param>
public sealed record EnrollmentRequest(
    string Panel,
    string Token,
    string PanelKeyFingerprint,
    string? ServersRoot,
    bool Development,
    string? BackupsRoot = null);

/// <summary>
/// Pairs this machine with a panel (docs/architecture/03): pins the panel key the administrator was shown, creates the
/// node key, proves possession of it with the token, and keeps the result only if the pinned key signed it.
/// </summary>
public sealed class AgentEnrollment(
    AgentConfigStore configs,
    INodeKeyStore keys,
    INodeSystem system,
    IAgentDirectorySecurity directorySecurity,
    PanelTransport transport,
    TimeProvider clock)
{
    /// <summary>The last enrollment created a node that waits for an administrator's approval (a reusable token's).</summary>
    public bool AwaitingApproval { get; private set; }

    public async Task<AgentConfig> EnrollAsync(EnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var panel = PanelAddress.Parse(request.Panel);
        var origin = PanelAddress.Origin(panel);
        using var http = transport.CreateClient(panel);

        var identity = await PanelTransport.GetAsync<PanelIdentityResponse>(http, AgentProtocol.PanelIdentityPath, cancellationToken);
        // The fingerprint is recomputed rather than trusting the KeyId the panel sends next to the key.
        var pinned = identity.Keys.FirstOrDefault(k => k.KeyId == request.PanelKeyFingerprint
            && ProtocolCrypto.Fingerprint(k.PublicKey) == request.PanelKeyFingerprint)
            ?? throw new AgentException(
                $"The panel at {origin} does not hold the key {request.PanelKeyFingerprint}. Check the URL and copy the command from the panel again; " +
                "if they are right, something between this machine and the panel is impersonating it.");

        directorySecurity.SecureState(configs.Paths.Root, configs.Paths.OwnFolders, request.Development);
        if (request.BackupsRoot is { } backups)
        {
            directorySecurity.SecureFolder(backups, request.Development);
        }

        var previous = configs.Load();
        var scope = request.Development ? NodeKeyScope.User : NodeKeyScope.Machine;
        var key = keys.Create($"PulsarAgent-{Guid.NewGuid():N}", scope);
        var saved = false;
        try
        {
            var publicKey = key.PublicKey;
            var proof = key.Sign(ProtocolCrypto.SignedData(AgentProtocol.Purposes.Enroll, ProtocolCrypto.Utf8(request.Token), publicKey));
            var signed = await PanelTransport.PostAsync<EnrollRequest, SignedMessage>(http, AgentProtocol.EnrollPath,
                new EnrollRequest(request.Token, publicKey, key.Reference.Storage, AgentInfo.Version, system.GetInventory(request.ServersRoot), proof),
                cancellationToken);

            if (signed.KeyId != pinned.KeyId || !ProtocolCrypto.VerifyMessage(signed, AgentProtocol.Purposes.EnrollmentResult, pinned.PublicKey))
            {
                throw new AgentException("The enrollment answer is not signed by the pinned panel key; nothing was saved.");
            }

            var result = ProtocolJson.Deserialize<EnrollmentResult>(signed.Payload);
            if (!string.Equals(result.PanelOrigin, origin, StringComparison.Ordinal))
            {
                throw new AgentException($"The panel answered for {result.PanelOrigin}, not {origin}; nothing was saved.");
            }

            if (!result.PanelKeys.Any(k => k.KeyId == pinned.KeyId))
            {
                throw new AgentException("The panel's key list does not include the key that signed the enrollment; nothing was saved.");
            }

            var config = new AgentConfig(panel, result.NodeId, result.CredentialId, key.Reference, result.PanelKeys,
                request.ServersRoot, clock.GetUtcNow(), request.Development, BackupsRoot: request.BackupsRoot);
            configs.Save(config);
            saved = true;
            AwaitingApproval = result.AwaitingApproval;

            // Re-enrollment: the panel has already revoked the old credential, so its key is useless now.
            if (previous is not null && previous.Key != key.Reference)
            {
                keys.Delete(previous.Key);
            }

            return config;
        }
        catch when (!saved)
        {
            keys.Delete(key.Reference);
            throw;
        }
        finally
        {
            key.Dispose();
        }
    }
}
