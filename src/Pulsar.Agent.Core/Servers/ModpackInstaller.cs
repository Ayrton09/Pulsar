using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Installs a modpack (<see cref="InstallModpackCommand"/>, Pulsar 1.0.9; docs/architecture/04, "Modpacks") in two steps.
/// <see cref="PrepareAsync"/> changes nothing on the server. For a Modrinth pack it downloads the <c>.mrpack</c>, checks its
/// checksum, reads its index (<c>modrinth.index.json</c>), which must name the game version and loader the panel chose,
/// and downloads every file the index lists for servers, each from a host the format allows and checked against its
/// SHA-512, into a folder of the agent's own. For a CurseForge pack it reads the pack's <c>manifest.json</c> for the game
/// version and loader the same way, and takes its files from the server pack its author publishes (checked against
/// CurseForge's SHA-1), not from the pack's list, which holds mods for players' games too. Once the game's install steps
/// have put in the pack's loader, <see cref="Apply"/> deletes the previous pack's files, writes the new ones and records
/// what it wrote in <c>.pulsar/modpack.json</c>. Every path is a plain relative one outside Pulsar's folder; sizes and
/// counts are bounded.
/// </summary>
public sealed class ModpackInstaller(IServerFiles files, HttpMessageHandler handler)
{
    public const string RecordPath = ".pulsar/modpack.json";

    /// <summary>The pack, its files' total and the overrides' total are each bounded by this.</summary>
    public const long MaximumSize = 4L * 1024 * 1024 * 1024;

    public const long MaximumFile = 1024L * 1024 * 1024;

    public const int MaximumFiles = 2000;

    public const int MaximumOverrides = 50_000;

    private const long MaximumIndex = 8 * 1024 * 1024;
    private const string IndexName = "modrinth.index.json";
    private const string ManifestName = "manifest.json";
    private const int MaximumNameLength = 64;

    /// <summary>Room left on the agent's drive after the pack and its files are downloaded there.</summary>
    private const long FreeSpaceMargin = 1024L * 1024 * 1024;

