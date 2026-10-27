using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;
using Pulsar.Manifests;

namespace Pulsar.Infrastructure.Mods;

/// <summary>
/// CurseForge's Core API (Pulsar 1.0.9) with the administrator's own key (its terms forbid sharing one): only
/// <c>api.curseforge.com</c>, over HTTPS, without redirects, with a cap on what is read. Minecraft is game 432; mods are
/// class 6, Bukkit plugins 5, modpacks 4471. Files are kept only on CurseForge's CDN with the SHA-1 it lists (it
/// publishes no stronger checksum); one whose author keeps it on CurseForge's site only has no address and is said so.
/// Files tagged for players' games only are left out.
/// </summary>
/// <remarks>
/// Pulsar 1.0.14: any game's projects too (<see cref="ICurseForgeProjects"/>), by id or by a search of the game, for the
/// editor of a variable that lists them (ARK: Survival Ascended's mods).
/// </remarks>
public sealed class CurseForgeClient(IHttpClientFactory clients, IPulsarDbContext db, ICatalogKeyProtector protector)
    : IModCatalog, ICurseForgeKeyCheck, ICurseForgeProjects
{
    public const string ClientName = "Pulsar.CurseForge";
    public static readonly Uri BaseAddress = new("https://api.curseforge.com/v1/");

    private const int MinecraftGameId = 432;
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxTextLength = 256;
    private const int MaxPageSize = 50;

    // CurseForge's search reaches 10,000 results at most.
    private const int MaxSearchWindow = 10_000;

    private static readonly HashSet<string> s_fileHosts = new(StringComparer.OrdinalIgnoreCase) { "edge.forgecdn.net", "mediafilez.forgecdn.net" };

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    // The administrator's key is the whole panel's: users' lookups share it, whatever each one's own budget.
    private static readonly FixedWindowRateLimiter s_budget = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 120,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    private string? _key;

    public string Source => ModSourceDefinition.CurseForge;

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => await KeyAsync(cancellationToken) is not null;

    public async Task<bool> AcceptsAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        try
        {
            await SendAsync<JsonElement>(HttpMethod.Get, $"games/{MinecraftGameId}", null, key, cancellationToken);
            return true;
        }
        catch (ModCatalogUnavailableException ex) when (ex.NotConfigured)
        {
            return false;
        }
    }

    public async Task<CatalogSearch> SearchAsync(string? query, string projectType, IReadOnlyList<string> loaders, string? gameVersion, int offset, int limit,
        CancellationToken cancellationToken, string? community = null, string? category = null)
    {
        ArgumentNullException.ThrowIfNull(loaders);
        var size = Math.Clamp(limit, 1, MaxPageSize);
        var index = Math.Clamp(offset, 0, MaxSearchWindow - size);
        var path = new StringBuilder($"mods/search?gameId={MinecraftGameId}&classId={ClassId(projectType)}&sortField=2&sortOrder=desc");
        path.Append(CultureInfo.InvariantCulture, $"&index={index}&pageSize={size}");
        if (!string.IsNullOrEmpty(query))
        {
            path.Append("&searchFilter=").Append(Uri.EscapeDataString(query));
        }

        if (gameVersion is not null)
        {
            path.Append("&gameVersion=").Append(Uri.EscapeDataString(gameVersion));
        }

        if (LoaderType(loaders) is { } type)
        {
            path.Append(CultureInfo.InvariantCulture, $"&modLoaderType={type}");
        }

        var found = await GetAsync<Page<ModResponse>>(path.ToString(), cancellationToken) ?? throw Unavailable("CurseForge sent an empty answer.");
        var hits = (found.Data ?? []).Where(m => m is { Id: > 0, Name: not null }).Select(m => new CatalogHit(Id(m.Id), Text(m.Slug) ?? Id(m.Id),
            Text(m.Name)!, Text(m.Summary), Text(m.Authors?.FirstOrDefault()?.Name), Math.Max(0, m.DownloadCount), m.DateModified)).ToList();
        return new CatalogSearch(hits, Math.Clamp(found.Pagination?.TotalCount ?? hits.Count, 0, MaxSearchWindow));
    }

    public async Task<IReadOnlyList<CatalogVersion>> VersionsAsync(string projectId, IReadOnlyList<string> loaders, string? gameVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaders);
        if (!IsId(projectId))
        {
            return [];
        }

        var path = new StringBuilder($"mods/{projectId}/files?pageSize={MaxPageSize}");
        if (gameVersion is not null)
        {
            path.Append("&gameVersion=").Append(Uri.EscapeDataString(gameVersion));
        }

        if (LoaderType(loaders) is { } type)
        {
            path.Append(CultureInfo.InvariantCulture, $"&modLoaderType={type}");
        }

        var files = await GetAsync<Page<FileResponse>>(path.ToString(), cancellationToken, notFoundIsNull: true);
        return await VersionsOfAsync([.. (files?.Data ?? []).OrderByDescending(f => f.FileDate)], cancellationToken);
    }

    public async Task<CatalogVersion?> VersionAsync(string versionId, CancellationToken cancellationToken) =>
        IsId(versionId) ? FirstOf(await VersionsOfAsync(await FilesAsync([versionId], cancellationToken), cancellationToken)) : null;

    public async Task<IReadOnlyList<CatalogProject>> ProjectsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return [.. (await ModsAsync(ids, cancellationToken)).Select(m => new CatalogProject(Id(m.Id), Text(m.Slug) ?? Id(m.Id), Text(m.Name)!))];
    }

    /// <summary>From each project's index of its latest files: the newest for the game version and loader (a release first).</summary>
    public async Task<IReadOnlyDictionary<string, CatalogVersion>> LatestVersionsAsync(IReadOnlyList<CatalogInstalled> installed, IReadOnlyList<string> loaders,
        string? gameVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(loaders);
        var type = LoaderType(loaders);
        var newest = new Dictionary<int, int>();
        foreach (var mod in await ModsAsync([.. installed.Select(i => i.ProjectId)], cancellationToken))
        {
            var suitable = (mod.LatestFilesIndexes ?? [])
                .Where(f => f is not null && f.FileId > 0 && (gameVersion is null || f.GameVersion == gameVersion) && (type is null || f.ModLoader == type)).ToList();
            if ((suitable.FirstOrDefault(f => f!.ReleaseType == 1) ?? suitable.FirstOrDefault()) is { } file)
            {
                newest[mod.Id] = file.FileId;
            }
        }

        var versions = await VersionsOfAsync(await FilesAsync([.. newest.Values.Select(Id)], cancellationToken), cancellationToken);
        return versions.GroupBy(v => v.ProjectId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    /// <summary>The files as versions, with each modpack file's server pack looked up (one more request for all of them).</summary>
    private async Task<IReadOnlyList<CatalogVersion>> VersionsOfAsync(IReadOnlyList<FileResponse> files, CancellationToken cancellationToken)
    {
        // A pack's server files are the pack's companion, never a version of their own.
        var usable = files.Where(f => f is { Id: > 0, ModId: > 0 } && f.IsServerPack != true && !IsForPlayersOnly(f)).ToList();
        var serverPackIds = usable.Where(f => f.ServerPackFileId is > 0).Select(f => Id(f.ServerPackFileId!.Value)).Distinct(StringComparer.Ordinal).ToList();
        var serverPacks = serverPackIds.Count == 0
            ? new Dictionary<int, FileResponse>()
            : (await FilesAsync(serverPackIds, cancellationToken)).Where(f => f.IsServerPack == true).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        return [.. usable.Select(f => Version(f, f.ServerPackFileId is { } id && serverPacks.TryGetValue(id, out var pack) ? File(pack) : null))];
    }

    private static CatalogVersion Version(FileResponse file, CatalogFile? serverPack)
    {
        var tags = (file.GameVersions ?? []).Select(v => Text(v)).OfType<string>().ToList();
        var loaders = tags.Select(t => t.ToLowerInvariant()).Where(t => t is "forge" or "neoforge" or "fabric" or "quilt").Distinct(StringComparer.Ordinal).ToList();
        var gameVersions = tags.Where(t => t.Length > 0 && char.IsAsciiDigit(t[0])).ToList();
        var name = Text(file.DisplayName) ?? Text(file.FileName) ?? Id(file.Id);
        var downloadable = File(file);
        return new CatalogVersion(Id(file.Id), Id(file.ModId), name, TextLimits.Truncate(name, 64), file.ReleaseType switch { 2 => "beta", 3 => "alpha", _ => "release" },
            loaders, gameVersions, file.FileDate ?? DateTimeOffset.MinValue, downloadable is null ? [] : [downloadable],
            [.. (file.Dependencies ?? []).Where(d => d is { ModId: > 0 }).Select(d => new CatalogDependency(Id(d!.ModId), null, d.RelationType switch
            {
                3 => "required",
                5 => "incompatible",
                1 or 6 => "embedded",
                _ => "optional",
            }))],
            DistributionForbidden: file.DownloadUrl is null, serverPack);
    }

    /// <summary>A file the panel can have a node download: on CurseForge's CDN, over HTTPS, with its SHA-1.</summary>
    private static CatalogFile? File(FileResponse file) =>
        file is { DownloadUrl: { } url, FileName: { } name } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && s_fileHosts.Contains(uri.Host) && (file.Hashes ?? []).FirstOrDefault(h => h?.Algo == 1)?.Value is { Length: 40 } sha1
            && sha1.All(char.IsAsciiHexDigit)
            ? new CatalogFile(url, name, sha1.ToLowerInvariant(), Math.Max(0, file.FileLength), Primary: true)
            : null;

    /// <summary>CurseForge tags a file Server, Client or both; one tagged for players' games only is no use on a server.</summary>
    private static bool IsForPlayersOnly(FileResponse file) =>
        file.GameVersions is { } tags && tags.Contains("Client", StringComparer.Ordinal) && !tags.Contains("Server", StringComparer.Ordinal);

    private async Task<List<FileResponse>> FilesAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var numbers = ids.Where(IsId).Select(i => int.Parse(i, CultureInfo.InvariantCulture)).Distinct().Take(500).ToList();
        if (numbers.Count == 0)
        {
            return [];
        }

        var found = await SendAsync<Page<FileResponse>>(HttpMethod.Post, "mods/files", JsonSerializer.Serialize(new { fileIds = numbers }), null,
            cancellationToken);
        return [.. (found?.Data ?? []).Where(f => f is not null)];
    }

    public async Task<(IReadOnlyList<CurseForgeProject> Projects, int Total)> SearchGameAsync(int gameId, string? query, int offset, int limit,
        CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit, 1, MaxPageSize);
        var index = Math.Clamp(offset, 0, MaxSearchWindow - size);
        var path = new StringBuilder().Append(CultureInfo.InvariantCulture, $"mods/search?gameId={Math.Max(1, gameId)}&sortField=2&sortOrder=desc&index={index}&pageSize={size}");
        if (!string.IsNullOrEmpty(query))
        {
            path.Append("&searchFilter=").Append(Uri.EscapeDataString(query));
        }

        var found = await GetAsync<Page<ModResponse>>(path.ToString(), cancellationToken) ?? throw Unavailable("CurseForge sent an empty answer.");
        var projects = (found.Data ?? []).Where(m => m is { Id: > 0, Name: not null }).Select(Project).ToList();
        return (projects, Math.Clamp(found.Pagination?.TotalCount ?? projects.Count, 0, MaxSearchWindow));
    }

    public async Task<IReadOnlyList<CurseForgeProject>> ProjectsByIdAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return [.. (await ModsAsync(ids, cancellationToken)).Select(Project)];
    }

    /// <summary>A project as the editor shows it: its page only on CurseForge's own site.</summary>
    private static CurseForgeProject Project(ModResponse mod) =>
        new(Id(mod.Id), Text(mod.Name)!, mod.GameId, Text(mod.Authors?.FirstOrDefault()?.Name), Math.Max(0, mod.DownloadCount), mod.DateModified,
            Uri.TryCreate(mod.Links?.WebsiteUrl, UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps && page.Host == "www.curseforge.com"
                ? page.AbsoluteUri
                : null);

    private async Task<List<ModResponse>> ModsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var numbers = ids.Where(IsId).Select(i => int.Parse(i, CultureInfo.InvariantCulture)).Distinct().Take(500).ToList();
        if (numbers.Count == 0)
        {
            return [];
        }

        var found = await SendAsync<Page<ModResponse>>(HttpMethod.Post, "mods", JsonSerializer.Serialize(new { modIds = numbers }), null, cancellationToken);
        return [.. (found?.Data ?? []).Where(m => m is { Id: > 0, Name: not null })];
    }

    private async Task<string?> KeyAsync(CancellationToken cancellationToken)
    {
        if (_key is not null)
        {
            return _key;
        }

        var stored = await db.ModCatalogSettings.AsNoTracking().Select(s => s.CurseForgeKey).SingleOrDefaultAsync(cancellationToken);
        try
        {
            return _key = stored is null ? null : protector.Unprotect(stored);
        }
        catch (CryptographicException)
        {
            return null; // encrypted with a key ring that is gone: as good as none
        }
    }

    private Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken, bool notFoundIsNull = false) =>
        SendAsync<T>(HttpMethod.Get, path, null, null, cancellationToken, notFoundIsNull);

    /// <param name="key">A key to try (the settings' check); null: the panel's.</param>
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, string? body, string? key, CancellationToken cancellationToken,
        bool notFoundIsNull = false)
    {
        var apiKey = key ?? await KeyAsync(cancellationToken)
            ?? throw new ModCatalogUnavailableException("No CurseForge API key is set.", notConfigured: true);
        using var permit = s_budget.AttemptAcquire();
        if (!permit.IsAcquired)
        {
            throw new ModCatalogUnavailableException("This panel asked CurseForge too often in the last minute.", busy: true);
        }

        var http = clients.CreateClient(ClientName);
        // The client's timeout ends at the headers when the body is read separately: this deadline covers both.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(http.Timeout);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(BaseAddress, path));
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ModCatalogUnavailableException("CurseForge refuses the panel's API key.", notConfigured: true);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new ModCatalogUnavailableException("CurseForge asks this panel to wait.", busy: true);
            }

            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"CurseForge answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw Unavailable("CurseForge's answer is larger than Pulsar reads.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw Unavailable("CurseForge's answer is larger than Pulsar reads.");
                }

                buffer.Write(chunk, 0, read);
            }

            return JsonSerializer.Deserialize<T>(buffer.ToArray(), s_json);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable($"CurseForge could not be reached ({ex.HttpRequestError}).", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("CurseForge did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw Unavailable("CurseForge sent an answer Pulsar does not understand.", ex);
        }
    }

    private static int ClassId(string projectType) => projectType switch
    {
        "plugin" => 5,
        "modpack" => 4471,
        _ => 6,
    };

    /// <summary>CurseForge's number for the first of the loaders it knows; null for plugins (their class says Bukkit).</summary>
    private static int? LoaderType(IReadOnlyList<string> loaders) => loaders.Select(l => l switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => (int?)null,
    }).FirstOrDefault(t => t is not null);

    private static bool IsId(string id) => id is { Length: > 0 and <= 10 } && id.All(char.IsAsciiDigit) && int.TryParse(id, CultureInfo.InvariantCulture, out _);

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static T? FirstOf<T>(IReadOnlyList<T> list) where T : class => list.Count > 0 ? list[0] : null;

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

    private sealed record Page<T>(List<T>? Data, Pagination? Pagination);

    private sealed record Pagination(int TotalCount);

    private sealed record Author(string? Name);

    private sealed record ModResponse(int Id, string? Name, string? Slug, string? Summary, long DownloadCount, List<Author>? Authors,
        DateTimeOffset? DateModified, List<FileIndex?>? LatestFilesIndexes, int GameId = 0, ModLinks? Links = null);

    private sealed record ModLinks(string? WebsiteUrl);

    private sealed record FileIndex(string? GameVersion, int FileId, int ReleaseType, int? ModLoader);

    private sealed record FileHash(string? Value, int Algo);

    private sealed record FileDependency(int ModId, int RelationType);

    private sealed record FileResponse(int Id, int ModId, string? DisplayName, string? FileName, int ReleaseType, DateTimeOffset? FileDate, long FileLength,
        string? DownloadUrl, List<FileHash?>? Hashes, List<string>? GameVersions, List<FileDependency?>? Dependencies, bool? IsServerPack,
        int? ServerPackFileId);
}
