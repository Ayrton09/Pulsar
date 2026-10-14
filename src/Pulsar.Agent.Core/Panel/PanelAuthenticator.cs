using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// Challenge/response authentication (docs/architecture/03): the node signs the panel's nonce together with its ids and
/// the panel origin, and gets a short-lived session for the hub, which it takes only with the panel's grant for it.
/// </summary>
public sealed class PanelAuthenticator(PanelTransport transport, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

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
        CancellationToken cancellationToken) =>
        (await SignInAsync(panel, config, key, certificate, cancellationToken)).Token;

    /// <summary>
    /// <see cref="AuthenticateAsync(Uri, AgentConfig, INodeKey, X509Certificate2?, CancellationToken)"/>, with the panel's
    /// grant and its clock offset. An answer without a grant that verifies (<see cref="PanelSignIn.VerifyGrant"/>) is no
    /// session: <see cref="PanelNotAnsweringException"/>, as whatever answered is not the panel.
    /// </summary>
    public async Task<PanelSignIn> SignInAsync(Uri panel, AgentConfig config, INodeKey key, X509Certificate2? certificate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(key);
        var origin = PanelAddress.Origin(panel);
        using var http = transport.CreateClient(panel, certificate);

        // A refusal that sends the agent to the agents' address signs this back, and so does the session's grant: an old
        // refusal or session played back does not answer this sign-in.
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
        var sentAt = _clock.GetUtcNow();
        var token = await PanelTransport.PostAsync<TokenRequest, TokenResponse>(http, AgentProtocol.TokenPath,
            new TokenRequest(challenge.ChallengeId, signature), cancellationToken);
        var receivedAt = _clock.GetUtcNow();
        if (token.AccessToken is not { Length: > 0 and <= MaxAccessTokenLength })
        {
            throw new PanelNotAnsweringException($"The address answered {AgentProtocol.TokenPath} with a session token of {token.AccessToken?.Length ?? 0} " +
                "characters, which is not the agent protocol: no panel answers it there now.");
        }

        // Its session id and time would otherwise be whatever answered here chose: enough to play recorded commands back.
        if (PanelSignIn.VerifyGrant(token, config, nonce) is not { IssuedAt: { } issuedAt } grant)
        {
            throw new PanelNotAnsweringException($"The address answered {AgentProtocol.TokenPath} with a session the panel did not grant (no grant signed by " +
                "a pinned panel key for this node, key, session and sign-in): it is not used.");
        }

        // The panel's clock is measured from the grant, off by at most this request's time: never more than commands allow.
        if (receivedAt - sentAt > CommandVerifier.ClockTolerance)
        {
            throw new PanelNotAnsweringException($"The panel took {(receivedAt - sentAt).TotalSeconds:0} seconds to answer {AgentProtocol.TokenPath}: " +
                "too long to measure its clock from the answer.");
        }

        return new PanelSignIn(token, grant, issuedAt - receivedAt);
    }
}
