namespace Pulsar.Application.Servers;

/// <summary>A project in Modrinth's search: a mod, a plugin.</summary>
public sealed record ModrinthHit(string ProjectId, string Slug, string Title, string? Description, string? Author, long Downloads, DateTimeOffset? Updated);

public sealed record ModrinthSearch(IReadOnlyList<ModrinthHit> Hits, int Total);

/// <summary>A file of a version: its address on Modrinth's CDN and the checksum Modrinth lists for it.</summary>
public sealed record ModrinthFile(string Url, string FileName, string Sha512, long Size, bool Primary);

/// <summary>What a version needs (<c>required</c>), may use (<c>optional</c>), carries (<c>embedded</c>) or cannot run with (<c>incompatible</c>).</summary>
public sealed record ModrinthDependency(string? ProjectId, string? VersionId, string Type);

public sealed record ModrinthVersion(
    string Id,
    string ProjectId,
    string Name,
    string VersionNumber,
    string VersionType,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<string> GameVersions,
    DateTimeOffset Published,
    IReadOnlyList<ModrinthFile> Files,
    IReadOnlyList<ModrinthDependency> Dependencies);

public sealed record ModrinthProject(string Id, string Slug, string Title);

/// <summary>Modrinth's public API (no key; it asks for a User-Agent naming the application), as the panel uses it.</summary>
public interface IModrinth
{
    /// <exception cref="ModCatalogUnavailableException">Modrinth could not be asked, or answered something else.</exception>
    Task<ModrinthSearch> SearchAsync(string? query, string projectType, IReadOnlyList<string> loaders, string gameVersion, int offset, int limit,
        CancellationToken cancellationToken);

    /// <summary>The project's versions for any of the loaders and the game version, newest first.</summary>
    /// <exception cref="ModCatalogUnavailableException">Modrinth could not be asked (a project it does not know is no version).</exception>
    Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string projectId, IReadOnlyList<string> loaders, string gameVersion, CancellationToken cancellationToken);

    /// <summary>One version; null when Modrinth does not know it.</summary>
    /// <exception cref="ModCatalogUnavailableException">Modrinth could not be asked.</exception>
    Task<ModrinthVersion?> VersionAsync(string versionId, CancellationToken cancellationToken);

    /// <summary>Projects by id or slug (those Modrinth knows).</summary>
    /// <exception cref="ModCatalogUnavailableException">Modrinth could not be asked.</exception>
    Task<IReadOnlyList<ModrinthProject>> ProjectsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);

    /// <summary>For each file (by its SHA-512), the newest version of its project for the loaders and game version.</summary>
    /// <exception cref="ModCatalogUnavailableException">Modrinth could not be asked.</exception>
    Task<IReadOnlyDictionary<string, ModrinthVersion>> LatestVersionsAsync(IReadOnlyList<string> sha512s, IReadOnlyList<string> loaders, string gameVersion,
        CancellationToken cancellationToken);
}

/// <summary>A mod catalog could not be asked; <see cref="Busy"/> when it said this panel asks too often.</summary>
public sealed class ModCatalogUnavailableException(string message, bool busy = false, Exception? innerException = null) : Exception(message, innerException)
{
    public bool Busy { get; } = busy;
}
