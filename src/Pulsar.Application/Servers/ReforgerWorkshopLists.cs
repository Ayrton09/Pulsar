using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>A mod of Arma Reforger's Workshop, as the editor of a variable that lists them shows it.</summary>
/// <param name="Id">16 hexadecimal digits, in capitals (<c>5965550F24A0C152</c>).</param>
/// <param name="Size">Its current version's size in bytes.</param>
public sealed record ReforgerMod(string Id, string Name, string? Author, string? Version, long? Size, DateTimeOffset? Updated);

/// <summary>
/// Arma Reforger's Workshop (Pulsar 1.0.14): its public pages, read by the panel; no key. <c>requester</c> names who
/// asks: each user reads a share of the pages the panel reads a minute, so that one cannot take them all.
/// </summary>
public interface IReforgerWorkshop
{
    /// <summary>A page of the Workshop's mods (<see cref="ReforgerWorkshopLists.PageSize"/>), the most popular first; with <paramref name="query"/>, those it matches.</summary>
    /// <param name="page">From 1.</param>
    /// <exception cref="ModCatalogUnavailableException">The Workshop could not be asked.</exception>
    Task<(IReadOnlyList<ReforgerMod> Mods, int Total)> SearchAsync(string? query, int page, string requester, CancellationToken cancellationToken);

    /// <summary>The mod of <paramref name="id"/> (16 hexadecimal digits in capitals); null when the Workshop has none.</summary>
    /// <exception cref="ModCatalogUnavailableException">The Workshop could not be asked.</exception>
    Task<ReforgerMod?> ModAsync(string id, string requester, CancellationToken cancellationToken);
}

/// <summary>One mod of a variable's list, looked up.</summary>
/// <param name="Url">Its page in the Workshop; null when the Workshop does not know it.</param>
public sealed record ReforgerModView(string Id, bool Found, string? Name, string? Author, string? Version, long? Size, DateTimeOffset? Updated, string? Url);

public sealed record ReforgerSearchView(IReadOnlyList<ReforgerModView> Hits, int Total);

/// <summary>
/// The editor of a variable that lists Arma Reforger's Workshop mods (Pulsar 1.0.14; docs/architecture/04, "Variables"):
/// what each id is, and a search of the Workshop, so users pick them by name. The game downloads them itself; Pulsar
/// never does. The Workshop has no API for this: the panel reads the data its public pages carry.
/// </summary>
public sealed class ReforgerWorkshopLists(IReforgerWorkshop workshop)
{
    /// <summary>Each id is a page of the Workshop: a lookup asks about this many at most.</summary>
    public const int MaxIds = 50;

    /// <summary>The Workshop's pages hold sixteen mods.</summary>
    public const int PageSize = 16;

    public const int MaxPage = 500;

    private const int MaxConcurrentLookups = 4;

    public static readonly Uri Site = new("https://reforger.armaplatform.com/");

    public async Task<Result<List<ReforgerModView>>> LookupAsync(IReadOnlyList<string> ids, string requester, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count is 0 or > MaxIds || !distinct.All(IsId))
        {
            return ModErrors.ListInvalid;
        }

        return await CatalogAsync(async () =>
        {
            using var gate = new SemaphoreSlim(MaxConcurrentLookups);
            var found = await Task.WhenAll(distinct.Select(async id =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return (Id: id, Mod: await workshop.ModAsync(id.ToUpperInvariant(), requester, cancellationToken));
                }
                finally
                {
                    gate.Release();
                }
            }));
            return found.Select(f => f.Mod is { } mod ? View(mod, f.Id) : new ReforgerModView(f.Id, false, null, null, null, null, null, null)).ToList();
        });
    }

    /// <summary>The Workshop's mods, a page of <see cref="PageSize"/>.</summary>
    /// <param name="page">From 1.</param>
    public async Task<Result<ReforgerSearchView>> SearchAsync(string? query, int page, string requester, CancellationToken cancellationToken)
    {
        var text = query?.Trim();
        if (text is { Length: > 100 } || text?.Any(char.IsControl) == true)
        {
            return ModErrors.QueryInvalid;
        }

        return await CatalogAsync(async () =>
        {
            var (mods, total) = await workshop.SearchAsync(text, Math.Clamp(page, 1, MaxPage), requester, cancellationToken);
            return new ReforgerSearchView([.. mods.Select(m => View(m, m.Id))], total);
        });
    }

    /// <summary>A Workshop id: 16 hexadecimal digits, in capitals or not.</summary>
    public static bool IsId(string id) => id is { Length: 16 } && id.All(char.IsAsciiHexDigit);

    /// <summary>The mod's page in the Workshop, made from its id alone.</summary>
    public static string PageOf(string id) => new Uri(Site, $"workshop/{id.ToUpperInvariant()}").AbsoluteUri;

    private static ReforgerModView View(ReforgerMod mod, string id) =>
        new(id, true, mod.Name, mod.Author, mod.Version, mod.Size, mod.Updated, PageOf(mod.Id));

    private static async Task<Result<T>> CatalogAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ModCatalogUnavailableException ex)
        {
            return ex.Busy ? ModErrors.CatalogBusy : ModErrors.CatalogUnavailable(ex.Message);
        }
    }
}
