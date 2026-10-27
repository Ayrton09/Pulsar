using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Infrastructure.Mods;

/// <summary>
/// Arma Reforger's Workshop (Pulsar 1.0.14; no key, no API): the data its public pages carry for the browser (Next.js's
/// <c>__NEXT_DATA__</c>), from <c>reforger.armaplatform.com</c> only, over HTTPS, without redirects, with a cap on what is
/// read and a User-Agent naming Pulsar. A mod's page is read once an hour at most; the whole panel reads a limited
/// number of pages a minute, and each user a share of them.
/// </summary>
public sealed partial class ReforgerWorkshopClient(IHttpClientFactory clients) : IReforgerWorkshop
{
    public const string ClientName = "Pulsar.ReforgerWorkshop";

    private const int MaxPageBytes = 2 * 1024 * 1024;
    private const int MaxTextLength = 256;
    private const int MaxRememberedMods = 5000;

    private static readonly TimeSpan s_modLifetime = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    // The mods' pages read lately (null: the Workshop has no such mod), for the lists users open again and again.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, ReforgerMod? Mod)> s_mods = new(StringComparer.Ordinal);

    // The Workshop publishes no limit: the whole panel reads at most this many of its pages a minute.
    private static readonly FixedWindowRateLimiter s_budget = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 240,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    // Each user a quarter of it, enough to look up a whole list at once (each id is a page): one user's lookups of
    // made-up ids do not leave the others without the Workshop.
    private static readonly PartitionedRateLimiter<string> s_userBudget = PartitionedRateLimiter.Create<string, string>(user =>
        RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }), StringComparer.Ordinal);

    public async Task<(IReadOnlyList<ReforgerMod> Mods, int Total)> SearchAsync(string? query, int page, string requester, CancellationToken cancellationToken)
    {
        var path = $"workshop?page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}"
            + (string.IsNullOrEmpty(query) ? "" : $"&search={Uri.EscapeDataString(query)}");
        var assets = (await PagePropsAsync(path, requester, cancellationToken))?.Assets ?? throw Unavailable("The Workshop's page lists no mods.");
        return ([.. (assets.Rows ?? []).Select(Mod).OfType<ReforgerMod>()], Math.Max(0, assets.Count ?? 0));
    }

    public async Task<ReforgerMod?> ModAsync(string id, string requester, CancellationToken cancellationToken)
    {
        if (!IdPattern().IsMatch(id))
        {
            throw new ArgumentException("A Workshop id is 16 hexadecimal digits in capitals.", nameof(id));
        }

        var now = DateTimeOffset.UtcNow;
        if (s_mods.TryGetValue(id, out var remembered) && now - remembered.At < s_modLifetime)
        {
            return remembered.Mod;
        }

        var props = await PagePropsAsync($"workshop/{id}", requester, cancellationToken, notFoundIsNull: true);
        // The page of another id would be another mod: only the one asked for counts.
        var mod = props?.Asset is { } asset && Mod(asset) is { } found && found.Id == id ? found : null;
        if (s_mods.Count >= MaxRememberedMods)
        {
            s_mods.Clear();
        }

        s_mods[id] = (now, mod);
        return mod;
    }

    private static ReforgerMod? Mod(Asset? asset)
    {
        if (asset is not { Id: { } id, Name: { } name } || !IdPattern().IsMatch(id) || asset.Blocked == true || Text(name) is not { } title)
        {
            return null;
        }

        var version = asset.CurrentVersionNumber is { } number && VersionPattern().IsMatch(number) ? number : null;
        return new ReforgerMod(id, title, Text(asset.Author?.Username), version, asset.CurrentVersionSize is >= 0 ? asset.CurrentVersionSize : null,
            asset.UpdatedAt);
    }

    /// <summary>The data a Workshop page carries; null when <paramref name="notFoundIsNull"/> and the Workshop has no such page.</summary>
    private async Task<PageProps?> PagePropsAsync(string path, string requester, CancellationToken cancellationToken, bool notFoundIsNull = false)
    {
        using var mine = s_userBudget.AttemptAcquire(requester);
        if (!mine.IsAcquired)
        {
            throw new ModCatalogUnavailableException("You read the Workshop's pages too often in the last minute.", busy: true);
        }

        using var permit = s_budget.AttemptAcquire();
        if (!permit.IsAcquired)
        {
            throw new ModCatalogUnavailableException("This panel read the Workshop's pages too often in the last minute.", busy: true);
        }

        var http = clients.CreateClient(ClientName);
        // The client's timeout ends at the headers when the body is read separately: this deadline covers both.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(http.Timeout);
        try
        {
            using var response = await http.GetAsync(new Uri(ReforgerWorkshopLists.Site, path), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new ModCatalogUnavailableException("The Workshop asks this panel to wait.", busy: true);
            }

            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"The Workshop answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxPageBytes)
            {
                throw Unavailable("The Workshop's page is larger than Pulsar reads.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxPageBytes)
                {
                    throw Unavailable("The Workshop's page is larger than Pulsar reads.");
                }

                buffer.Write(chunk, 0, read);
            }

            // Next.js writes the page's data as JSON in one script, with '<' escaped: the first "</script>" after it ends it.
            var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var start = NextDataPattern().Match(html);
            var end = start.Success ? html.IndexOf("</script>", start.Index + start.Length, StringComparison.OrdinalIgnoreCase) : -1;
            if (end < 0)
            {
                throw Unavailable("The Workshop's page carries no data Pulsar reads.");
            }

            return JsonSerializer.Deserialize<NextData>(html.AsSpan(start.Index + start.Length, end - start.Index - start.Length), s_json)?.Props?.PageProps;
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable($"The Workshop could not be reached ({ex.HttpRequestError}).", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("The Workshop did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw Unavailable("The Workshop's page carries data Pulsar does not understand.", ex);
        }
    }

    /// <summary>Authors write these: no control or direction characters, and a length the page can show.</summary>
    private static string? Text(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = new string([.. text.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim();
        return cleaned.Length == 0 ? null : TextLimits.Truncate(cleaned, MaxTextLength);
    }

    private static ModCatalogUnavailableException Unavailable(string message, Exception? inner = null) => new(message, busy: false, inner);

    [GeneratedRegex(@"^[0-9A-F]{16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("""<script id="__NEXT_DATA__"[^>]{0,200}>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NextDataPattern();

    private sealed record NextData(NextProps? Props);

    private sealed record NextProps(PageProps? PageProps);

    private sealed record PageProps(AssetList? Assets, Asset? Asset);

    private sealed record AssetList(int? Count, List<Asset?>? Rows);

    private sealed record Asset(string? Id, string? Name, AssetAuthor? Author, string? CurrentVersionNumber, long? CurrentVersionSize, DateTimeOffset? UpdatedAt,
        bool? Blocked);

    private sealed record AssetAuthor(string? Username);
}
