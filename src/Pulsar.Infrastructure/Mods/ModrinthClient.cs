using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;

namespace Pulsar.Infrastructure.Mods;

/// <summary>
/// Modrinth's public API (v2, no key): only <c>api.modrinth.com</c>, over HTTPS, without redirects, with a cap on what is
/// read and a User-Agent naming Pulsar, as Modrinth asks of applications. It allows 300 requests a minute per address;
/// past that it answers 429, which is reported as busy.
/// </summary>
public sealed class ModrinthClient(IHttpClientFactory clients) : IModrinth
{
    public const string ClientName = "Pulsar.Modrinth";
    public static readonly Uri BaseAddress = new("https://api.modrinth.com/v2/");

    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxTextLength = 256;

    // Only Modrinth's CDN serves files the agent will take.
    private const string FileHost = "cdn.modrinth.com";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async Task<ModrinthSearch> SearchAsync(string? query, string projectType, IReadOnlyList<string> loaders, string? gameVersion, int offset, int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaders);
        // Outer list: all of them; inner: any of them.
        var facets = new List<List<string>>
        {
            new() { $"project_type:{projectType}" },
            loaders.Select(l => $"categories:{l}").ToList(),
        };
        if (gameVersion is not null)
        {
            facets.Add([$"versions:{gameVersion}"]);
        }

        if (projectType is "mod" or "modpack")
        {
            facets.Add(["server_side:required", "server_side:optional"]); // those only for players' games are no use on a server
        }

