using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>What the agent installed of an addon, kept in <c>.pulsar/addons/&lt;key&gt;.json</c>.</summary>
/// <param name="Added">Files the addon brought, removed with it.</param>
/// <param name="Replaced">Game files it overwrote: removing it restores them with a validating update.</param>
public sealed record AddonRecord(string Key, string Release, string Asset, string Sha256, IReadOnlyList<string> Added, IReadOnlyList<string> Replaced);

/// <summary>
/// Installs and removes a manifest's addons (docs/architecture/04, "Addons"). The archive is a zip asset of a GitHub
/// release, found through GitHub's API and checked against the SHA-256 GitHub publishes for it; it is unpacked into the
/// addon's folder of the server with the archive rules of restores (relative paths only, nothing under <c>.pulsar</c>,
/// size and entry limits), through the agent's link-safe file access.
/// </summary>
public sealed class AddonInstaller(IServerFiles files, HttpMessageHandler handler)
{
    public const string RecordDirectory = ".pulsar/addons";

    private const long MaximumExtracted = 2L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 50_000;

    // The API, and the hosts GitHub's release downloads redirect to.
    private static readonly HashSet<string> s_hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The GitHub API's address (tests point it elsewhere through the handler, never through this).</summary>
    public static readonly Uri GitHubApi = new("https://api.github.com/");

