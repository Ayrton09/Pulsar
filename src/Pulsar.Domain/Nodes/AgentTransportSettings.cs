namespace Pulsar.Domain.Nodes;

/// <summary>
/// How agents must connect (one row, Phase 16). With <see cref="RequireClientCertificates"/>, an agent session needs
/// the node key as a TLS client certificate on the agents' endpoint; the main endpoint turns agents away with that
/// address. Off by default: the session token alone, over TLS, is the v1 protocol.
/// </summary>
public sealed class AgentTransportSettings
{
    public const int SingletonId = 1;

    private AgentTransportSettings()
    {
        // For EF Core materialization.
    }

    public int Id { get; private set; }

    public bool RequireClientCertificates { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AgentTransportSettings Default(DateTimeOffset now) => new() { Id = SingletonId, UpdatedAt = now };

    /// <summary>False when it already was so.</summary>
    public bool ChangeRequirement(bool requireClientCertificates, DateTimeOffset now)
    {
        if (RequireClientCertificates == requireClientCertificates)
        {
            return false;
        }

        RequireClientCertificates = requireClientCertificates;
        UpdatedAt = now;
        return true;
    }
}
