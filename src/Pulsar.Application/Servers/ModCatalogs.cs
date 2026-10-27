namespace Pulsar.Application.Servers;

/// <summary>A project in a catalog's search: a mod, a plugin, a modpack.</summary>
public sealed record CatalogHit(string ProjectId, string Slug, string Title, string? Description, string? Author, long Downloads, DateTimeOffset? Updated);

public sealed record CatalogSearch(IReadOnlyList<CatalogHit> Hits, int Total);

/// <summary>A file of a version: its address on the catalog's CDN and the checksum the catalog lists for it.</summary>
/// <param name="Hash">Lowercase hex: SHA-512 (Modrinth's) or SHA-1 (CurseForge's, the only one it publishes).</param>
public sealed record CatalogFile(string Url, string FileName, string Hash, long Size, bool Primary);

/// <summary>What a version needs (<c>required</c>), may use (<c>optional</c>), carries (<c>embedded</c>) or cannot run with (<c>incompatible</c>).</summary>
public sealed record CatalogDependency(string? ProjectId, string? VersionId, string Type);

/// <param name="Files">Its files the panel can have a node download (none when its author does not let them be).</param>
/// <param name="DistributionForbidden">The catalog has its file but does not give it out to other programs (its author's choice).</param>
/// <param name="ServerPack">A modpack's server files, which its author publishes beside it (CurseForge); null otherwise.</param>
public sealed record CatalogVersion(
    string Id,
    string ProjectId,
    string Name,
    string VersionNumber,
    string VersionType,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<string> GameVersions,
    DateTimeOffset Published,
    IReadOnlyList<CatalogFile> Files,
    IReadOnlyList<CatalogDependency> Dependencies,
    bool DistributionForbidden = false,
    CatalogFile? ServerPack = null);

public sealed record CatalogProject(string Id, string Slug, string Title);

/// <summary>A mod on the server, as a catalog looks its updates up: by its project, or by its file's checksum.</summary>
public sealed record CatalogInstalled(string ProjectId, string VersionId, string Hash);

/// <summary>
/// A mod catalog's public API as the panel uses it (Pulsar 1.0.9): Modrinth (no key), CurseForge (the administrator's
/// key). Project types are <c>mod</c>, <c>plugin</c> and <c>modpack</c>; loaders the catalog's names in lowercase.
/// </summary>
public interface IModCatalog
{
    /// <summary>The source's name in manifests and the API: <c>modrinth</c>, <c>curseforge</c>.</summary>
    string Source { get; }

    /// <summary>Whether the panel can ask it (CurseForge needs the administrator's key).</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);

    /// <param name="gameVersion">Only projects for this game version; null: any.</param>
    /// <param name="community">The catalog's name for the game (uMod's <c>rust</c>), for a catalog of several games' files.</param>
    /// <param name="category">The catalog's category the projects are in (Thunderstore's <c>server-side</c>); any when null.</param>
    /// <exception cref="ModCatalogUnavailableException">The catalog could not be asked, or answered something else.</exception>
    Task<CatalogSearch> SearchAsync(string? query, string projectType, IReadOnlyList<string> loaders, string? gameVersion, int offset, int limit,
        CancellationToken cancellationToken, string? community = null, string? category = null);

    /// <summary>The project's versions for any of the loaders and the game version (any, when null), newest first.</summary>
    /// <exception cref="ModCatalogUnavailableException">The catalog could not be asked (a project it does not know has no versions).</exception>
    Task<IReadOnlyList<CatalogVersion>> VersionsAsync(string projectId, IReadOnlyList<string> loaders, string? gameVersion, CancellationToken cancellationToken);

    /// <summary>One version; null when the catalog does not know it.</summary>
    /// <exception cref="ModCatalogUnavailableException">The catalog could not be asked.</exception>
    Task<CatalogVersion?> VersionAsync(string versionId, CancellationToken cancellationToken);

    /// <summary>Projects by id (or slug, for Modrinth): those the catalog knows.</summary>
    /// <exception cref="ModCatalogUnavailableException">The catalog could not be asked.</exception>
    Task<IReadOnlyList<CatalogProject>> ProjectsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);

    /// <summary>
    /// For each installed mod's project, the newest version for the loaders and game version (by project id); any game
    /// version when null (a catalog whose files are not made per game version).
    /// </summary>
    /// <exception cref="ModCatalogUnavailableException">The catalog could not be asked.</exception>
    Task<IReadOnlyDictionary<string, CatalogVersion>> LatestVersionsAsync(IReadOnlyList<CatalogInstalled> installed, IReadOnlyList<string> loaders,
        string? gameVersion, CancellationToken cancellationToken);
}

/// <summary>The catalogs the panel knows, by source.</summary>
public sealed class ModCatalogs(IEnumerable<IModCatalog> catalogs)
{
    private readonly Dictionary<string, IModCatalog> _bySource = catalogs.ToDictionary(c => c.Source, StringComparer.Ordinal);

    public IModCatalog? For(string source) => _bySource.GetValueOrDefault(source);
}

/// <summary>
/// A mod catalog could not be asked; <see cref="Busy"/> when it said this panel asks too often, <see cref="NotConfigured"/>
/// when it needs a key the panel does not have (or that it refused).
/// </summary>
public sealed class ModCatalogUnavailableException(string message, bool busy = false, Exception? innerException = null, bool notConfigured = false)
    : Exception(message, innerException)
{
    public bool Busy { get; } = busy;

    public bool NotConfigured { get; } = notConfigured;
}
