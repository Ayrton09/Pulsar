using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Agents;

/// <summary>
/// Authentication of agents on the hub: <c>Authorization: Bearer &lt;session token&gt;</c> issued by
/// <c>/agent/v1/auth/token</c>. A browser cookie never authenticates here and an agent token never works on /api.
/// </summary>
internal sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AgentSessionStore sessions,
    AgentsEndpoint agents, AgentTransportPolicy transportPolicy)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "PulsarAgent";
    public const string NodeIdClaim = "pulsar:node";
    public const string CredentialIdClaim = "pulsar:credential";
    public const string SessionIdClaim = "pulsar:session";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var session = sessions.Find(header["Bearer ".Length..].Trim());
        if (session is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown or expired agent session."));
        }

        // A session signed in with a client certificate needs it on every request: the token alone is worth nothing.
        // One signed in without belongs to the main endpoint, while certificates are not required (Phase 16).
        var transport = agents.TransportOf(Context);
        if (session.CertificateKey is not null
            ? !transport.ThroughAgentsEndpoint || transport.ClientKeyFingerprint != session.CertificateKey
            : transport.ThroughAgentsEndpoint || transportPolicy.RequiresCertificateFor(transport))
        {
            return Task.FromResult(AuthenticateResult.Fail("The agent session does not match this connection's client certificate."));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(NodeIdClaim, session.NodeId.ToString()),
            new Claim(CredentialIdClaim, session.CredentialId.ToString()),
            new Claim(SessionIdClaim, session.SessionId.ToString()),
        ], Scheme);

        // The hub closes the connection when the session expires (CloseOnAuthenticationExpiration); the agent then
        // re-authenticates with a fresh challenge.
        var properties = new AuthenticationProperties { ExpiresUtc = session.ExpiresAt };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme)));
    }
}

internal static class AgentClaims
{
    public static NodeId NodeId(this ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirstValue(AgentAuthenticationHandler.NodeIdClaim)!, CultureInfo.InvariantCulture));

    public static Guid CredentialId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(AgentAuthenticationHandler.CredentialIdClaim)!, CultureInfo.InvariantCulture);

    public static Guid SessionId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(AgentAuthenticationHandler.SessionIdClaim)!, CultureInfo.InvariantCulture);
}

/// <summary>Endpoint metadata: authenticated by the agent protocol, not by a user permission (read by the coverage test).</summary>
public sealed class AgentEndpointMetadata
{
    public static AgentEndpointMetadata Instance { get; } = new();
}
