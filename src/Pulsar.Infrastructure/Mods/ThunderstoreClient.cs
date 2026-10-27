using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Infrastructure.Mods;

/// <summary>
/// Thunderstore's packages (Pulsar 1.0.14; no key): only <c>thunderstore.io</c>, over HTTPS, without redirects, with a
/// cap on what is read and a User-Agent naming Pulsar. A package is a zip for BepInEx; Thunderstore's API gives a
/// package's newest version and any version by its number, with the packages it depends on, and publishes no checksum
/// (the node takes the zip from Thunderstore's own hosts only). Project ids are <c>namespace-name</c>
/// (<c>ValheimModding-Jotunn</c>), version ids <c>namespace-name-version</c>, as Thunderstore names them.
/// </summary>
public sealed partial class ThunderstoreClient(IHttpClientFactory clients) : IModCatalog
{
    public const string ClientName = "Pulsar.Thunderstore";
    public static readonly Uri BaseAddress = new("https://thunderstore.io/");

    public const string SourceName = "thunderstore";

    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxTextLength = 256;

    /// <summary>Thunderstore's listing pages hold twenty packages.</summary>
    private const int PageSize = 20;

    private const int MaxConcurrentLookups = 4;

    /// <summary>An update check asks about this many packages at most, those read longest ago first (the others as read lately).</summary>
    private const int MaxUpdateLookups = 15;

    private const int MaxRememberedPages = 2000;

