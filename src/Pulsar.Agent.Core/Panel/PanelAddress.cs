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