    /// <summary>
    /// Installs <paramref name="addon"/>'s release, or brings it to the latest one; nothing is downloaded when the same
    /// asset is installed and <paramref name="force"/> is off (a game update overwrote it: force).
    /// </summary>
    /// <exception cref="GameProcessException">With an <c>addon.*</c> code.</exception>
    public async Task<AddonRecord> InstallAsync(string serverDirectory, AddonDefinition addon, bool force, Func<string, double?, Task> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addon);
        ArgumentNullException.ThrowIfNull(progress);
        if (addon.Source is not GitHubReleaseSource source)
        {
            throw Failed("addon.unsupported_source", $"This agent cannot install {addon.Name} from its source.");
        }

        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pulsar-Agent", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var client = new VerifiedDownloader(http, s_hosts, "addon", "addons");
        try
        {
            await progress(InstallStages.Preparing, null);
            var release = await ReleaseAsync(client, source, cancellationToken);
            var pattern = new Regex(source.Asset, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var matching = (release.Assets ?? []).Where(a => a.Name is not null && pattern.IsMatch(a.Name)).ToList();
            if (matching is not [{ Name: { } name, BrowserDownloadUrl: { } url } asset])
            {
                throw Failed("addon.no_asset", $"{addon.Name} {release.TagName}: {matching.Count} assets match '{source.Asset}', not one.");
            }

            if (asset.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
            {
                throw Failed("addon.no_checksum", $"GitHub publishes no SHA-256 for {name}: Pulsar does not install it.");
            }

            var sha256 = digest[7..];
            var previous = Read(serverDirectory, addon.Key);
            if (!force && previous is { } same && same.Asset == name && same.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                return same;
            }

            await using var download = await client.DownloadAsync(VerifiedDownloader.Https(url, "addon"), HashAlgorithmName.SHA256, sha256,
                percent => progress(InstallStages.Downloading, percent), cancellationToken);
            await progress(InstallStages.Installing, null);
            var record = Unpack(serverDirectory, addon, release.TagName ?? name, name, sha256, download, previous);
            Write(serverDirectory, record);
            return record;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Failed("addon.download_failed", $"{addon.Name} could not be installed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Removes the files <paramref name="addon"/> added and its lines; returns the game files it had replaced (a
    /// validating update brings them back). Removing an addon that is not installed changes nothing.
    /// </summary>
    public IReadOnlyList<string> Remove(string serverDirectory, AddonDefinition addon)
    {
        ArgumentNullException.ThrowIfNull(addon);
        var record = Read(serverDirectory, addon.Key);
        foreach (var path in record?.Added ?? [])
        {
            Delete(serverDirectory, path);
        }

        // Folders the addon's files leave empty, deepest first, down to its target folder (which stays).
        var target = addon.Target.Trim('/');
        var folders = (record?.Added ?? []).SelectMany(Parents).Where(f => target.Length == 0 || f.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length);
        foreach (var folder in folders)
        {
            files.DeleteEmptyDirectory(serverDirectory, folder);
        }

        foreach (var edits in addon.LineEditList.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (ReadText(serverDirectory, edits.Key) is { } text && edits.Aggregate(text, LineEditor.Remove) is var edited && edited != text)
            {
                using var content = new MemoryStream(new UTF8Encoding(false).GetBytes(edited));
                files.Write(serverDirectory, edits.Key, content);
            }
        }

        Delete(serverDirectory, RecordPath(addon.Key));
        return record?.Replaced ?? [];
    }

    /// <summary>The record of an installed addon; null when it is not installed (or its record cannot be read).</summary>
    public AddonRecord? Read(string serverDirectory, string key)
    {
        if (ReadText(serverDirectory, RecordPath(key)) is not { } text)
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<AddonRecord>(text, s_json);
            // The server's account can write this folder: only paths inside the server are ever touched.
            return record is null ? null : record with
            {
                Added = [.. record.Added.Where(ManifestValidator.IsValidRelativePath)],
                Replaced = [.. record.Replaced.Where(ManifestValidator.IsValidRelativePath)],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<GitHubRelease> ReleaseAsync(VerifiedDownloader client, GitHubReleaseSource source, CancellationToken cancellationToken)
    {
        if (source.Tag is { } tag)
        {
            return await client.GetJsonAsync<GitHubRelease>(new Uri(GitHubApi, $"repos/{source.Repo}/releases/tags/{Uri.EscapeDataString(tag)}"), null,
                cancellationToken);
        }

        if (!source.Prerelease)
        {
            return await client.GetJsonAsync<GitHubRelease>(new Uri(GitHubApi, $"repos/{source.Repo}/releases/latest"), null, cancellationToken);
        }

        var releases = await client.GetJsonAsync<GitHubRelease[]>(new Uri(GitHubApi, $"repos/{source.Repo}/releases?per_page=10"), null, cancellationToken);
        return releases.FirstOrDefault(r => !r.Draft) ?? throw Failed("addon.no_release", $"{source.Repo} has no release.");
    }

    private AddonRecord Unpack(string serverDirectory, AddonDefinition addon, string release, string asset, string sha256, Stream download,
        AddonRecord? previous)
    {
        using var archive = new ZipArchive(download, ZipArchiveMode.Read);
        var entries = new List<(ZipArchiveEntry Entry, string Path)>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue;
            }

            var path = addon.Target.Length == 0 ? name : $"{addon.Target}/{name}";
            if (!ManifestValidator.IsValidRelativePath(path) || path.Split('/')[0].Equals(".pulsar", StringComparison.OrdinalIgnoreCase))
            {
                throw Failed("addon.bad_archive", $"{asset} has an entry Pulsar does not unpack: '{entry.FullName}'.");
            }

            total += entry.Length;
            if (total > MaximumExtracted || entries.Count >= MaximumEntries)
            {
                throw Failed("addon.bad_archive", $"{asset} is larger than Pulsar unpacks.");
            }

            entries.Add((entry, path));
        }

        // A file counts as the game's (replaced, not removed with the addon) when it was there before the addon was.
        var ownedBefore = new HashSet<string>(previous?.Added ?? [], StringComparer.OrdinalIgnoreCase);
        var replacedBefore = new HashSet<string>(previous?.Replaced ?? [], StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var replaced = new List<string>();
        foreach (var (entry, path) in entries)
        {
            bool existed;
            using (var existing = files.OpenRead(serverDirectory, path))
            {
                existed = existing is not null;
            }

            if (replacedBefore.Contains(path) || (existed && !ownedBefore.Contains(path)))
            {
                replaced.Add(path);
            }
            else
            {
                added.Add(path);
            }

            using var content = entry.Open();
            files.Write(serverDirectory, path, content);
        }

        // Files of the previous release the new one does not have.
        var current = entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in ownedBefore.Where(p => !current.Contains(p)))
        {
            Delete(serverDirectory, old);
        }

        return new AddonRecord(addon.Key, release, asset, sha256.ToLowerInvariant(), added, replaced);
    }

    private void Write(string serverDirectory, AddonRecord record)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(record, s_json));
        files.Write(serverDirectory, RecordPath(record.Key), content);
    }

    private string? ReadText(string serverDirectory, string path)
    {
        using var stream = files.OpenRead(serverDirectory, path);
        if (stream is null)
        {
            return null;
        }

        if (stream.Length > ConfigFileWriter.MaximumSize * 8)
        {
            throw Failed("addon.file_too_large", $"{path} is larger than Pulsar reads.");
        }

        return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
    }

    private void Delete(string serverDirectory, string path)
    {
        try
        {
            files.Delete(serverDirectory, path);
        }
        catch (DirectoryNotFoundException)
        {
            // already gone with its folder
        }
    }

    private static string RecordPath(string key) => $"{RecordDirectory}/{key}.json";

    private static IEnumerable<string> Parents(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            yield return path[..slash];
        }
    }

    private static GameProcessException Failed(string code, string message, Exception? inner = null) => new(code, message, inner);

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        bool Draft,
        IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        string? Digest);
}
