using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>A Workshop item as Steam describes it; <see cref="Found"/> is false when Steam does not know it or hides it.</summary>
/// <param name="AppId">The game whose Workshop it is in.</param>
/// <param name="Children">A collection's items (null for an item that is not a collection).</param>
public sealed record WorkshopItem(string Id, bool Found, string? Title, long? AppId, long? Size, DateTimeOffset? UpdatedAt,
    IReadOnlyList<string>? Children);

/// <summary>Steam's public Workshop API (no key), as the panel uses it.</summary>
public interface ISteamWorkshop
{
    /// <summary>Steam's details of each id, in the same order.</summary>
    /// <exception cref="WorkshopUnavailableException">Steam could not be asked, or answered something else.</exception>
    Task<IReadOnlyList<WorkshopItem>> GetItemsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);
}

public sealed class WorkshopUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>One looked-up item, for the Workshop editor.</summary>
/// <param name="OtherGame">Steam knows it, but in another game's Workshop.</param>
/// <param name="Items">A collection's items (empty for an item): the editor offers to add them instead.</param>
public sealed record WorkshopItemView(string Id, bool Found, string? Title, bool OtherGame, long? Size, DateTimeOffset? UpdatedAt,
    bool IsCollection, IReadOnlyList<string> Items);

/// <summary>
/// Looks up Workshop items for the editor of a variable that lists them (docs/architecture/04, "Variables"): what each
/// id is, so users see names instead of numbers. The game downloads the items itself; Pulsar never does.
/// </summary>
public sealed class WorkshopLookup(ISteamWorkshop steam)
{
    public const int MaxIds = 100;

    public async Task<Result<List<WorkshopItemView>>> LookupAsync(long appId, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        if (appId is < 1 or > uint.MaxValue || distinct.Count is 0 or > MaxIds || !distinct.All(IsId))
        {
            return ServerErrors.WorkshopInvalid;
        }

        IReadOnlyList<WorkshopItem> items;
        try
        {
            items = await steam.GetItemsAsync(distinct, cancellationToken);
        }
        catch (WorkshopUnavailableException ex)
        {
            return ServerErrors.WorkshopUnavailable(ex.Message);
        }

        return items.Select(i => new WorkshopItemView(i.Id, i.Found, i.Title, i.Found && i.AppId != appId, i.Size, i.UpdatedAt,
            i.Children is not null, i.Children ?? [])).ToList();
    }

    /// <summary>A Workshop id: a 64-bit number, without leading zeros.</summary>
    public static bool IsId(string id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 20 && id[0] != '0' && id.All(char.IsAsciiDigit) && ulong.TryParse(id, out _);
}
