namespace Pulsar.Domain.Appearance;

/// <summary>
/// How the panel looks for everyone (one row). <see cref="SteamArtwork"/>: the built-in games' pictures come from Steam's
/// store CDN, so every visitor's browser contacts Steam (docs/architecture/08, T41); an administrator can turn that off.
/// On by default: a panel without the row shows them.
/// </summary>
public sealed class AppearanceSettings
{
    public const int SingletonId = 1;
    public const bool DefaultSteamArtwork = true;

    private AppearanceSettings()
    {
        // For EF Core materialization.
    }

    public int Id { get; private set; }

    public bool SteamArtwork { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AppearanceSettings Default(DateTimeOffset now) => new() { Id = SingletonId, SteamArtwork = DefaultSteamArtwork, UpdatedAt = now };

    /// <summary>False when it already was so.</summary>
    public bool ChangeSteamArtwork(bool steamArtwork, DateTimeOffset now)
    {
        if (SteamArtwork == steamArtwork)
        {
            return false;
        }

        SteamArtwork = steamArtwork;
        UpdatedAt = now;
        return true;
    }
}
