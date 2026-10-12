using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <param name="Panel">Panel URL as typed by the administrator.</param>
/// <param name="Token">Single-use enrollment token shown by the panel.</param>
/// <param name="PanelKeyFingerprint">Fingerprint of the panel key, shown next to the token: the agent pins it.</param>
/// <param name="ServersRoot">Where server directories go (null: the default); the folder is restricted like the state directory.</param>
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
    /// <summary>How long enrollment keeps waiting for a panel that limits enrollments from this address.</summary>
    public static readonly TimeSpan MaxBusyWait = TimeSpan.FromMinutes(10);

    /// <summary>The last enrollment created a node that waits for an administrator's approval (a reusable token's).</summary>
    public bool AwaitingApproval { get; private set; }

    /// <summary>Told before each wait for a busy panel, with how long it is.</summary>
    public Action<TimeSpan>? Waiting { get; set; }

    /// <summary>Told what the enrollment went on despite (an unreadable earlier configuration).</summary>
    public Action<string>? Warning { get; set; }

    public async Task<AgentConfig> EnrollAsync(EnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Development)
        {
            RefuseProductionState();
        }

        var panel = PanelAddress.Parse(request.Panel);
        var origin = PanelAddress.Origin(panel);

        // The key comes first: a panel's agents' address asks for it as the client certificate from the first request on
        // (Phase 16). It is deleted again unless the enrollment is saved.
        var scope = request.Development ? NodeKeyScope.User : NodeKeyScope.Machine;
        var key = keys.Create($"PulsarAgent-{Guid.NewGuid():N}", scope);
        var saved = false;
        try
        {
            using var certificate = NodeCertificate.TryCreate(key, out _);
            using var http = transport.CreateClient(panel, certificate);
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

            // Steam logins are lent to server directories in it, and the agent applies their rules there: no one else may
            // put a folder of theirs in its place (docs/architecture/06, "Isolation").
            directorySecurity.SecureServersRoot(request.ServersRoot ?? AgentConfig.DefaultServersRoot(configs.Paths, request.Development), request.Development);

            var previous = Previous();
            var publicKey = key.PublicKey;
            var proof = key.Sign(ProtocolCrypto.SignedData(AgentProtocol.Purposes.Enroll, ProtocolCrypto.Utf8(request.Token), publicKey));
            var signed = await PostEnrollAsync(http,
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

    /// <summary>
    /// A development enrollment would replace the configuration in its state directory and delete the machine key it
    /// names: never a production enrollment's (the default state directory is the service's).
    /// </summary>
    private void RefuseProductionState()
    {
        bool production;
        try
        {
            production = configs.Load() is { Development: false };
        }
        catch (UnauthorizedAccessException)
        {
            // Only administrators may read it: a production state directory.
            production = true;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            production = false;
        }

        if (production)
        {
            throw new AgentException($"""
                {configs.Paths.Root} holds this machine's production enrollment, which a development enrollment would replace.
                Pass --data-dir with another folder for a development agent (or unenroll the production one first).
                """);
        }
    }

    /// <summary>The configuration being replaced; one that cannot be read is replaced as if there were none.</summary>
    private AgentConfig? Previous()
    {
        try
        {
            return configs.Load();
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            Warning?.Invoke($"{configs.Paths.ConfigFile} cannot be read ({ex.Message}): it is replaced, and the node key it names stays in the key store.");
            return null;
        }
    }

    private static bool IsUnreadable(Exception exception) =>
        exception is System.Text.Json.JsonException or InvalidDataException or NotSupportedException;

    /// <summary>
    /// Many machines enrolling at once from one address (a rollout behind NAT) go over the panel's per-address limit:
    /// each waits as long as the panel says, plus a random share so they do not all come back together, for up to
    /// <see cref="MaxBusyWait"/>. Only that answer is retried: a refused token is not (it would only use the limit up).
    /// </summary>
    private async Task<SignedMessage> PostEnrollAsync(HttpClient http, EnrollRequest enroll, CancellationToken cancellationToken)
    {
        var giveUpAt = clock.GetUtcNow() + MaxBusyWait;
        while (true)
        {
            try
            {
                return await PanelTransport.PostAsync<EnrollRequest, SignedMessage>(http, AgentProtocol.EnrollPath, enroll, cancellationToken);
            }
            catch (PanelBusyException busy)
            {
                var asked = busy.RetryAfter is { } after ? TimeSpan.FromSeconds(Math.Clamp(after.TotalSeconds, 1, 60)) : TimeSpan.FromSeconds(30);
                var wait = asked + TimeSpan.FromSeconds(Random.Shared.NextDouble() * 10);
                if (clock.GetUtcNow() + wait > giveUpAt)
                {
                    throw new AgentException(
                        $"The panel turned the enrollment away for too many attempts from this address for {MaxBusyWait.TotalMinutes:0} minutes. " +
                        "Try again later, or raise RateLimiting:Auth:EnrollPermitLimit in the panel's settings for a large rollout.", busy);
                }

                Waiting?.Invoke(wait);
                await Task.Delay(wait, clock, cancellationToken);
            }
        }
    }
}
