using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>What the agent installed of an addon, kept in <c>.pulsar/addons/&lt;key&gt;.json</c>.</summary>
/// <param name="Added">Files the addon brought, removed with it.</param>
/// <param name="Replaced">Game files it overwrote: removing it restores them with a validating update.</param>
public sealed record AddonRecord(string Key, string Release, string Asset, string Sha256, IReadOnlyList<string> Added, IReadOnlyList<string> Replaced);

/// <summary>
/// Installs and removes a manifest's addons (docs/architecture/04, "Addons"). The archive is a zip or a .tar.gz asset
/// (Pulsar 1.0.12) of a GitHub release, found through GitHub's API and checked against the SHA-256 GitHub publishes for
/// it; it is unpacked into the
/// addon's folder of the server with the archive rules of restores (relative paths only, nothing under <c>.pulsar</c>,
/// size and entry limits), through the agent's link-safe file access. A game server published so (TShock) is installed
/// the same way by its install step (<see cref="GitHubReleaseStep"/>), recorded in another folder.
/// </summary>
/// <param name="recordDirectory">Where what was installed is recorded: <see cref="RecordDirectory"/> for addons.</param>
public sealed class AddonInstaller(IServerFiles files, HttpMessageHandler handler, string recordDirectory = AddonInstaller.RecordDirectory)
{
    public const string RecordDirectory = ".pulsar/addons";

    private const long MaximumExtracted = 2L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 50_000;

    // A .tar.gz's headers and padding, beside its files: 512 bytes each, extended headers, the end of the archive.
    private const long MaximumTarOverhead = 64L * 1024 * 1024;

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

