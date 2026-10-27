using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>A CurseForge project of any game, as the editor of a variable that lists them shows it.</summary>
/// <param name="Url">Its page on CurseForge's site; null when CurseForge gives none there.</param>
public sealed record CurseForgeProject(string Id, string Name, int GameId, string? Author, long Downloads, DateTimeOffset? Updated, string? Url);

/// <summary>CurseForge's projects of any game (Pulsar 1.0.14), with the administrator's key: the game's own mods, by id or by a search.</summary>
public interface ICurseForgeProjects
{
    /// <summary>Whether the panel has a key to ask CurseForge with.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);

    /// <summary>The game's projects, the most popular first; with <paramref name="query"/>, those it matches.</summary>
    /// <exception cref="ModCatalogUnavailableException">CurseForge could not be asked.</exception>
    Task<(IReadOnlyList<CurseForgeProject> Projects, int Total)> SearchGameAsync(int gameId, string? query, int offset, int limit,
        CancellationToken cancellationToken);

    /// <summary>The projects CurseForge knows of <paramref name="ids"/>, of whatever game.</summary>
    /// <exception cref="ModCatalogUnavailableException">CurseForge could not be asked.</exception>
    Task<IReadOnlyList<CurseForgeProject>> ProjectsByIdAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);
}

/// <summary>One project of a variable's list, looked up.</summary>
/// <param name="OtherGame">CurseForge knows it, as another game's.</param>
public sealed record CurseForgeProjectView(string Id, bool Found, string? Name, bool OtherGame, string? Author, long? Downloads, DateTimeOffset? Updated,
    string? Url);

public sealed record CurseForgeSearchView(IReadOnlyList<CurseForgeProjectView> Hits, int Total);

/// <summary>
/// The editor of a variable that lists CurseForge projects (Pulsar 1.0.14; docs/architecture/04, "Variables"; ARK:
/// Survival Ascended's mods): what each id is, and a search of the game's projects, so users pick them by name. The game
/// downloads them itself; Pulsar never does. CurseForge needs the administrator's key, as its mod catalog does.
/// </summary>
public sealed class CurseForgeLists(ICurseForgeProjects curseForge)
{
    public const int MaxIds = 100;
    public const int PageSize = 20;

    public async Task<Result<List<CurseForgeProjectView>>> LookupAsync(int gameId, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        if (gameId < 1 || distinct.Count is 0 or > MaxIds || !distinct.All(IsId))
        {
            return ModErrors.ListInvalid;
        }

        if (!await curseForge.IsConfiguredAsync(cancellationToken))
        {
            return ModErrors.CatalogNotConfigured;
        }

        return await CatalogAsync(async () =>
        {
            var found = (await curseForge.ProjectsByIdAsync(distinct, cancellationToken)).GroupBy(p => p.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            return distinct.Select(id => found.TryGetValue(id, out var project)
                ? new CurseForgeProjectView(id, true, project.Name, project.GameId != gameId, project.Author, project.Downloads, project.Updated, project.Url)
                : new CurseForgeProjectView(id, false, null, false, null, null, null, null)).ToList();
        });
    }

    /// <summary>The game's projects, a page of <see cref="PageSize"/>.</summary>
    public async Task<Result<CurseForgeSearchView>> SearchAsync(int gameId, string? query, int offset, CancellationToken cancellationToken)
    {
        var text = query?.Trim();
        if (gameId < 1)
        {
            return ModErrors.ListInvalid;
        }

        if (text is { Length: > 100 } || text?.Any(char.IsControl) == true)
        {
            return ModErrors.QueryInvalid;
        }

        if (!await curseForge.IsConfiguredAsync(cancellationToken))
        {
            return ModErrors.CatalogNotConfigured;
        }

        return await CatalogAsync(async () =>
        {
            var (projects, total) = await curseForge.SearchGameAsync(gameId, text, Math.Clamp(offset, 0, 9_000), PageSize, cancellationToken);
            return new CurseForgeSearchView([.. projects.Select(p => new CurseForgeProjectView(p.Id, true, p.Name, p.GameId != gameId, p.Author, p.Downloads,
                p.Updated, p.Url))], total);
        });
    }

    /// <summary>A CurseForge project's id: a positive number without leading zeros.</summary>
    public static bool IsId(string id) =>
        id is { Length: > 0 and <= 10 } && id[0] != '0' && id.All(char.IsAsciiDigit) && int.TryParse(id, out _);

    private static async Task<Result<T>> CatalogAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ModCatalogUnavailableException ex)
        {
            return ex.NotConfigured ? ModErrors.CatalogNotConfigured : ex.Busy ? ModErrors.CatalogBusy : ModErrors.CatalogUnavailable(ex.Message);
        }
    }
}