    // The pack comes from its catalog's CDN only: Modrinth's, or CurseForge's (whose edge redirects to its file host).
    private static readonly HashSet<string> s_packHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "edge.forgecdn.net", "mediafilez.forgecdn.net",
    };

    // A server pack's scripts and programs: Pulsar starts the game itself, and never runs what a pack brings.
    private static readonly HashSet<string> s_skippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".sh", ".ps1", ".psm1", ".psd1", ".ps1xml", ".exe", ".com", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".command",
        ".msi", ".msp", ".scr", ".lnk", ".pif", ".cpl", ".reg", ".scf", ".dll",
    };

    // Its files, from the hosts the .mrpack format allows (GitHub's downloads redirect to its asset hosts).
    private static readonly HashSet<string> s_fileHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com", "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The index's name for a catalog loader's version among its dependencies.</summary>
    public static string? IndexLoader(string loader) => loader switch
    {
        "fabric" => "fabric-loader",
        "quilt" => "quilt-loader",
        "forge" => "forge",
        "neoforge" => "neoforge",
        _ => null,
    };

    /// <summary>Downloads and checks the pack and its files; nothing on the server changes.</summary>
    /// <param name="serverPack">A CurseForge pack's server files; null for a Modrinth pack.</param>
    /// <param name="loaderVersionError">Why the pack's loader version is not one the server's variable takes, or null.</param>
    /// <exception cref="GameProcessException">With a <c>modpack.*</c> code.</exception>
    public async Task<PreparedModpack> PrepareAsync(ModDownload pack, ModDownload? serverPack, string loader, string gameVersion,
        Func<string, string?> loaderVersionError, Func<string, double?, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(loaderVersionError);
        ArgumentNullException.ThrowIfNull(progress);
        if (pack.Size is < 0 or > MaximumSize || pack.Algorithm is not { } algorithm || IndexLoader(loader) is not { } loaderKey
            || (serverPack is not null && (serverPack.Size is < 0 or > MaximumSize || serverPack.Algorithm is null)))
        {
            throw Failed("modpack.invalid", "The modpack has no checksum, is larger than Pulsar installs, or is for a loader Pulsar does not know.");
        }

        EnsureSpace(pack.Size + (serverPack?.Size ?? 0));
        var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var prepared = new PreparedModpack(http);
        try
        {
            var packs = new VerifiedDownloader(http, s_packHosts, "modpack", "modpacks") { StallTimeout = StallTimeout };
            prepared.Pack = await Download(() => packs.DownloadAsync(VerifiedDownloader.Https(pack.Url, "modpack"), algorithm, pack.Hash,
                percent => progress(Steam.InstallStages.Downloading, percent / 10), cancellationToken, MaximumSize));
            prepared.Archive = await OpenZipAsync(prepared.Pack, cancellationToken);
            if (serverPack is not null)
            {
                await PrepareServerPackAsync(prepared, packs, serverPack, loader, gameVersion, loaderVersionError, progress, cancellationToken);
                return prepared;
            }

            var index = ReadIndex(prepared.Archive);
            prepared.Name = Name(index.Name);
            prepared.LoaderVersion = CheckDependencies(index, loaderKey, gameVersion, loaderVersionError);
            var listed = Files(index);
            prepared.Overrides = Overrides(prepared.Archive);

            // Several servers may install packs at once: each checks the room its files need before downloading them.
            EnsureSpace(listed.Sum(f => f.Size));

            // Every file before anything changes: a pack that cannot be had whole is not installed at all.
            prepared.Staging = Directory.CreateTempSubdirectory("pulsar-modpack-").FullName;
            var downloads = new VerifiedDownloader(http, s_fileHosts, "modpack", "a modpack's files") { StallTimeout = StallTimeout };
            for (var i = 0; i < listed.Count; i++)
            {
                var (path, url, hash, size) = listed[i];
                var staged = Path.Combine(prepared.Staging, i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
                var index1 = i;
                await using (var body = await Download(() => downloads.DownloadAsync(url, HashAlgorithmName.SHA512, hash,
                                 percent => progress(Steam.InstallStages.Downloading, 10 + (index1 + percent / 100) * 90 / listed.Count), cancellationToken,
                                 Math.Max(size, 1))))
                await using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    body.Position = 0;
                    await body.CopyToAsync(file, cancellationToken);
                }

                prepared.Files.Add((path, staged));
            }

            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A CurseForge pack: its <c>manifest.json</c> must name the game version and loader the panel chose (and the loader's
    /// version); its files are the server pack's, unpacked from its one top folder when it has one, without scripts or programs.
    /// </summary>
    private static async Task PrepareServerPackAsync(PreparedModpack prepared, VerifiedDownloader packs, ModDownload serverPack, string loader,
        string gameVersion, Func<string, string?> loaderVersionError, Func<string, double?, Task> progress, CancellationToken cancellationToken)
    {
        var manifest = ReadManifest(prepared.Archive!);
        prepared.Name = Name(manifest.Name);
        if (manifest.Minecraft?.Version != gameVersion)
        {
            throw Failed("modpack.mismatch", $"The modpack is for Minecraft {manifest.Minecraft?.Version ?? "(none named)"}, not {gameVersion}.");
        }

        // CurseForge names a loader with its version: forge-47.2.0, neoforge-21.1.72, fabric-0.16.10.
        var loaders = manifest.Minecraft.ModLoaders ?? [];
        var primary = loaders.FirstOrDefault(l => l?.Primary == true) ?? (loaders.Count == 1 ? loaders[0] : null);
        var prefix = $"{loader}-";
        if (primary?.Id is not { } id || !id.StartsWith(prefix, StringComparison.Ordinal) || loaderVersionError(id[prefix.Length..]) is not null)
        {
            throw Failed("modpack.mismatch", $"The modpack names no version of {loader} the server takes.");
        }

        prepared.LoaderVersion = id[prefix.Length..];
        prepared.ServerPack = await Download(() => packs.DownloadAsync(VerifiedDownloader.Https(serverPack.Url, "modpack"), serverPack.Algorithm!.Value,
            serverPack.Hash, percent => progress(Steam.InstallStages.Downloading, 10 + percent * 0.9), cancellationToken, MaximumSize));
        prepared.ServerArchive = await OpenZipAsync(prepared.ServerPack, cancellationToken);
        prepared.Overrides = ServerPackFiles(prepared.ServerArchive);
    }

    /// <summary>
    /// The pack's zip, its list of entries bounded before .NET reads it into memory (as every other zip the agent opens):
    /// one of far too many entries, even of those never installed, is refused first.
    /// </summary>
    private static async Task<ZipArchive> OpenZipAsync(FileStream content, CancellationToken cancellationToken)
    {
        content.Position = 0;
        try
        {
            await FileArchives.CheckDirectoryAsync(content, cancellationToken);
        }
        catch (FileManagerException ex)
        {
            throw Failed("modpack.too_large", $"The modpack is larger than Pulsar installs: {ex.Message}", ex);
        }

        return new ZipArchive(content, ZipArchiveMode.Read);
    }

    private static CurseForgeManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(ManifestName);
        if (entry is null || entry.Length > MaximumIndex)
        {
            throw Failed("modpack.invalid", $"The modpack has no {ManifestName}, or a larger one than Pulsar reads.");
        }

        try
        {
            using var content = BoundedEntryStream.Open(entry);
            var manifest = JsonSerializer.Deserialize<CurseForgeManifest>(content, s_json);
            return manifest is { ManifestType: "minecraftModpack", ManifestVersion: 1, Minecraft: not null }
                ? manifest
                : throw Failed("modpack.invalid", "The modpack's manifest is not one of a Minecraft modpack in version 1.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw Failed("modpack.invalid", $"The modpack's manifest cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A server pack's files: under its one top folder when every file is in the same one (most are zipped so), else as
    /// they are; scripts and programs left out; checked as the overrides of a Modrinth pack are.
    /// </summary>
    private static List<(string Path, ZipArchiveEntry Entry)> ServerPackFiles(ZipArchive archive)
    {
        var files = archive.Entries.Where(e => !e.FullName.Replace('\\', '/').EndsWith('/')).ToList();
        var names = files.Select(e => e.FullName.Replace('\\', '/')).ToList();
        var top = names.Count > 0 && names.All(n => n.Contains('/', StringComparison.Ordinal))
            && names.Select(n => n[..n.IndexOf('/', StringComparison.Ordinal)]).Distinct(StringComparer.Ordinal).Count() == 1
                ? names[0][..(names[0].IndexOf('/', StringComparison.Ordinal) + 1)]
                : "";
        var chosen = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            var path = names[i][top.Length..];
            if (s_skippedExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            if (!IsPackPath(path) || !chosen.TryAdd(path, files[i]))
            {
                throw Failed("modpack.unsafe_path", $"The server pack has a file Pulsar does not write: '{files[i].FullName}'.");
            }
        }

        if (chosen.Count == 0)
        {
            throw Failed("modpack.invalid", "The server pack has no files.");
        }

        if (chosen.Count > MaximumOverrides || chosen.Values.Sum(e => e.Length) > MaximumSize)
        {
            throw Failed("modpack.too_large", "The server pack is larger than Pulsar installs.");
        }

        return [.. chosen.Select(p => (p.Key, p.Value))];
    }

    /// <summary>
    /// Puts the prepared pack on the server, after its loader was installed: the previous pack's files are deleted (those
    /// the new one does not have), then its files and overrides are written. Returns how many files were written.
    /// </summary>
    /// <exception cref="GameProcessException"><c>modpack.write_failed</c>: the server may hold part of the pack.</exception>
    public int Apply(string serverDirectory, PreparedModpack prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        // All it writes is the pack's: its files and its overrides go when another pack takes its place.
        var current = prepared.Files.Select(f => f.Path).Concat(prepared.Overrides.Select(o => o.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var previous = ReadRecord(serverDirectory);
        try
        {
            // Recorded first, the old files with the new: an install that fails midway leaves a record the next one trusts.
            WriteRecord(serverDirectory, new ModpackRecord(prepared.Name, [.. current.Union(previous, StringComparer.OrdinalIgnoreCase)]));
            var keep = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in previous.Where(p => !keep.Contains(p)))
            {
                Delete(serverDirectory, old);
            }

            foreach (var (path, staged) in prepared.Files)
            {
                using var content = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
                files.Write(serverDirectory, path, content);
            }

            foreach (var (path, entry) in prepared.Overrides)
            {
                using var content = BoundedEntryStream.Open(entry);
                files.Write(serverDirectory, path, content);
            }

            WriteRecord(serverDirectory, new ModpackRecord(prepared.Name, current));
            return prepared.Files.Count + prepared.Overrides.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw Failed("modpack.write_failed", $"The modpack could not all be written: {ex.Message}", ex);
        }
    }

    private static ModpackIndex ReadIndex(ZipArchive archive)
    {
        var entry = archive.GetEntry(IndexName);
        if (entry is null || entry.Length > MaximumIndex)
        {
            throw Failed("modpack.invalid", $"The modpack has no {IndexName}, or a larger one than Pulsar reads.");
        }

        try
        {
            using var content = BoundedEntryStream.Open(entry);
            var index = JsonSerializer.Deserialize<ModpackIndex>(content, s_json);
            return index is { FormatVersion: 1, Game: "minecraft" }
                ? index
                : throw Failed("modpack.invalid", "The modpack's index is not one of Minecraft's in format 1.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw Failed("modpack.invalid", $"The modpack's index cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>The index must be for the game version and loader the panel chose, and nothing else; returns the loader's version.</summary>
    private static string CheckDependencies(ModpackIndex index, string loaderKey, string gameVersion, Func<string, string?> loaderVersionError)
    {
        var dependencies = index.Dependencies ?? [];
        if (!dependencies.TryGetValue("minecraft", out var minecraft) || minecraft != gameVersion)
        {
            throw Failed("modpack.mismatch", $"The modpack is for Minecraft {minecraft ?? "(none named)"}, not {gameVersion}.");
        }

        if (!dependencies.TryGetValue(loaderKey, out var loaderVersion) || loaderVersion is null || loaderVersionError(loaderVersion) is { } error)
        {
            throw Failed("modpack.mismatch", $"The modpack names no version of {loaderKey} the server takes.");
        }

        if (dependencies.Keys.FirstOrDefault(k => k != "minecraft" && k != loaderKey) is { } other)
        {
            throw Failed("modpack.mismatch", $"The modpack also needs {other}, which this server does not run.");
        }

        return loaderVersion;
    }

    /// <summary>The files the index lists for servers, checked: paths, checksums, sizes, hosts.</summary>
    private static List<(string Path, Uri Url, string Sha512, long Size)> Files(ModpackIndex index)
    {
        var listed = new List<(string, Uri, string, long)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in index.Files ?? [])
        {
            if (file.Env?.Server == "unsupported")
            {
                continue; // only for players' games
            }

            if (file.Path is not { } path || !IsPackPath(path) || !paths.Add(path))
            {
                throw Failed("modpack.unsafe_path", $"The modpack lists a file Pulsar does not write: '{file.Path}'.");
            }

            if (file.Hashes?.Sha512 is not { Length: 128 } sha512 || !sha512.All(char.IsAsciiHexDigit) || file.FileSize is < 0 or > MaximumFile)
            {
                throw Failed("modpack.invalid", $"'{path}' has no SHA-512, or is larger than Pulsar installs.");
            }

            var url = (file.Downloads ?? []).Select(d => Uri.TryCreate(d, UriKind.Absolute, out var uri) ? uri : null)
                .FirstOrDefault(u => u is not null && u.Scheme == Uri.UriSchemeHttps && s_fileHosts.Contains(u.Host))
                ?? throw Failed("modpack.untrusted_host", $"'{path}' is not offered by a host Pulsar downloads modpacks' files from.");
            total += file.FileSize;
            if (listed.Count >= MaximumFiles || total > MaximumSize)
            {
                throw Failed("modpack.too_large", "The modpack has more files than Pulsar installs.");
            }

            listed.Add((path, url, sha512.ToLowerInvariant(), file.FileSize));
        }

        return listed;
    }

    /// <summary>The overrides' files, <c>server-overrides/</c> over <c>overrides/</c>, checked as the index's files are.</summary>
    private static List<(string Path, ZipArchiveEntry Entry)> Overrides(ZipArchive archive)
    {
        var chosen = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in new[] { "overrides/", "server-overrides/" })
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/'))
                {
                    continue; // another folder of the pack, or a folder (it comes with its files)
                }

                var path = name[prefix.Length..];
                if (!IsPackPath(path))
                {
                    throw Failed("modpack.unsafe_path", $"The modpack has an override Pulsar does not write: '{entry.FullName}'.");
                }

                chosen[path] = entry;
            }
        }

        if (chosen.Count > MaximumOverrides || chosen.Values.Sum(e => e.Length) > MaximumSize)
        {
            throw Failed("modpack.too_large", "The modpack's overrides are larger than Pulsar installs.");
        }

        return [.. chosen.Select(p => (p.Key, p.Value))];
    }

    /// <summary>The pack's name as its author wrote it, for the console and the record: no control or format characters, and short.</summary>
    private static string? Name(string? name)
    {
        var cleaned = new string([.. (name ?? "").Where(c => !char.IsControl(c)
            && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)]).Trim();
        return cleaned.Length == 0 ? null : cleaned.Length <= MaximumNameLength ? cleaned : cleaned[..MaximumNameLength];
    }

    private static void EnsureSpace(long needed)
    {
        var temporary = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        if (temporary is { Length: > 0 } && new DriveInfo(temporary).AvailableFreeSpace - needed < FreeSpaceMargin)
        {
            throw Failed("modpack.disk_full", $"The node's drive {temporary} has too little free space for the modpack.");
        }
    }

    private static bool IsPackPath(string path) => ManifestValidator.IsValidRelativePath(path) && !FileCommands.IsPulsarFolder(path);

    /// <summary>The previous pack's files, as recorded; only paths a pack may own are ever deleted.</summary>
    private List<string> ReadRecord(string serverDirectory)
    {
        using var stream = files.OpenRead(serverDirectory, RecordPath);
        if (stream is null || stream.Length > MaximumIndex)
        {
            return [];
        }

        try
        {
            // The server's account can write Pulsar's folder: a record that is not one is none.
            var record = JsonSerializer.Deserialize<ModpackRecord>(stream, s_json);
            return [.. (record?.Files ?? []).Where(p => p is not null && IsPackPath(p)).Take(MaximumFiles)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteRecord(string serverDirectory, ModpackRecord record)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(record, s_json));
        files.Write(serverDirectory, RecordPath, content);
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

    private static async Task<FileStream> Download(Func<Task<FileStream>> download)
    {
        try
        {
            return await download();
        }
        catch (HttpRequestException ex)
        {
            throw Failed("modpack.download_failed", $"The modpack could not be downloaded: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw Failed("modpack.download_failed", $"The modpack could not be downloaded: {ex.Message}", ex);
        }
    }

    private static GameProcessException Failed(string code, string message, Exception? inner = null) => new(code, message, inner);

    /// <summary>What the agent wrote of the installed pack, kept in <see cref="RecordPath"/>.</summary>
    public sealed record ModpackRecord(string? Name, IReadOnlyList<string> Files);

    private sealed record ModpackIndex(int FormatVersion, string? Game, string? Name, List<IndexFile>? Files, Dictionary<string, string?>? Dependencies);

    private sealed record IndexFile(string? Path, IndexHashes? Hashes, IndexEnv? Env, List<string>? Downloads, long FileSize);

    private sealed record IndexHashes(string? Sha1, string? Sha512);

    private sealed record IndexEnv(string? Client, string? Server);

    private sealed record CurseForgeManifest(string? ManifestType, int ManifestVersion, string? Name, CurseForgeMinecraft? Minecraft);

    private sealed record CurseForgeMinecraft(string? Version, List<CurseForgeLoader?>? ModLoaders);

    private sealed record CurseForgeLoader(string? Id, bool Primary);
}

/// <summary>A modpack downloaded and checked (<see cref="ModpackInstaller.PrepareAsync"/>): its files wait in a folder of the agent's own.</summary>
public sealed class PreparedModpack : IDisposable
{
    private readonly HttpClient _http;

    internal PreparedModpack(HttpClient http) => _http = http;

    public string? Name { get; internal set; }

    /// <summary>The loader's version the pack's index names.</summary>
    public string LoaderVersion { get; internal set; } = "";

    internal FileStream? Pack { get; set; }

    internal ZipArchive? Archive { get; set; }

    /// <summary>A CurseForge pack's server files.</summary>
    internal FileStream? ServerPack { get; set; }

    internal ZipArchive? ServerArchive { get; set; }

    internal string? Staging { get; set; }

    internal List<(string Path, string Staged)> Files { get; } = [];

    internal List<(string Path, ZipArchiveEntry Entry)> Overrides { get; set; } = [];

    public int Count => Files.Count + Overrides.Count;

    public void Dispose()
    {
        Archive?.Dispose();
        Pack?.Dispose();
        ServerArchive?.Dispose();
        ServerPack?.Dispose();
        _http.Dispose();
        if (Staging is not null)
        {
            try
            {
                Directory.Delete(Staging, recursive: true);
            }
            catch (IOException)
            {
                // a file still open (an antivirus scan): the system's temporary folder is cleaned in time
            }
        }
    }
}
