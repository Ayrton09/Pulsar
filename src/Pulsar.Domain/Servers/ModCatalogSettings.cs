namespace Pulsar.Domain.Servers;

/// <summary>
/// The panel's settings for mod catalogs (one row; Pulsar 1.0.9). CurseForge's API takes a key per developer that its
/// terms forbid sharing, so each panel's administrator brings their own: kept encrypted with the panel's Data Protection
/// key ring, never returned, never sent to nodes.
/// </summary>
public sealed class ModCatalogSettings
{
    public const int SingletonId = 1;

    private ModCatalogSettings()
    {
        // For EF Core materialization.
    }

    public int Id { get; private set; }

    /// <summary>The administrator's CurseForge API key, encrypted; null when there is none.</summary>
    public byte[]? CurseForgeKey { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ModCatalogSettings Default(DateTimeOffset now) => new() { Id = SingletonId, UpdatedAt = now };

    public void SetCurseForgeKey(byte[]? protectedKey, DateTimeOffset now)
    {
        CurseForgeKey = protectedKey;
        UpdatedAt = now;
    }
}
