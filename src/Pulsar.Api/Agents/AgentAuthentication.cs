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
internal sealed partial class AgentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AgentSessionStore sessions,
    AgentsEndpoint agents, AgentTransportPolicy transportPolicy)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "PulsarAgent";
    public const string NodeIdClaim = "pulsar:node";
    public const string CredentialIdClaim = "pulsar:credential";
    public const string SessionIdClaim = "pulsar:session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var session = sessions.Find(header["Bearer ".Length..].Trim());
        if (session is null)
        {
            return AuthenticateResult.Fail("Unknown or expired agent session.");
        }

        // A session signed in with a client certificate needs it on every request: the token alone is worth nothing.
        // One signed in without belongs to the main endpoint, while certificates are not required (Phase 16).
        var transport = agents.TransportOf(Context);
        if (session.CertificateKey is not null
            ? !transport.ThroughAgentsEndpoint || transport.ClientKeyFingerprint != session.CertificateKey
            : transport.ThroughAgentsEndpoint || transportPolicy.RequiresCertificateFor(transport))
        {
            // The theft client certificates defend against (a copied token, without the key it was issued with): the
            // node's administrators should hear of it.
            LogSessionCertificateMismatch(Logger, session.NodeId, session.SessionId, transport.ThroughAgentsEndpoint);
            await Context.RequestServices.GetRequiredService<AgentAccess>().SessionRefusedAsync(session.NodeId, session.CredentialId, Context.RequestAborted);
            return AuthenticateResult.Fail("The agent session does not match this connection's client certificate.");
        }

        // A connection of the agents' endpoint that carries a node's session leaves the budgets and the deadline of
        // connections not signed in, and lasts as long as the session (AgentsConnectionLimit). A node holds so many.
        if (Context.Features.Get<AgentsEndpointConnection>() is { } connection)
        {
            if (connection.Authenticated(session.NodeId, session.SessionId, session.ExpiresAt) is not { } request)
            {
                return AuthenticateResult.Fail("The node holds as many connections to the agents' endpoint as it may.");
            }

            Context.Response.RegisterForDispose(request);
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
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A request presented the session token {SessionId} of node {NodeId} without the session's client certificate (through the agents' endpoint: {ThroughAgentsEndpoint}): refused")]
    private static partial void LogSessionCertificateMismatch(ILogger logger, NodeId nodeId, Guid sessionId, bool throughAgentsEndpoint);
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
