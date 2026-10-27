using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;

namespace Pulsar.Infrastructure.Mods;

/// <summary>
/// uMod's plugin directory (Pulsar 1.0.14; no key): only <c>umod.org</c>, over HTTPS, without redirects, with a cap on
/// what is read and a User-Agent naming Pulsar. A plugin is one C# file for Oxide and Carbon; uMod serves only its newest
/// release, with that file's SHA-1, and lists no dependencies. Project ids are uMod's slugs (<c>rust-kits</c>), version
/// ids its version numbers (<c>4.4.10</c>).
/// </summary>
public sealed partial class UmodClient(IHttpClientFactory clients, UmodBudget budget) : IModCatalog
{
    public const string ClientName = "Pulsar.Umod";
    public static readonly Uri BaseAddress = new("https://umod.org/");

    public const string SourceName = "umod";

    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxTextLength = 256;

    /// <summary>uMod's search pages hold ten plugins.</summary>
    private const int PageSize = 10;

    /// <summary>An update check asks for each plugin on its own: no more than this many at once.</summary>
    private const int MaxConcurrentLookups = 4;

    /// <summary>
    /// An update check asks uMod about this many plugins at most, those read longest ago first; the others it takes as
    /// read in the last minutes: a server with hundreds of plugins cannot spend the panel's budget with uMod, and the
    /// next checks cover them.
    /// </summary>
    private const int MaxUpdateLookups = 15;

    private const int MaxRememberedPages = 2000;

    private static readonly TimeSpan s_pageLifetime = TimeSpan.FromMinutes(30);

