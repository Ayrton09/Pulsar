using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>
/// An addon of the server's game installed on it (docs/architecture/02, "Addons"), as its agent last reported: the
/// release it has. Its files and records are on the node; this row is what the panel shows and sends in the spec.
/// </summary>
public sealed class ServerAddon
{
    public const int MaxKeyLength = 32;
    public const int MaxReleaseLength = 128;

    private ServerAddon()
    {
        // For EF Core materialization.
        Key = null!;
        Release = null!;
    }

    private ServerAddon(ServerId serverId, string key, string release, DateTimeOffset now)
    {
        ServerId = serverId;
        Key = key;
        Release = Truncate(release);
        InstalledAt = now;
        UpdatedAt = now;
    }

    public ServerId ServerId { get; private set; }

    public string Key { get; private set; }

    /// <summary>The release installed (a GitHub tag).</summary>
    public string Release { get; private set; }

    public DateTimeOffset InstalledAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ServerAddon Installed(ServerId serverId, string key, string release, DateTimeOffset now) => new(serverId, key, release, now);

    /// <summary>Another release was installed (on request, or after a game update).</summary>
    public void Updated(string release, DateTimeOffset now)
    {
        Release = Truncate(release);
        UpdatedAt = now;
    }

    private static string Truncate(string release) => TextLimits.Truncate(release, MaxReleaseLength);
}