        var path = $"search?query={Uri.EscapeDataString(query ?? "")}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}"
            + $"&index={(string.IsNullOrEmpty(query) ? "downloads" : "relevance")}&offset={offset.ToString(CultureInfo.InvariantCulture)}"
            + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        var found = await GetAsync<SearchResponse>(path, cancellationToken) ?? throw Unavailable("Modrinth sent an empty answer.");
        return new ModrinthSearch([.. (found.Hits ?? []).Where(h => h.ProjectId is not null && h.Title is not null).Select(h => new ModrinthHit(
            Id(h.ProjectId!), Text(h.Slug) ?? Id(h.ProjectId!), Text(h.Title)!, Text(h.Description), Text(h.Author), Math.Max(0, h.Downloads), h.DateModified))],
            Math.Max(0, found.TotalHits));
    }

    public async Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string projectId, IReadOnlyList<string> loaders, string? gameVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaders);
        var path = $"project/{Uri.EscapeDataString(projectId)}/version?loaders={Uri.EscapeDataString(JsonSerializer.Serialize(loaders))}"
            + (gameVersion is null ? "" : $"&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        var versions = await GetAsync<List<VersionResponse>>(path, cancellationToken, notFoundIsNull: true);
        return [.. (versions ?? []).Select(Version).OfType<ModrinthVersion>()];
    }

    public async Task<ModrinthVersion?> VersionAsync(string versionId, CancellationToken cancellationToken) =>
        Version(await GetAsync<VersionResponse>($"version/{Uri.EscapeDataString(versionId)}", cancellationToken, notFoundIsNull: true));

    public async Task<IReadOnlyList<ModrinthProject>> ProjectsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var projects = await GetAsync<List<ProjectResponse>>($"projects?ids={Uri.EscapeDataString(JsonSerializer.Serialize(ids))}", cancellationToken);
        return [.. (projects ?? []).Where(p => p.Id is not null && p.Title is not null)
            .Select(p => new ModrinthProject(Id(p.Id!), Text(p.Slug) ?? Id(p.Id!), Text(p.Title)!))];
    }

    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> LatestVersionsAsync(IReadOnlyList<string> sha512s, IReadOnlyList<string> loaders,
        string gameVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sha512s);
        var body = JsonSerializer.Serialize(new { hashes = sha512s, algorithm = "sha512", loaders, game_versions = new[] { gameVersion } });
        var latest = await SendAsync<Dictionary<string, VersionResponse>>(HttpMethod.Post, "version_files/update", body, cancellationToken) ?? [];
        return latest.Where(p => sha512s.Contains(p.Key)).Select(p => (p.Key, Version: Version(p.Value)))
            .Where(p => p.Version is not null).ToDictionary(p => p.Key, p => p.Version!, StringComparer.Ordinal);
    }

    /// <summary>A version as the panel uses it: its files only on Modrinth's CDN, with a SHA-512; null when it cannot be read.</summary>
    private static ModrinthVersion? Version(VersionResponse? version)
    {
        if (version is not { Id: not null, ProjectId: not null })
        {
            return null;
        }

        var files = (version.Files ?? []).Where(f => f is { Url: not null, Filename: not null } && f.Hashes?.Sha512 is { Length: 128 }
                && Uri.TryCreate(f.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == FileHost)
            .Select(f => new ModrinthFile(f.Url!, f.Filename!, f.Hashes!.Sha512!.ToLowerInvariant(), Math.Max(0, f.Size), f.Primary)).ToList();
        return new ModrinthVersion(Id(version.Id), Id(version.ProjectId), Text(version.Name) ?? Text(version.VersionNumber) ?? Id(version.Id),
            Text(version.VersionNumber) ?? "?", Text(version.VersionType) ?? "release", [.. (version.Loaders ?? []).Select(l => Text(l)!).Where(l => l is not null)],
            [.. (version.GameVersions ?? []).Select(v => Text(v)!).Where(v => v is not null)], version.DatePublished ?? DateTimeOffset.MinValue, files,
            [.. (version.Dependencies ?? []).Select(d => new ModrinthDependency(d.ProjectId is null ? null : Id(d.ProjectId),
                d.VersionId is null ? null : Id(d.VersionId), Text(d.DependencyType) ?? "optional"))]);
    }

    private Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken, bool notFoundIsNull = false) =>
        SendAsync<T>(HttpMethod.Get, path, null, cancellationToken, notFoundIsNull);

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, string? body, CancellationToken cancellationToken, bool notFoundIsNull = false)
    {
        var http = clients.CreateClient(ClientName);
        // The client's timeout ends at the headers when the body is read separately: this deadline covers both.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(http.Timeout);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(BaseAddress, path));
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new ModCatalogUnavailableException("Modrinth asks this panel to wait.", busy: true);
            }

            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"Modrinth answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw Unavailable("Modrinth's answer is larger than Pulsar reads.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw Unavailable("Modrinth's answer is larger than Pulsar reads.");
                }

                buffer.Write(chunk, 0, read);
            }

            return JsonSerializer.Deserialize<T>(buffer.ToArray(), s_json);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable($"Modrinth could not be reached ({ex.HttpRequestError}).", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("Modrinth did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw Unavailable("Modrinth sent an answer Pulsar does not understand.", ex);
        }
    }

    /// <summary>An id as the panel keeps it: letters, digits, '-' and '_' only (anything else is not Modrinth's).</summary>
    private static string Id(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? id
            : throw Unavailable("Modrinth sent an id Pulsar does not take.");

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

    private sealed record SearchResponse(List<HitResponse>? Hits, [property: JsonPropertyName("total_hits")] int TotalHits);

    private sealed record HitResponse(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        string? Slug,
        string? Title,
        string? Description,
        string? Author,
        long Downloads,
        [property: JsonPropertyName("date_modified")] DateTimeOffset? DateModified);

    private sealed record ProjectResponse(string? Id, string? Slug, string? Title);

    private sealed record VersionResponse(
        string? Id,
        [property: JsonPropertyName("project_id")] string? ProjectId,
        string? Name,
        [property: JsonPropertyName("version_number")] string? VersionNumber,
        [property: JsonPropertyName("version_type")] string? VersionType,
        List<string>? Loaders,
        [property: JsonPropertyName("game_versions")] List<string>? GameVersions,
        [property: JsonPropertyName("date_published")] DateTimeOffset? DatePublished,
        List<FileResponse>? Files,
        List<DependencyResponse>? Dependencies);

    private sealed record FileResponse(string? Url, string? Filename, FileHashes? Hashes, long Size, bool Primary);

    private sealed record FileHashes(string? Sha1, string? Sha512);

    private sealed record DependencyResponse(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("version_id")] string? VersionId,
        [property: JsonPropertyName("dependency_type")] string? DependencyType);
}
