using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulsar.Application.Servers;

namespace Pulsar.Infrastructure.Steam;

/// <summary>
/// Steam's public Workshop methods (<c>ISteamRemoteStorage/GetPublishedFileDetails</c> and <c>GetCollectionDetails</c>),
/// which need no key: only <c>api.steampowered.com</c>, over HTTPS, without redirects, with a cap on what is read.
/// </summary>
public sealed class SteamWorkshopClient(IHttpClientFactory clients) : ISteamWorkshop
{
    public const string ClientName = "Pulsar.Steam";
    public static readonly Uri BaseAddress = new("https://api.steampowered.com/");

    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxTitleLength = 128;
    private const int MaxChildren = 1000;

    public async Task<IReadOnlyList<WorkshopItem>> GetItemsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var http = clients.CreateClient(ClientName);
        // Asked together: the details of each id, and the items of those that are collections.
        var detailsTask = PostAsync<DetailsResponse>(http, "ISteamRemoteStorage/GetPublishedFileDetails/v1/", "itemcount", ids, cancellationToken);
        var collectionsTask = PostAsync<CollectionsResponse>(http, "ISteamRemoteStorage/GetCollectionDetails/v1/", "collectioncount", ids, cancellationToken);
        var details = (await detailsTask).Response?.Details ?? [];
        var collections = (await collectionsTask).Response?.Collections ?? [];

        return [.. ids.Select(id =>
        {
            var detail = details.FirstOrDefault(d => d.Id == id);
            var collection = collections.FirstOrDefault(c => c.Id == id && c.Result == 1 && c.Children is not null);
            if (detail is not { Result: 1 })
            {
                return new WorkshopItem(id, false, null, null, null, null, null);
            }

            var children = collection?.Children!.OrderBy(c => c.SortOrder).Select(c => c.Id).OfType<string>().Where(WorkshopLookup.IsId)
                .Distinct(StringComparer.Ordinal).Take(MaxChildren).ToList();
            return new WorkshopItem(id, true, Clean(detail.Title), detail.ConsumerAppId, Number(detail.FileSize),
                detail.TimeUpdated is > 0 ? DateTimeOffset.FromUnixTimeSeconds(detail.TimeUpdated.Value) : null, children);
        })];
    }

    private static async Task<T> PostAsync<T>(HttpClient http, string path, string countName, IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new(countName, ids.Count.ToString(CultureInfo.InvariantCulture)) };
        form.AddRange(ids.Select((id, i) => new KeyValuePair<string, string>($"publishedfileids[{i}]", id)));
        // The client's timeout ends at the headers when the body is read separately: this deadline covers both.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(http.Timeout);
        try
        {
            // Headers first: the body is read below, up to the cap, whether or not Steam says its length.
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseAddress, path)) { Content = new FormUrlEncodedContent(form) };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new WorkshopUnavailableException($"Steam answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new WorkshopUnavailableException("Steam's answer is larger than Pulsar reads.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new WorkshopUnavailableException("Steam's answer is larger than Pulsar reads.");
                }

                buffer.Write(chunk, 0, read);
            }

            return JsonSerializer.Deserialize<T>(buffer.ToArray()) ?? throw new WorkshopUnavailableException("Steam sent an empty answer.");
        }
        catch (HttpRequestException ex)
        {
            throw new WorkshopUnavailableException($"Steam could not be reached ({ex.HttpRequestError}).", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkshopUnavailableException("Steam did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw new WorkshopUnavailableException("Steam sent an answer Pulsar does not understand.", ex);
        }
    }

    /// <summary>Authors write titles: no control or direction characters, and a length the editor can show.</summary>
    private static string? Clean(string? title)
    {
        if (title is null)
        {
            return null;
        }

        var cleaned = new string([.. title.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim();
        return cleaned.Length > MaxTitleLength ? cleaned[..MaxTitleLength] : cleaned;
    }

    private static long? Number(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var x) => x,
        { ValueKind: JsonValueKind.String } s when long.TryParse(s.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var x) => x,
        _ => null,
    };

    private sealed record DetailsResponse([property: JsonPropertyName("response")] DetailsBody? Response);

    private sealed record DetailsBody([property: JsonPropertyName("publishedfiledetails")] List<Detail>? Details);

    private sealed record Detail(
        [property: JsonPropertyName("publishedfileid")] string? Id,
        [property: JsonPropertyName("result")] int Result,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("consumer_app_id")] long? ConsumerAppId,
        [property: JsonPropertyName("file_size")] JsonElement? FileSize,
        [property: JsonPropertyName("time_updated")] long? TimeUpdated);

    private sealed record CollectionsResponse([property: JsonPropertyName("response")] CollectionsBody? Response);

    private sealed record CollectionsBody([property: JsonPropertyName("collectiondetails")] List<Collection>? Collections);

    private sealed record Collection(
        [property: JsonPropertyName("publishedfileid")] string? Id,
        [property: JsonPropertyName("result")] int Result,
        [property: JsonPropertyName("children")] List<Child>? Children);

    private sealed record Child(
        [property: JsonPropertyName("publishedfileid")] string? Id,
        [property: JsonPropertyName("sortorder")] int SortOrder);
}