        using var http = GitHubClient();
        var client = new VerifiedDownloader(http, s_hosts, "addon", "addons");
        try
        {
            await progress(InstallStages.Preparing, null);
            var (release, name, url, sha256) = await AssetAsync(client, addon.Name, source, cancellationToken);
            var previous = Read(serverDirectory, addon.Key);
            if (!force && previous is { } same && same.Asset == name && same.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                return same;
            }

            await using var download = await client.DownloadAsync(VerifiedDownloader.Https(url, "addon"), HashAlgorithmName.SHA256, sha256,
                percent => progress(InstallStages.Downloading, percent), cancellationToken);
            await progress(InstallStages.Installing, null);
            var record = Unpack(serverDirectory, addon, release, name, sha256, download, previous);
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
    /// Whether <paramref name="source"/>'s release has another asset than the one recorded as <paramref name="key"/> (an
    /// update check of a <see cref="GitHubReleaseStep"/>): the releases' tags, the installed one null when nothing is.
    /// </summary>
    /// <exception cref="GameProcessException">With an <c>addon.*</c> code.</exception>
    public async Task<GameBuild> CheckAsync(string serverDirectory, string key, GitHubReleaseSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var http = GitHubClient();
        var client = new VerifiedDownloader(http, s_hosts, "addon", "addons");
        try
        {
            var (release, name, _, sha256) = await AssetAsync(client, source.Repo, source, cancellationToken);
            var installed = Read(serverDirectory, key) is { Asset.Length: > 0 } record ? record : null;
            var same = installed is not null && installed.Asset == name && installed.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase);
            return new GameBuild(installed?.Release, release, !same);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Failed("addon.download_failed", $"{source.Repo}'s releases could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Removes the files <paramref name="addon"/> added and its lines; returns the game files it had replaced (a
    /// validating update brings them back). Removing an addon that is not installed changes nothing.
    /// </summary>
    /// <exception cref="GameProcessException"><c>config.invalid</c>: a file it edits is not UTF-8 text (nothing was removed).</exception>
    public IReadOnlyList<string> Remove(string serverDirectory, AddonDefinition addon)
    {
        ArgumentNullException.ThrowIfNull(addon);
        // The files its lines are taken out of are read first, as the start reads them: one that is not UTF-8 is refused
        // before anything is removed, and never rewritten.
        var edited = new List<(string Path, string Text)>();
        foreach (var edits in addon.LineEditList.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (ReadText(serverDirectory, edits.Key, strict: true) is { } text && edits.Aggregate(text, LineEditor.Remove) is var without && without != text)
            {
                edited.Add((edits.Key, without));
            }
        }

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

        foreach (var (path, text) in edited)
        {
            using var content = new MemoryStream(new UTF8Encoding(false).GetBytes(text));
            files.Write(serverDirectory, path, content);
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
            // The server's account can write this folder: only paths an addon may own are ever touched (never SteamCMD
            // or another addon's record under .pulsar), and a record may lack anything (one without its key is none).
            return record?.Key is null ? null : record with
            {
                Release = record.Release ?? "",
                Asset = record.Asset ?? "",
                Sha256 = record.Sha256 ?? "",
                Added = [.. (record.Added ?? []).Where(p => p is not null && IsAddonPath(p))],
                Replaced = [.. (record.Replaced ?? []).Where(p => p is not null && IsAddonPath(p))],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private HttpClient GitHubClient()
    {
        var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pulsar-Agent", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The release's one asset that matches, with the SHA-256 GitHub publishes for it (one without is refused).</summary>
    private static async Task<(string Release, string Name, string Url, string Sha256)> AssetAsync(VerifiedDownloader client, string what,
        GitHubReleaseSource source, CancellationToken cancellationToken)
    {
        var release = await ReleaseAsync(client, source, cancellationToken);
        var pattern = new Regex(source.Asset, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var matching = (release.Assets ?? []).Where(a => a.Name is not null && pattern.IsMatch(a.Name)).ToList();
        if (matching is not [{ Name: { } name, BrowserDownloadUrl: { } url } asset])
        {
            throw Failed("addon.no_asset", $"{what} {release.TagName}: {matching.Count} assets match '{source.Asset}', not one.");
        }

        if (asset.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
        {
            throw Failed("addon.no_checksum", $"GitHub publishes no SHA-256 for {name}: Pulsar does not install it.");
        }

        return (release.TagName ?? name, name, url, digest[7..]);
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
        using var archive = IsTarGz(asset) ? (AddonArchive)new TarGzArchive(download, asset) : new ZipAddonArchive(download);
        var entries = new List<string>();
        long total = 0;
        foreach (var (entryName, length) in archive.Files())
        {
            var name = entryName.Replace('\\', '/');
            var path = addon.Target.Length == 0 ? name : $"{addon.Target}/{name}";
            if (!IsAddonPath(path))
            {
                throw Failed("addon.bad_archive", $"{asset} has an entry Pulsar does not unpack: '{entryName}'.");
            }

            total += length;
            if (total > MaximumExtracted || entries.Count >= MaximumEntries)
            {
                throw Failed("addon.bad_archive", $"{asset} is larger than Pulsar unpacks.");
            }

            entries.Add(path);
        }

        // A file counts as the game's (replaced, not removed with the addon) when it was there before the addon was.
        var ownedBefore = new HashSet<string>(previous?.Added ?? [], StringComparer.OrdinalIgnoreCase);
        var replacedBefore = new HashSet<string>(previous?.Replaced ?? [], StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var replaced = new List<string>();
        foreach (var path in entries)
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
        }

        // Game files an older release replaced stay replaced when this one no longer ships them: they still hold its copy.
        var replacing = replaced.Union(previous?.Replaced ?? [], StringComparer.OrdinalIgnoreCase).ToList();

        // Recorded before the first file is written, so an unpacking that fails midway leaves a record a retry or a removal
        // trusts: what this attempt adds stays the addon's. No asset: a retry never takes it as installed.
        Write(serverDirectory, new AddonRecord(addon.Key, previous?.Release ?? "", "", "",
            [.. ownedBefore.Union(added, StringComparer.OrdinalIgnoreCase).Except(replacing, StringComparer.OrdinalIgnoreCase)], replacing));
        archive.Extract((index, content) => files.Write(serverDirectory, entries[index], content));

        // Files of the previous release the new one does not have.
        var current = entries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in ownedBefore.Where(p => !current.Contains(p)))
        {
            Delete(serverDirectory, old);
        }

        return new AddonRecord(addon.Key, release, asset, sha256.ToLowerInvariant(), added, replacing);
    }

    private void Write(string serverDirectory, AddonRecord record)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(record, s_json));
        files.Write(serverDirectory, RecordPath(record.Key), content);
    }

    /// <param name="strict">A game file the addon edits: refused when it is not UTF-8 (<see cref="Utf8Text"/>).</param>
    private string? ReadText(string serverDirectory, string path, bool strict = false)
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

        return strict ? Utf8Text.Read(stream, path) : new StreamReader(stream, Encoding.UTF8).ReadToEnd();
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

    private string RecordPath(string key) => $"{recordDirectory}/{key}.json";

    /// <summary>
    /// A path an addon may own: inside the server directory, outside Pulsar's own files (<c>.pulsar</c>, by any case or
    /// by a first segment shaped like its 8.3 short name: addon files are written as SYSTEM).
    /// </summary>
    private static bool IsAddonPath(string path) => ManifestValidator.IsValidRelativePath(path) && !FileCommands.IsPulsarFolder(path);

    private static IEnumerable<string> Parents(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            yield return path[..slash];
        }
    }

    private static GameProcessException Failed(string code, string message, Exception? inner = null) => new(code, message, inner);

    private static bool IsTarGz(string asset) =>
        asset.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || asset.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    /// <summary>An addon's archive: its files (folders left out), then their contents, by their place in that list.</summary>
    private abstract class AddonArchive : IDisposable
    {
        public abstract IReadOnlyList<(string Name, long Length)> Files();

        public abstract void Extract(Action<int, Stream> write);

        public abstract void Dispose();
    }

    private sealed class ZipAddonArchive(Stream download) : AddonArchive
    {
        private readonly ZipArchive _zip = new(download, ZipArchiveMode.Read);

        public override IReadOnlyList<(string Name, long Length)> Files() => [.. FileEntries().Select(e => (e.FullName, e.Length))];

        public override void Extract(Action<int, Stream> write)
        {
            var index = 0;
            foreach (var entry in FileEntries())
            {
                using var content = BoundedEntryStream.Open(entry);
                write(index++, content);
            }
        }

        public override void Dispose() => _zip.Dispose();

        private IEnumerable<ZipArchiveEntry> FileEntries() => _zip.Entries.Where(e => !e.FullName.Replace('\\', '/').EndsWith('/'));
    }

    /// <summary>
    /// A .tar.gz, read twice from the verified download (a file): once for its files, which are checked before anything is
    /// written, once to write them. Only plain files and folders: a link, a device or anything else fails it, and a file's
    /// content ends where its header says. What either reading decompresses is counted, headers and skipped data included:
    /// past the size of the files Pulsar unpacks and room for their headers, the archive is refused (a small download
    /// can hold far more).
    /// </summary>
    private sealed class TarGzArchive(Stream download, string asset) : AddonArchive
    {
        public override IReadOnlyList<(string Name, long Length)> Files()
        {
            var files = new List<(string Name, long Length)>();
            Read(entry => files.Add((Name(entry), entry.Length)));
            return files;
        }

        public override void Extract(Action<int, Stream> write)
        {
            var index = 0;
            Read(entry => write(index++, entry.DataStream ?? Stream.Null));
        }

        public override void Dispose()
        {
        }

        private void Read(Action<TarEntry> file)
        {
            download.Seek(0, SeekOrigin.Begin);
            using var gzip = new GZipStream(download, CompressionMode.Decompress, leaveOpen: true);
            using var counted = new BoundedReadStream(gzip, MaximumExtracted + MaximumTarOverhead,
                () => Failed("addon.bad_archive", $"{asset} is larger than Pulsar unpacks."));
            using var reader = new TarReader(counted, leaveOpen: true);
            var entries = 0;
            while (reader.GetNextEntry() is { } entry)
            {
                if (++entries > MaximumEntries)
                {
                    throw Failed("addon.bad_archive", $"{asset} is larger than Pulsar unpacks.");
                }

                switch (entry.EntryType)
                {
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                        file(entry);
                        break;
                    case TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes:
                        break;
                    default:
                        throw Failed("addon.bad_archive", $"{asset} has an entry Pulsar does not unpack: '{entry.Name}' ({entry.EntryType}).");
                }
            }
        }

        // Tar names often start with ./ (a folder packed from inside it).
        private static string Name(TarEntry entry) => entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
    }

    /// <summary>Reads another stream up to a number of bytes in all; reading further throws what <c>tooLarge</c> makes.</summary>
    private sealed class BoundedReadStream(Stream inner, long maximum, Func<Exception> tooLarge) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            _read += read;
            return _read > maximum ? throw tooLarge() : read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        bool Draft,
        IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        string? Digest);
}
