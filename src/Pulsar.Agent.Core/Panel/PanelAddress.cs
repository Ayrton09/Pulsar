using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// The panel URL the agent was given. HTTPS is required; plain HTTP is accepted only for a loopback panel (traffic
/// that never leaves the machine, e.g. development), because the session token would otherwise travel in clear.
/// </summary>
public static class PanelAddress
{
    /// <summary>Parses and normalizes a panel URL ("https://panel.example.com"); throws <see cref="AgentException"/>.</summary>
    public static Uri Parse(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new AgentException($"'{value}' is not a panel URL. Use the address of the panel, e.g. https://panel.example.com.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new AgentException("The panel must be reached over HTTPS (plain HTTP is only accepted for a panel on this machine).");
        }

        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new AgentException("The panel URL must be just its origin, without a path, query or credentials.");
        }

        return new Uri(Origin(uri) + "/");
    }

    /// <summary>An https origin ("https://panel.example.com:8444/"), or null: what an agents' address must be.</summary>
    public static Uri? TryParseHttpsOrigin(string? value)
    {
        try
        {
            var address = Parse(value ?? string.Empty);
            return address.Scheme == Uri.UriSchemeHttps ? address : null;
        }
        catch (AgentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The agents' address the panel told (Phase 16): its operator's <paramref name="url"/> when there is one, otherwise
    /// <paramref name="port"/> on the host of <paramref name="reached"/>, the address this agent reached the panel by. The
    /// host never comes from the panel, so a proxy that sets the request's host cannot pick it. Null when invalid.
    /// </summary>
    public static Uri? AgentsAddress(Uri reached, int port, string? url)
    {
        ArgumentNullException.ThrowIfNull(reached);
        if (url is not null)
        {
            return TryParseHttpsOrigin(url);
        }

        return port is > 0 and <= 65535 ? TryParseHttpsOrigin(new UriBuilder(Uri.UriSchemeHttps, reached.Host, port).Uri.ToString()) : null;
    }

    /// <summary>"scheme://host[:port]" in lower case: what the panel binds into challenges and enrollment results.</summary>
    public static string Origin(Uri panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        return panel.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}

/// <summary>A failure with a message meant for the person running the agent.</summary>
public class AgentException : Exception
{
    public AgentException()
    {
    }

    public AgentException(string message)
        : base(message)
    {
    }

    public AgentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The panel turned the request away for too many attempts from this address (429): it may be tried again later.</summary>
public sealed class PanelBusyException : AgentException
{
    public PanelBusyException()
    {
    }

    public PanelBusyException(string message)
        : base(message)
    {
    }

    public PanelBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PanelBusyException(string message, TimeSpan? retryAfter)
        : base(message) => RetryAfter = retryAfter;

    /// <summary>How long the panel asked to wait (its Retry-After), when it said.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// The panel requires this agent to sign in on its agents' address with its node key as a TLS client certificate
/// (Phase 16). <see cref="AgentsAddress"/> says where, signed for this node, when the refusal came with one.
/// </summary>
public sealed class CertificateRequiredException : AgentException
{
    public const string Code = "agent.certificate_required";

    public CertificateRequiredException()
    {
    }

    public CertificateRequiredException(string message)
        : base(message)
    {
    }

    public CertificateRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CertificateRequiredException(string message, SignedMessage? agentsAddress)
        : base(message) => AgentsAddress = agentsAddress;

    /// <param name="refusedAt">The address that refused (the agents' port goes on its host).</param>
    /// <param name="challengeNonce">The nonce of the challenge request it refused, which a trustworthy address carries.</param>
    public CertificateRequiredException(string message, SignedMessage? agentsAddress, Uri refusedAt, byte[] challengeNonce)
        : base(message)
    {
        AgentsAddress = agentsAddress;
        RefusedAt = refusedAt;
        ChallengeNonce = challengeNonce;
    }

    public SignedMessage? AgentsAddress { get; }

    public Uri? RefusedAt { get; }

    public byte[]? ChallengeNonce { get; }
}

/// <summary>
/// What answered is not the panel's agent protocol (a 404, a 401 or 403 without the panel's error, a page instead of its
/// answer) or cannot serve it now (5xx): a port forwarded to something else, a proxy whose panel is down. Like no answer
/// at all, it sends the agent to its fallback address.
/// </summary>
public sealed class PanelNotAnsweringException : AgentException
{
    public PanelNotAnsweringException()
    {
    }

    public PanelNotAnsweringException(string message)
        : base(message)
    {
    }

    public PanelNotAnsweringException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The panel refused this node's credential (revoked, node disabled or deleted). Retrying will not help soon.</summary>
public sealed class AgentRejectedException : AgentException
{
    public AgentRejectedException()
    {
    }

    public AgentRejectedException(string message)
        : base(message)
    {
    }

    public AgentRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
