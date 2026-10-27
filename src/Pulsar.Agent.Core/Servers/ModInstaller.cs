using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>What the agent wrote of a package (Pulsar 1.0.14), kept in <c>.pulsar/packages/&lt;package&gt;.json</c>: removed with it.</summary>
public sealed record PackageRecord(string Package, IReadOnlyList<string> Files);

/// <summary>
/// Puts a mod catalog's files into a server and deletes others (<see cref="ChangeModsCommand"/>, Pulsar 1.0.9;
/// docs/architecture/04, "Mods"). Every file is downloaded first, over HTTPS from the catalog's own hosts, and checked
/// against its SHA-512 or SHA-1; only then are files deleted and written, so a failed download changes nothing. Files go
/// only into the folders the game's manifest names for its catalogs, ending as it says (<c>.jar</c>; uMod's <c>.cs</c>).
/// A Thunderstore package (Pulsar 1.0.14) is a zip, which Thunderstore publishes no checksum for, taken from its own hosts
/// only and unpacked into BepInEx's folder: its plugins and patchers each in a folder named after it, its configuration
/// only where there is none; what it wrote is recorded, and removed with it.
/// </summary>
public sealed class ModInstaller(IServerFiles files, HttpMessageHandler handler)
{
    public const string PackageRecords = ".pulsar/packages";

    /// <summary>A mod or plugin larger than this is not one.</summary>
    public const long MaximumFile = 256L * 1024 * 1024;

    private const long MaximumUnpacked = 1024L * 1024 * 1024;
    private const int MaximumEntries = 5000;

    private const string UmodHost = "umod.org";