    private static readonly TimeSpan s_pageLifetime = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    // The package pages read, for update checks past their cap (installs always ask again).
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, PackageResponse? Package)> s_pages = new(StringComparer.Ordinal);

    // Each community's categories by slug, which the listing takes by id; they hardly change.
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> s_categories = new(StringComparer.Ordinal);

    // Thunderstore publishes no limit: the whole panel asks it at most this often.
    private static readonly FixedWindowRateLimiter s_budget = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 60,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    public string Source => SourceName;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    /// The community's packages (<paramref name="community"/>: <c>valheim</c>), in <paramref name="category"/> (a slug:
    /// <c>server-side</c>) when given, the most downloaded first.
    /// </summary>
    public async Task<CatalogSearch> SearchAsync(string? query, string projectType, IReadOnlyList<string> loaders, string? gameVersion, int offset, int limit,
        CancellationToken cancellationToken, string? community = null, string? category = null)
    {
        if (community is null || !SlugPattern().IsMatch(community) || (category is not null && !SlugPattern().IsMatch(category)))
        {
            throw Unavailable("Thunderstore is asked for a community's packages.");
        }

        var categoryId = category is null ? null : await CategoryAsync(community, category, cancellationToken);
        var found = new List<ListingResult?>();
        var total = 0;
        var first = offset / PageSize;
        for (var page = first; page <= (offset + Math.Max(1, limit) - 1) / PageSize; page++)
        {
            var path = $"api/cyberstorm/listing/{community}/?q={Uri.EscapeDataString(query ?? "")}&page={(page + 1).ToString(CultureInfo.InvariantCulture)}"
                + "&ordering=most-downloaded" + (categoryId is null ? "" : $"&included_categories={categoryId}");
            var answer = await GetAsync<ListingResponse>(path, cancellationToken) ?? throw Unavailable("Thunderstore sent an empty answer.");
            total = Math.Max(0, answer.Count ?? 0);
            found.AddRange(answer.Results ?? []);
            if (answer.Results is not { Count: > 0 } || answer.Next is null)
            {
                break;
            }
        }

        return new CatalogSearch([.. found.Skip(offset % PageSize).Take(limit).Select(Hit).OfType<CatalogHit>()], total);
    }

    /// <summary>The package's newest version (Thunderstore lists no others; any of them is taken by its id).</summary>
    public async Task<IReadOnlyList<CatalogVersion>> VersionsAsync(string projectId, IReadOnlyList<string> loaders, string? gameVersion,
        CancellationToken cancellationToken) =>
        await PackageAsync(projectId, cancellationToken) is { Latest: { } latest } && Version(latest) is { } version ? [version] : [];

    /// <summary>A version by its id (<c>namespace-name-version</c>).</summary>
    public async Task<CatalogVersion?> VersionAsync(string versionId, CancellationToken cancellationToken)
    {
        if (VersionIdPattern().Match(versionId) is not { Success: true } match)
        {
            return null;
        }

        var version = await GetAsync<VersionResponse>(
            $"api/experimental/package/{match.Groups["ns"].Value}/{match.Groups["name"].Value}/{match.Groups["version"].Value}/", cancellationToken,
            notFoundIsNull: true);
        return version is null ? null : Version(version);
    }

    public async Task<IReadOnlyList<CatalogProject>> ProjectsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var projects = new List<CatalogProject>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(ServerMods.MaxProjectsPerInstall))
        {
            if (await PackageAsync(id, cancellationToken) is { Namespace: { } ns, Name: { } name } && ProjectIdPattern().IsMatch($"{ns}-{name}"))
            {
                projects.Add(new CatalogProject($"{ns}-{name}", $"{ns}-{name}", Title(name)));
            }
        }

        return projects;
    }

    /// <summary>Each package's newest version, asked for one by one (Thunderstore has no lookup of several).</summary>
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
                return (Id: id, Package: await PackageAsync(id, cancellationToken));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var pages = (await Task.WhenAll(lookups)).ToList();
        pages.AddRange(remembered.Select(id => (Id: id, s_pages.TryGetValue(id, out var page) ? page.Package : null)));
        foreach (var (id, package) in pages)
        {
            if (package?.Latest is { } latest && Version(latest) is { } version)
            {
                newest[id] = version;
            }
        }

        return newest;
    }

    /// <summary>A package's page as Thunderstore has it now, remembered for update checks.</summary>
    private async Task<PackageResponse?> PackageAsync(string projectId, CancellationToken cancellationToken)
    {
        if (ProjectIdPattern().Match(projectId) is not { Success: true } match)
        {
            return null;
        }

        var package = await GetAsync<PackageResponse>($"api/experimental/package/{match.Groups["ns"].Value}/{match.Groups["name"].Value}/", cancellationToken,
            notFoundIsNull: true);
        if (s_pages.Count >= MaxRememberedPages)
        {
            s_pages.Clear();
        }

        s_pages[projectId] = (DateTimeOffset.UtcNow, package);
        return package;
    }

    /// <summary>
    /// A category's id in a community's listing, by its slug. One the community does not have fails the search rather than
    /// searching without it (a game's client-side mods among its server's); only a lookup that found categories is kept.
    /// </summary>
    private async Task<string> CategoryAsync(string community, string slug, CancellationToken cancellationToken)
    {
        if (!s_categories.TryGetValue(community, out var categories))
        {
            var filters = await GetAsync<FiltersResponse>($"api/cyberstorm/community/{community}/filters/", cancellationToken, notFoundIsNull: true);
            categories = (filters?.PackageCategories ?? []).Where(c => c is { Id: not null, Slug: not null } && CategoryIdPattern().IsMatch(c.Id!)
                    && SlugPattern().IsMatch(c.Slug!))
                .GroupBy(c => c.Slug!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id!, StringComparer.Ordinal);
            if (categories.Count > 0)
            {
                s_categories[community] = categories;
            }
        }

        return categories.GetValueOrDefault(slug) ?? throw Unavailable($"Thunderstore's {community} community has no '{slug}' category.");
    }

    private static CatalogHit? Hit(ListingResult? result) =>
        result is { Namespace: { } ns, Name: { } name, IsDeprecated: not true, IsNsfw: not true } && ProjectIdPattern().IsMatch($"{ns}-{name}")
            && $"{ns}-{name}".Length <= ServerMod.MaxIdLength
            ? new CatalogHit($"{ns}-{name}", $"{ns}-{name}", Title(name), Text(result.Description), Text(ns), Math.Max(0, result.DownloadCount ?? 0),
                result.LastUpdated)
            : null;

    /// <summary>
    /// A version as the panel uses it: its zip at Thunderstore's own download address for that version, which names no
    /// checksum (an empty one); its dependencies by Thunderstore's names. Null when anything is not as Thunderstore says.
    /// </summary>
    private static CatalogVersion? Version(VersionResponse version)
    {
        if (version is not { Namespace: { } ns, Name: { } name, VersionNumber: { } number } || !ProjectIdPattern().IsMatch($"{ns}-{name}")
            || !NumberPattern().IsMatch(number) || !Uri.TryCreate(version.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.Host != BaseAddress.Host || url.AbsolutePath != $"/package/download/{ns}/{name}/{number}/"
            || $"{ns}-{name}-{number}".Length > ServerMod.MaxIdLength)
        {
            return null;
        }

        var dependencies = (version.Dependencies ?? []).Select(d => d is null ? null : VersionIdPattern().Match(d)).Where(m => m is { Success: true })
            .Select(m => new CatalogDependency($"{m!.Groups["ns"].Value}-{m.Groups["name"].Value}", m.Value, "required")).ToList();
        var file = new CatalogFile(url.AbsoluteUri, $"{ns}-{name}-{number}.zip", "", 0, Primary: true);
        return new CatalogVersion($"{ns}-{name}-{number}", $"{ns}-{name}", $"{Title(name)} {number}", number, "release", [], [],
            version.DateCreated ?? DateTimeOffset.MinValue, [file], dependencies);
    }

    /// <summary>Thunderstore's names write spaces as underscores.</summary>
    private static string Title(string name) => Text(name.Replace('_', ' ')) ?? name;

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken, bool notFoundIsNull = false)
    {
        using var permit = s_budget.AttemptAcquire();
        if (!permit.IsAcquired)
        {
            throw new ModCatalogUnavailableException("This panel asked Thunderstore too often in the last minute.", busy: true);
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
                throw new ModCatalogUnavailableException("Thunderstore asks this panel to wait.", busy: true);
            }

            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"Thunderstore answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw Unavailable("Thunderstore's answer is larger than Pulsar reads.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw Unavailable("Thunderstore's answer is larger than Pulsar reads.");
                }

                buffer.Write(chunk, 0, read);
            }

            return JsonSerializer.Deserialize<T>(buffer.ToArray(), s_json);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable($"Thunderstore could not be reached ({ex.HttpRequestError}).", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("Thunderstore did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw Unavailable("Thunderstore sent an answer Pulsar does not understand.", ex);
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

    /// <summary>A package: <c>ValheimModding-Jotunn</c> (a namespace and a name have no '-').</summary>
    [GeneratedRegex(@"^(?<ns>[A-Za-z0-9_]{1,64})-(?<name>[A-Za-z0-9_]{1,64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdPattern();

    /// <summary>A package's version: <c>ValheimModding-Jotunn-2.30.2</c>.</summary>
    [GeneratedRegex(@"^(?<ns>[A-Za-z0-9_]{1,64})-(?<name>[A-Za-z0-9_]{1,64})-(?<version>[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9})\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionIdPattern();

    [GeneratedRegex(@"^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    /// <summary>A community or a category: <c>valheim</c>, <c>server-side</c>.</summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^[0-9]{1,10}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryIdPattern();

    private sealed record ListingResponse(int? Count, string? Next, List<ListingResult?>? Results);

    private sealed record ListingResult(
        string? Namespace,
        string? Name,
        string? Description,
        [property: JsonPropertyName("download_count")] long? DownloadCount,
        [property: JsonPropertyName("last_updated")] DateTimeOffset? LastUpdated,
        [property: JsonPropertyName("is_deprecated")] bool? IsDeprecated,
        [property: JsonPropertyName("is_nsfw")] bool? IsNsfw);

    private sealed record PackageResponse(string? Namespace, string? Name, VersionResponse? Latest);

    private sealed record VersionResponse(
        string? Namespace,
        string? Name,
        [property: JsonPropertyName("version_number")] string? VersionNumber,
        [property: JsonPropertyName("download_url")] string? DownloadUrl,
        List<string?>? Dependencies,
        [property: JsonPropertyName("date_created")] DateTimeOffset? DateCreated);

    private sealed record FiltersResponse([property: JsonPropertyName("package_categories")] List<CategoryResponse>? PackageCategories);

    private sealed record CategoryResponse(string? Id, string? Slug);
}
