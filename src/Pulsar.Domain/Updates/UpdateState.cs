namespace Pulsar.Domain.Updates;

/// <summary>
/// The panel's look at Pulsar's releases (Phase 19, one row): whether it looks once a day (an administrator can turn that
/// off: GitHub sees the panel's address when it does), when it last looked, why that failed, and the newest release it
/// found, kept as its signed manifest so that what it offers and what it asks a node to install are the same release.
/// On by default: a panel without the row looks.
/// </summary>
public sealed class UpdateState
{
    public const int SingletonId = 1;
    public const bool DefaultCheckAutomatically = true;
    public const int MaxErrorLength = 500;
    public const int MaxVersionLength = 32;
    public const int MaxManifestLength = 64 * 1024;

    private UpdateState()
    {
        // For EF Core materialization.
    }

    public int Id { get; private set; }

    public bool CheckAutomatically { get; private set; }

    /// <summary>When the panel last looked, found something or not.</summary>
    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>Why the last look failed (no network, a release not signed by a trusted key...); null when it worked.</summary>
    public string? CheckError { get; private set; }

    public string? LatestVersion { get; private set; }

    /// <summary>The newest release's manifest as it was signed and verified (UTF-8 JSON).</summary>
    public string? LatestManifest { get; private set; }

    /// <summary>Its SHA-256 (lower-case hex): what a node is told to install, and checks the release on GitHub against.</summary>
    public string? LatestManifestSha256 { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static UpdateState Default(DateTimeOffset now) => new()
    {
        Id = SingletonId,
        CheckAutomatically = DefaultCheckAutomatically,
        UpdatedAt = now,
    };

    /// <summary>False when it already was so.</summary>
    public bool ChangeCheckAutomatically(bool checkAutomatically, DateTimeOffset now)
    {
        if (CheckAutomatically == checkAutomatically)
        {
            return false;
        }

        CheckAutomatically = checkAutomatically;
        UpdatedAt = now;
        return true;
    }

    /// <summary>A look that found a verified release (the caller verified its signature).</summary>
    public void Found(string version, string manifest, string manifestSha256, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(manifest);
        ArgumentException.ThrowIfNullOrEmpty(manifestSha256);
        if (version.Length > MaxVersionLength || manifest.Length > MaxManifestLength || manifestSha256.Length != 64)
        {
            throw new ArgumentException("Not a release's version, manifest and SHA-256.");
        }

        LatestVersion = version;
        LatestManifest = manifest;
        LatestManifestSha256 = manifestSha256;
        CheckedAt = now;
        CheckError = null;
        UpdatedAt = now;
    }

    /// <summary>A look that failed: what was found before stays offered.</summary>
    public void Failed(string error, DateTimeOffset now)
    {
        CheckError = string.IsNullOrEmpty(error) ? "The check failed." : error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
        CheckedAt = now;
        UpdatedAt = now;
    }
}