    // The catalogs' own CDNs only: Modrinth's, CurseForge's (whose edge redirects to its file host), and uMod's site, which
    // serves its plugins itself (Pulsar 1.0.14).
    private static readonly HashSet<string> s_hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "edge.forgecdn.net", "mediafilez.forgecdn.net", UmodHost,
    };

    // Thunderstore's site, whose downloads redirect, and its CDNs.
    private static readonly HashSet<string> s_packageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "thunderstore.io", "ccdn.thunderstore.io", "gcdn.thunderstore.io",
    };

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <param name="folders">The folders the game's manifest puts catalog files into.</param>
    /// <param name="packageFolders">The BepInEx folders it unpacks packages into (Thunderstore's).</param>
    /// <exception cref="GameProcessException">With a <c>mods.*</c> code; nothing was changed unless the code is <c>mods.write_failed</c>.</exception>
    public async Task ChangeAsync(string serverDirectory, IReadOnlyDictionary<string, IReadOnlyList<string>> folders, IReadOnlyList<ModDownload> add,
        IReadOnlyList<string> remove, Func<string, double?, Task> progress, CancellationToken cancellationToken,
        IReadOnlySet<string>? packageFolders = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(progress);
        packageFolders ??= new HashSet<string>();
        if (add.Count + remove.Count is 0 || add.Count + remove.Count > ChangeModsCommand.MaxFiles)
        {
            throw Failed("mods.invalid", $"A change adds or removes 1 to {ChangeModsCommand.MaxFiles} files.");
        }

        foreach (var path in add.Select(a => a.Path).Concat(remove))
        {
            if (!ModFiles.IsPath(path, folders) && !ModFiles.IsPackagePath(path, packageFolders))
            {
                throw Failed("mods.invalid_path", $"'{path}' is not a file of the game's mod folders.");
            }
        }

        foreach (var file in add)
        {
            // A package's own folder takes a package, unpacked; a file's path takes a file.
            if ((file.Layout is not null) != ModFiles.IsPackagePath(file.Path, packageFolders)
                || (file.Layout is not (null or ModTargetDefinition.BepInExLayout)))
            {
                throw Failed("mods.invalid", $"'{file.Path}' is not where its {(file.Layout is null ? "file" : "package")} goes.");
            }
        }

        if (add.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != add.Count)
        {
            throw Failed("mods.invalid", "A file is added twice.");
        }

        // The panel replaces only the files it put there (those it removes in the same change): a file someone else put
        // in the folder under the same name stays theirs.
        var removed = new HashSet<string>(remove, StringComparer.OrdinalIgnoreCase);
        foreach (var path in add.Where(a => a.Layout is null).Select(a => a.Path).Where(p => !removed.Contains(p)))
        {
            if (Exists(serverDirectory, path))
            {
                throw Failed("mods.file_exists", $"'{path}' is already on the server, and the panel did not put it there: delete or rename it first.");
            }
        }

        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        // uMod gives a request that does not name its program one download a minute (Pulsar 1.0.14), others 30.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pulsar-Agent", "1"));
        var client = new VerifiedDownloader(http, s_hosts, "mods", "the mod catalog") { StallTimeout = StallTimeout };
        var packageClient = new VerifiedDownloader(http, s_packageHosts, "mods", "the mod catalog") { StallTimeout = StallTimeout };
        var downloaded = new List<(ModDownload File, FileStream Content)>();
        try
        {
            for (var i = 0; i < add.Count; i++)
            {
                var file = add[i];
                var package = file.Layout is not null;
                if (file.Size is < 0 or > MaximumFile || (file.Algorithm is null && !(package && file.Hash.Length == 0)))
                {
                    throw Failed("mods.invalid", $"'{file.Path}' has no SHA-512 or SHA-1, or is larger than a mod.");
                }

                var address = VerifiedDownloader.Https(file.Url, "mods");
                // uMod's site serves a plugin only as /plugins/<its file>: nothing else there is a mod (the panel checks it too).
                if (string.Equals(address.Host, UmodHost, StringComparison.OrdinalIgnoreCase)
                    && address.AbsolutePath != $"/plugins/{file.Path[(file.Path.LastIndexOf('/') + 1)..]}")
                {
                    throw Failed("mods.untrusted_host", $"'{file.Url}' is not a plugin's file on uMod.");
                }

                var index = i;
                downloaded.Add((file, await (package ? packageClient : client).DownloadAsync(address, file.Algorithm, file.Algorithm is null ? null : file.Hash,
                    percent => progress(Steam.InstallStages.Downloading, (index + percent / 100) * 100 / add.Count), cancellationToken, MaximumFile)));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            await DisposeAsync(downloaded);
            throw Failed("mods.download_failed", $"A mod could not be downloaded: {ex.Message}", ex);
        }
        catch
        {
            await DisposeAsync(downloaded);
            throw;
        }

        try
        {
            // Every package is read and checked before anything changes: what it would write, and that it takes nobody's file.
            var unpacking = new List<(ModDownload File, ZipArchive Zip, IReadOnlyList<(ZipArchiveEntry Entry, string Path, bool Config)> Entries)>();
            try
            {
                foreach (var (file, content) in downloaded.Where(d => d.File.Layout is not null))
                {
                    content.Position = 0;
                    await FileArchives.CheckDirectoryAsync(content, cancellationToken);
                    var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
                    unpacking.Add((file, zip, Plan(serverDirectory, file.Path, zip, removed)));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FileManagerException)
            {
                throw Failed("mods.bad_package", $"A package is not a zip Pulsar unpacks: {ex.Message}", ex);
            }

            await progress(Steam.InstallStages.Installing, null);
            try
            {
                foreach (var path in remove)
                {
                    if (ModFiles.IsPackagePath(path, packageFolders))
                    {
                        RemovePackage(serverDirectory, path);
                    }
                    else
                    {
                        Delete(serverDirectory, path);
                    }
                }

                foreach (var (file, content) in downloaded.Where(d => d.File.Layout is null))
                {
                    content.Position = 0;
                    files.Write(serverDirectory, file.Path, content);
                }

                foreach (var (file, _, entries) in unpacking)
                {
                    WritePackage(serverDirectory, file.Path, entries);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw Failed("mods.write_failed", $"The mods could not all be written: {ex.Message}", ex);
            }
            finally
            {
                foreach (var (_, zip, _) in unpacking)
                {
                    zip.Dispose();
                }
            }
        }
        finally
        {
            await DisposeAsync(downloaded);
        }
    }

    /// <summary>
    /// Where a package's file goes in BepInEx's folder <paramref name="folder"/>, by Thunderstore's layout (as mod managers
    /// unpack it): <c>plugins/</c> and <c>patchers/</c> into a folder named after the package, <c>config/</c> into BepInEx's
    /// own (<paramref name="config"/>), anything else beside its plugins; a leading <c>BepInEx/</c> is the same folder. Null
    /// for a folder's entry.
    /// </summary>
    public static string? PackageTarget(string folder, string package, string entry, out bool config)
    {
        ArgumentNullException.ThrowIfNull(entry);
        config = false;
        var name = entry.Replace('\\', '/');
        if (name.Length == 0 || name.EndsWith('/'))
        {
            return null;
        }

        if (name.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase))
        {
            name = name["BepInEx/".Length..];
        }

        var slash = name.IndexOf('/', StringComparison.Ordinal);
        var rest = slash < 0 ? name : name[(slash + 1)..];
        switch (slash < 0 ? "" : name[..slash].ToUpperInvariant())
        {
            case "PLUGINS":
                return $"{folder}/plugins/{package}/{rest}";
            case "PATCHERS":
                return $"{folder}/patchers/{package}/{rest}";
            case "CONFIG":
                config = true;
                return $"{folder}/config/{rest}";
            default:
                return $"{folder}/plugins/{package}/{name}";
        }
    }

    /// <summary>A package's files and where each goes, checked: plain relative paths, outside Pulsar's folder, within limits, nobody else's.</summary>
    private List<(ZipArchiveEntry Entry, string Path, bool Config)> Plan(string serverDirectory, string packagePath, ZipArchive zip,
        HashSet<string> removed)
    {
        var (folder, package) = ModFiles.SplitPackagePath(packagePath);
        var replacing = removed.Contains(packagePath);
        var record = replacing ? Record(serverDirectory, folder, package) : null;
        var ownedSet = new HashSet<string>(record?.Files ?? [], StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Path, bool Config)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (PackageTarget(folder, package, entry.FullName, out var config) is not { } path)
            {
                continue;
            }

            if (!ManifestValidator.IsValidRelativePath(path) || FileCommands.IsPulsarFolder(path) || !targets.Add(path))
            {
                throw Failed("mods.bad_package", $"{package} has an entry Pulsar does not unpack: '{entry.FullName}'.");
            }

            total += entry.Length;
            if (total > MaximumUnpacked || entries.Count >= MaximumEntries)
            {
                throw Failed("mods.bad_package", $"{package} is larger than Pulsar unpacks.");
            }

            // Its configuration goes only where there is none; any other file only where nobody else put one (the
            // package being replaced put those it recorded, or, its record lost, those in its own folders).
            if (!config && !ownedSet.Contains(path) && !(replacing && record is null && InOwnFolders(folder, package, path)) && Exists(serverDirectory, path))
            {
                throw Failed("mods.file_exists", $"'{path}' is already on the server, and the panel did not put it there: delete or rename it first.");
            }

            entries.Add((entry, path, config));
        }

        return entries;
    }

    /// <summary>Records what it writes before the first file, so a failure midway leaves a record its removal trusts.</summary>
    private void WritePackage(string serverDirectory, string packagePath, IReadOnlyList<(ZipArchiveEntry Entry, string Path, bool Config)> entries)
    {
        var (_, package) = ModFiles.SplitPackagePath(packagePath);
        var written = entries.Where(e => !e.Config || !Exists(serverDirectory, e.Path)).ToList();
        WriteRecord(serverDirectory, new PackageRecord(package, [.. written.Where(e => !e.Config).Select(e => e.Path)]));
        foreach (var (entry, path, _) in written)
        {
            using var content = BoundedEntryStream.Open(entry);
            files.Write(serverDirectory, path, content);
        }
    }

    /// <summary>
    /// Deletes the files the package's record lists (its configuration stays), then its folders once empty, then the
    /// record. With its record lost, the files right in its own folders go.
    /// </summary>
    private void RemovePackage(string serverDirectory, string packagePath)
    {
        var (folder, package) = ModFiles.SplitPackagePath(packagePath);
        var listed = Record(serverDirectory, folder, package)?.Files ?? Loose(serverDirectory, folder, package);
        foreach (var path in listed)
        {
            Delete(serverDirectory, path);
        }

        // Its folders, deepest first, as long as they are empty (a file someone else put there stays, with its folder).
        var directories = listed.SelectMany(Parents).Append($"{folder}/plugins/{package}").Append($"{folder}/patchers/{package}")
            .Where(d => InOwnFolders(folder, package, d + "/"))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Count(c => c == '/')).ToList();
        foreach (var directory in directories)
        {
            try
            {
                files.DeleteEmptyDirectory(serverDirectory, directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not empty, or not there: it stays.
            }
        }

        Delete(serverDirectory, RecordPath(package));

        static IEnumerable<string> Parents(string path)
        {
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                yield return path[..slash];
            }
        }
    }

    /// <summary>The files right in a package's own folders, for a package whose record is lost.</summary>
    private List<string> Loose(string serverDirectory, string folder, string package)
    {
        var loose = new List<string>();
        foreach (var directory in new[] { $"{folder}/plugins/{package}", $"{folder}/patchers/{package}" })
        {
            try
            {
                loose.AddRange(files.ListFiles(serverDirectory, directory, MaximumEntries).Select(f => $"{directory}/{f.Name}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not there, or not a folder Pulsar reads: nothing to delete there.
            }
        }

        return loose;
    }

    /// <summary>A path inside the package's own folders of plugins or patchers.</summary>
    private static bool InOwnFolders(string folder, string package, string path) =>
        path.StartsWith($"{folder}/plugins/{package}/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith($"{folder}/patchers/{package}/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Its record, keeping only paths in its own folders: the server's account can write <c>.pulsar</c>, not choose what the agent deletes.</summary>
    private PackageRecord? Record(string serverDirectory, string folder, string package)
    {
        try
        {
            using var stream = files.OpenRead(serverDirectory, RecordPath(package));
            if (stream is null || stream.Length > 4 * 1024 * 1024)
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<PackageRecord>(stream, s_json);
            return record is null ? null : record with
            {
                Files = [.. (record.Files ?? []).Where(p => p is not null && ManifestValidator.IsValidRelativePath(p) && InOwnFolders(folder, package, p))],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteRecord(string serverDirectory, PackageRecord record)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(record, s_json));
        files.Write(serverDirectory, RecordPath(record.Package), content);
    }

    private static string RecordPath(string package) => $"{PackageRecords}/{package}.json";

    private bool Exists(string serverDirectory, string path)
    {
        try
        {
            using var existing = files.OpenRead(serverDirectory, path);
            return existing is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // something is there, a link or a file in use: not a file to replace
        }
    }

    private void Delete(string serverDirectory, string path)
    {
        try
        {
            files.Delete(serverDirectory, path);
        }
        catch (DirectoryNotFoundException)
        {
            // Its folder is gone already.
        }
    }

    private static async Task DisposeAsync(List<(ModDownload File, FileStream Content)> downloaded)
    {
        foreach (var (_, content) in downloaded)
        {
            await content.DisposeAsync();
        }

        downloaded.Clear();
    }

    private static GameProcessException Failed(string code, string message, Exception? inner = null) => new(code, message, inner);
}