    // The plugin pages read, for update checks past their cap (installs always ask again: a release may have changed).
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, PluginResponse? Plugin)> s_pages = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public string Source => SourceName;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>The game's plugins (<paramref name="community"/>: uMod's game, <c>rust</c>), the most downloaded first without a query.</summary>
    public async Task<CatalogSearch> SearchAsync(string? query, string projectType, IReadOnlyList<string> loaders, string? gameVersion, int offset, int limit,
        CancellationToken cancellationToken, string? community = null, string? category = null)
    {
        if (community is null || !CommunityPattern().IsMatch(community))
        {
            throw Unavailable("uMod is asked for a game's plugins.");
        }

        // Skipped and taken by position in uMod's pages, then checked: a plugin left out does not shift the next page.
        var found = new List<PluginResponse?>();
        var total = 0;
        var first = offset / PageSize;
        for (var page = first; page <= (offset + Math.Max(1, limit) - 1) / PageSize; page++)
        {
            var path = $"plugins/search.json?query={Uri.EscapeDataString(query ?? "")}&page={(page + 1).ToString(CultureInfo.InvariantCulture)}"
                + (string.IsNullOrEmpty(query) ? "&sort=downloads&sortdir=desc" : "") + $"&categories%5B%5D={community}";
            var answer = await GetAsync<SearchResponse>(path, cancellationToken) ?? throw Unavailable("uMod sent an empty answer.");
            total = Math.Max(0, answer.Total ?? 0);
            found.AddRange(answer.Data ?? []);
            if (answer.Data is not { Count: > 0 } || page + 1 >= (answer.LastPage ?? 0))
            {
                break;
            }
        }

        return new CatalogSearch([.. found.Skip(offset % PageSize).Take(limit).Select(p => p is null ? null : Hit(p)).OfType<CatalogHit>()], total);
    }

    /// <summary>Its one release uMod serves: the newest.</summary>
    public async Task<IReadOnlyList<CatalogVersion>> VersionsAsync(string projectId, IReadOnlyList<string> loaders, string? gameVersion,
        CancellationToken cancellationToken) =>
        await PluginAsync(projectId, cancellationToken) is { } plugin && Version(plugin) is { } version ? [version] : [];

    /// <summary>uMod knows a release only by its plugin: <see cref="VersionsAsync"/> gives it.</summary>
    public Task<CatalogVersion?> VersionAsync(string versionId, CancellationToken cancellationToken) => Task.FromResult<CatalogVersion?>(null);

    public async Task<IReadOnlyList<CatalogProject>> ProjectsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var projects = new List<CatalogProject>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(ServerMods.MaxProjectsPerInstall))
        {
            if (await PluginAsync(id, cancellationToken) is { } plugin && Hit(plugin) is { } hit)
            {
                projects.Add(new CatalogProject(hit.ProjectId, hit.Slug, hit.Title));
            }
        }

        return projects;
    }

    /// <summary>Each plugin's newest release, asked for one by one (uMod has no lookup of several).</summary>
    public async Task<IReadOnlyDictionary<string, CatalogVersion>> LatestVersionsAsync(IReadOnlyList<CatalogInstalled> installed, IReadOnlyList<string> loaders,
        string? gameVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var newest = new Dictionary<string, CatalogVersion>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var ids = installed.Select(i => i.ProjectId).Distinct(StringComparer.Ordinal).ToList();
        // Asked again: those never read or read longest ago, up to the cap; the others as read in the last minutes, if they were.
        var ordered = ids.OrderBy(id => s_pages.TryGetValue(id, out var page) ? page.At : DateTimeOffset.MinValue).ToList();
        var remembered = ordered.Skip(MaxUpdateLookups).Where(id => s_pages.TryGetValue(id, out var page) && now - page.At < s_pageLifetime).ToList();
        using var gate = new SemaphoreSlim(MaxConcurrentLookups);
        var lookups = ordered.Take(MaxUpdateLookups).Select(async id =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return (Id: id, Plugin: await PluginAsync(id, cancellationToken));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var pages = (await Task.WhenAll(lookups)).ToList();
        pages.AddRange(remembered.Select(id => (Id: id, s_pages.TryGetValue(id, out var page) ? page.Plugin : null)));
        foreach (var (id, plugin) in pages)
        {
            if (plugin is not null && Version(plugin) is { } version)
            {
                newest[id] = version;
            }
        }

        return newest;
    }

    /// <summary>A plugin's page as uMod has it now, remembered for update checks.</summary>
    private async Task<PluginResponse?> PluginAsync(string slug, CancellationToken cancellationToken)
    {
        if (!SlugPattern().IsMatch(slug))
        {
            return null;
        }

        var plugin = await GetAsync<PluginResponse>($"plugins/{slug}.json", cancellationToken, notFoundIsNull: true);
        if (s_pages.Count >= MaxRememberedPages)
        {
            s_pages.Clear();
        }

        s_pages[slug] = (DateTimeOffset.UtcNow, plugin);
        return plugin;
    }

    private static CatalogHit? Hit(PluginResponse plugin) =>
        plugin.Slug is { } slug && SlugPattern().IsMatch(slug) && Text(plugin.Title) is { } title
            ? new CatalogHit(slug, slug, title, Text(plugin.Description), Text(plugin.Author), Math.Max(0, plugin.Downloads ?? 0), plugin.UpdatedAtAtom)
            : null;

    /// <summary>
    /// Its newest release as the panel uses it: only a plugin uMod gives out (some it links elsewhere), its file on uMod's
    /// own site named as the plugin (<c>Kits.cs</c>), with a SHA-1; null otherwise.
    /// </summary>
    private static CatalogVersion? Version(PluginResponse plugin)
    {
        if (Hit(plugin) is not { } hit || plugin.Distribution != "download" || plugin.Name is not { } name || !NamePattern().IsMatch(name)
            || plugin.LatestReleaseVersion is not { } number || !VersionPattern().IsMatch(number)
            || plugin.LatestReleaseVersionChecksum is not { Length: 40 } checksum || !checksum.All(char.IsAsciiHexDigit)
            || !Uri.TryCreate(plugin.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.Host != BaseAddress.Host || url.AbsolutePath != $"/plugins/{name}.cs")
        {
            return null;
        }

        var file = new CatalogFile(url.AbsoluteUri, $"{name}.cs", checksum.ToLowerInvariant(), 0, Primary: true);
        return new CatalogVersion(number, hit.ProjectId, $"{hit.Title} {number}", number, "release", [], [], plugin.LatestReleaseAtAtom ?? plugin.UpdatedAtAtom
            ?? DateTimeOffset.MinValue, [file], []);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken, bool notFoundIsNull = false)
    {
        using var permit = budget.Limiter.AttemptAcquire();
        if (!permit.IsAcquired)
        {
            throw new ModCatalogUnavailableException("This panel asked uMod too often in the last minute.", busy: true);
        }

        var http = clients.CreateClient(ClientName);
        // The client's timeout ends at the headers when the body is read separately: this deadline covers both.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(http.Timeout);
        try
        {
            using var response = await http.GetAsync(new Uri(BaseAddress, path), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new ModCatalogUnavailableException("uMod asks this panel to wait.", busy: true);
            }

            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"uMod answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw Unavailable("uMod's answer is larger than Pulsar reads.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw Unavailable("uMod's answer is larger than Pulsar reads.");
                }

                buffer.Write(chunk, 0, read);
            }

            return JsonSerializer.Deserialize<T>(buffer.ToArray(), s_json);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable($"uMod could not be reached ({ex.HttpRequestError}).", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("uMod did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw Unavailable("uMod sent an answer Pulsar does not understand.", ex);
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

    /// <summary>uMod's page of a plugin: <c>rust-kits</c>.</summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    /// <summary>A plugin's name, which is its class and its file's: <c>Kits</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9]{1,6}(\.[0-9]{1,6}){0,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommunityPattern();

    private sealed record SearchResponse(List<PluginResponse?>? Data, int? Total, [property: JsonPropertyName("last_page")] int? LastPage);

    private sealed record PluginResponse(
        string? Name,
        string? Title,
        string? Slug,
        string? Description,
        string? Author,
        long? Downloads,
        string? Distribution,
        [property: JsonPropertyName("download_url")] string? DownloadUrl,
        [property: JsonPropertyName("latest_release_version")] string? LatestReleaseVersion,
        [property: JsonPropertyName("latest_release_version_checksum")] string? LatestReleaseVersionChecksum,
        [property: JsonPropertyName("latest_release_at_atom")] DateTimeOffset? LatestReleaseAtAtom,
        [property: JsonPropertyName("updated_at_atom")] DateTimeOffset? UpdatedAtAtom);
}

/// <summary>
/// How often the whole panel asks uMod, one for the panel (a singleton). uMod answers an address 30 times a minute
/// (X-RateLimit-Limit), and a node on the panel's machine downloads from the same address: 20 a minute leaves the nodes room.
/// </summary>
public sealed class UmodBudget(int requestsPerMinute = UmodBudget.RequestsPerMinute) : IDisposable
{
    public const int RequestsPerMinute = 20;

    public FixedWindowRateLimiter Limiter { get; } = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = requestsPerMinute,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    public void Dispose() => Limiter.Dispose();
}
