using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>What a <see cref="MinecraftServerStep"/> installs: the version, the server type (Mojang's own when none) and its loader's version.</summary>
public sealed record MinecraftChoice(string? Version, string? Type = null, string? Loader = null)
{
    public string TypeOrVanilla => string.IsNullOrEmpty(Type) ? MinecraftServerStep.Vanilla : Type;
}

/// <summary>
/// Runs the server's Java runtime (<c>java/bin/java.exe</c>) with <paramref name="arguments"/>, as the server's account
/// in its folder, its output in the server's console.
/// </summary>
/// <exception cref="GameProcessException">It could not start, exited with an error, or stalled.</exception>
public delegate Task JavaRunner(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// The <see cref="MinecraftServerStep"/> (docs/architecture/04, "Install/update steps"): Mojang's <c>server.jar</c> for
/// the chosen version and the Eclipse Temurin runtime that version asks for, in <c>java/</c>. Only HTTPS from the
/// publishers' own hosts, redirects included, and every download is checked against the checksum its publisher lists
/// in the metadata fetched over that same TLS: SHA-1 for Mojang (the only one it publishes), SHA-256 for Adoptium.
/// Other server types (Pulsar 1.0.9) are in MinecraftLoaders.cs.
/// </summary>
/// <param name="java">Runs Forge's and NeoForge's installers; without one, those types cannot be installed.</param>
public sealed partial class MinecraftInstaller(IServerFiles files, HttpMessageHandler handler, JavaRunner? java = null)
{
    /// <summary>Mojang's list of versions (the launcher's own source).</summary>
    public static readonly Uri VersionManifestUrl = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    public const string ServerJar = "server.jar";

    public const string JavaDirectory = "java";

    /// <summary>The runtime's release and the files it installed, so an update replaces exactly those.</summary>
    private const string JavaMarker = JavaDirectory + "/pulsar-java.txt";

    /// <summary>The version of <see cref="ServerJar"/> and its SHA-1, as installed (Pulsar 1.0.9): what an update check names.</summary>
    public const string VersionMarker = ".pulsar/minecraft-version.txt";

    // A server.jar is tens of megabytes: a larger file is not Mojang's, and is not hashed.
    private const long MaximumJarSize = 1024L * 1024 * 1024;

    private const long MaximumExtracted = 2L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private const int MaximumMarkerSize = 1024 * 1024; // a runtime lists about 300 files of a few dozen characters

    // GitHub serves Adoptium's binaries: github.com redirects to its asset hosts. Then each server type's publisher.
    private static readonly HashSet<string> s_hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "piston-meta.mojang.com", "piston-data.mojang.com", "launchermeta.mojang.com", "launcher.mojang.com",
        "api.adoptium.net", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        "fill.papermc.io", "fill-data.papermc.io", "api.purpurmc.org", "meta.fabricmc.net", "maven.fabricmc.net",
        "files.minecraftforge.net", "maven.minecraftforge.net", "maven.neoforged.net",
    };

    /// <summary>A download that stops sending for this long is abandoned.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <param name="version">A Mojang version id, or null or <see cref="MinecraftServerStep.Latest"/> for the latest release.</param>
    /// <param name="progress">Stage and percent, as SteamCMD reports them.</param>
    /// <exception cref="GameProcessException">With a <c>minecraft.*</c> code.</exception>
    public Task InstallAsync(string serverDirectory, string? version, Func<string, double?, Task> progress, CancellationToken cancellationToken) =>
        InstallAsync(serverDirectory, new MinecraftChoice(version), progress, cancellationToken);

    /// <param name="progress">Stage and percent, as SteamCMD reports them.</param>
    /// <exception cref="GameProcessException">With a <c>minecraft.*</c> code.</exception>
    public async Task InstallAsync(string serverDirectory, MinecraftChoice choice, Func<string, double?, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(progress);
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new VerifiedDownloader(http, s_hosts, "minecraft", "Minecraft or Java") { StallTimeout = StallTimeout };
        try
        {
            await progress(InstallStages.Preparing, null);
            if (choice.TypeOrVanilla == MinecraftServerStep.Vanilla)
            {
                await InstallVanillaAsync(client, serverDirectory, choice.Version, progress, cancellationToken);
                return;
            }

            // The runtime first: Forge's and NeoForge's installers run on it.
            var target = await ResolveAsync(client, choice, cancellationToken);
            var (_, details) = await MojangAsync(client, target.Version, cancellationToken);
            await InstallJavaAsync(client, serverDirectory, details.JavaVersion?.MajorVersion ?? 8,
                percent => progress(InstallStages.Downloading, percent / 2), cancellationToken);
            await InstallTypeAsync(client, serverDirectory, target, percent => progress(InstallStages.Downloading, 50 + percent / 2), cancellationToken);
            await progress(InstallStages.Installing, 100);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Failed("minecraft.download_failed", $"Minecraft could not be installed: {ex.Message}", ex);
        }
    }

    private async Task InstallVanillaAsync(VerifiedDownloader client, string serverDirectory, string? version, Func<string, double?, Task> progress,
        CancellationToken cancellationToken)
    {
        var (id, details) = await MojangAsync(client, version, cancellationToken);
        var server = details.Downloads?.Server is { Url: not null, Sha1: not null } download
            ? download
            : throw Failed("minecraft.no_server", $"Minecraft {id} has no dedicated server download.");

        await using (var jar = await DownloadAsync(client, Https(server.Url!), HashAlgorithmName.SHA1, server.Sha1,
                         percent => progress(InstallStages.Downloading, percent / 2), cancellationToken))
        {
            await progress(InstallStages.Installing, 50);
            files.Write(serverDirectory, ServerJar, jar);
        }

        WriteMarker(serverDirectory, new LoaderTarget(MinecraftServerStep.Vanilla, id, ""), server.Sha1!.ToLowerInvariant());
        await InstallJavaAsync(client, serverDirectory, details.JavaVersion?.MajorVersion ?? 8,
            percent => progress(InstallStages.Downloading, 50 + percent / 2), cancellationToken);
    }

    /// <summary>Mojang's details of a version (the latest release for null or "latest"), checked against Mojang's list.</summary>
    private static async Task<(string Id, VersionDetails Details)> MojangAsync(VerifiedDownloader client, string? version, CancellationToken cancellationToken)
    {
        var versions = await GetJsonAsync<VersionManifest>(client, VersionManifestUrl, sha1: null, cancellationToken);
        var id = string.IsNullOrEmpty(version) || version == MinecraftServerStep.Latest ? versions.Latest?.Release : version;
        var entry = versions.Versions?.FirstOrDefault(v => v.Id == id && v.Url is not null)
            ?? throw Failed("minecraft.unknown_version", $"Mojang does not list a Minecraft version '{id}'.");
        // The details name the server's download and its checksum: they are used only when they match Mojang's list.
        var sha1 = entry.Sha1 ?? throw Failed("minecraft.no_checksum", $"Mojang lists no checksum for Minecraft {id}.");
        return (id!, await GetJsonAsync<VersionDetails>(client, Https(entry.Url!), sha1, cancellationToken));
    }

    /// <summary>
    /// What was installed, for update checks: Mojang's version and the main file's checksum, then (other types) the type
    /// and its build. Mojang's own servers keep the two lines of Pulsar 1.0.9's first update checks.
    /// </summary>
    private void WriteMarker(string serverDirectory, LoaderTarget target, string checksum)
    {
        var text = target.Type == MinecraftServerStep.Vanilla
            ? $"{target.Version}\n{checksum}\n"
            : $"{target.Version}\n{checksum}\n{target.Type}\n{target.Build}\n";
        using var marker = new MemoryStream(Encoding.UTF8.GetBytes(text));
        files.Write(serverDirectory, VersionMarker, marker);
    }

    /// <summary>
    /// Whether an update would change <see cref="ServerJar"/> (an update check, Pulsar 1.0.9): Mojang's checksum of the
    /// version the server follows (the latest release, or the one chosen) against the installed file's. The installed
    /// version is named when the file is that version or the one the installer recorded.
    /// </summary>
    /// <exception cref="GameProcessException">With a <c>minecraft.*</c> code.</exception>
    public Task<GameBuild> CheckAsync(string serverDirectory, string? version, CancellationToken cancellationToken) =>
        CheckAsync(serverDirectory, new MinecraftChoice(version), cancellationToken);

    /// <summary>
    /// Whether an update would install something new (an update check, Pulsar 1.0.9): for Mojang's server, its checksum of
    /// the version the server follows against the installed jar's; for Paper and Purpur, the newest build's checksum against
    /// the jar's; for Fabric, Mojang's jar and the loader; for Forge and NeoForge, the build the installer recorded.
    /// </summary>
    /// <exception cref="GameProcessException">With a <c>minecraft.*</c> code.</exception>
    public async Task<GameBuild> CheckAsync(string serverDirectory, MinecraftChoice choice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new VerifiedDownloader(http, s_hosts, "minecraft", "Minecraft or Java") { StallTimeout = StallTimeout };
        try
        {
            var type = choice.TypeOrVanilla;
            var recorded = ReadVersionMarker(serverDirectory);
            if (type is MinecraftServerStep.Vanilla or MinecraftServerStep.Fabric)
            {
                var target = type == MinecraftServerStep.Vanilla
                    ? null
                    : await ResolveAsync(client, choice, cancellationToken);
                var (id, details) = await MojangAsync(client, target?.Version ?? choice.Version, cancellationToken);
                var latest = details.Downloads?.Server?.Sha1 ?? throw Failed("minecraft.no_server", $"Minecraft {id} has no dedicated server download.");
                var installed = await JarHashAsync(serverDirectory, HashAlgorithmName.SHA1, cancellationToken);
                var same = installed is not null && string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase)
                    && (target is null || (recorded?.Type == type && recorded.Build == target.Build));
                var named = recorded is not null && recorded.Type == type && recorded.Checksum == installed ? recorded : null;
                var wanted = target ?? new LoaderTarget(type, id, "");
                return new GameBuild(same ? Label(wanted) : named is null ? null : Label(named), Label(wanted), !same);
            }

            var resolved = await ResolveAsync(client, choice, cancellationToken);
            if (type is MinecraftServerStep.Paper or MinecraftServerStep.Purpur)
            {
                var installed = await JarHashAsync(serverDirectory, resolved.Algorithm!.Value, cancellationToken);
                var same = installed is not null && string.Equals(installed, resolved.Checksum, StringComparison.OrdinalIgnoreCase);
                var named = recorded is not null && recorded.Type == type && recorded.Checksum == installed ? recorded : null;
                return new GameBuild(same ? Label(resolved) : named is null ? null : Label(named), Label(resolved), !same);
            }

            // Forge and NeoForge: what the installer recorded (their libraries are many files, written by the installer).
            var current = recorded is not null && recorded.Type == type ? recorded : null;
            var upToDate = current is not null && current.Version == resolved.Version && current.Build == resolved.Build;
            return new GameBuild(current is null ? null : Label(current), Label(resolved), !upToDate);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Failed("minecraft.check_failed", $"The latest Minecraft server could not be looked up: {ex.Message}", ex);
        }
    }

    /// <summary>"26.3", "26.3 (Paper 223)", "1.21.8 (Forge 58.1.26)": how a check names what is installed and what is out.</summary>
    private static string Label(LoaderTarget target) => target.Type switch
    {
        MinecraftServerStep.Vanilla => target.Version,
        MinecraftServerStep.Paper => $"{target.Version} (Paper {target.Build})",
        MinecraftServerStep.Purpur => $"{target.Version} (Purpur {target.Build})",
        MinecraftServerStep.Fabric => $"{target.Version} (Fabric {target.Build})",
        MinecraftServerStep.Forge => $"{target.Version} (Forge {target.Build})",
        _ => $"{target.Version} (NeoForge {target.Build})",
    };

    private static string Label(Marker marker) => Label(new LoaderTarget(marker.Type, marker.Version, marker.Build));

    /// <summary>The installed server.jar's checksum (lowercase hex); null when there is none, or it is far larger than a server's.</summary>
    private async Task<string?> JarHashAsync(string serverDirectory, HashAlgorithmName algorithm, CancellationToken cancellationToken)
    {
        await using var jar = files.OpenRead(serverDirectory, ServerJar);
        if (jar is null || jar.Length > MaximumJarSize)
        {
            return null;
        }

        // The checksum each publisher lists: SHA-1 for Mojang, SHA-256 for Paper, MD5 for Purpur.
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[81920];
        int read;
        while ((read = await jar.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>What <see cref="VersionMarker"/> records; <see cref="Build"/> is empty for Mojang's own server.</summary>
    private sealed record Marker(string Version, string Checksum, string Type, string Build);

    /// <summary>
    /// What the installer recorded. The server's account can write the file: anything but a version, a checksum (or "-")
    /// and, for another type, a known type and a build counts as none.
    /// </summary>
    private Marker? ReadVersionMarker(string serverDirectory)
    {
        using var stream = files.OpenRead(serverDirectory, VersionMarker);
        if (stream is null || stream.Length > 512)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var marker = lines switch
        {
            [var version, var checksum] => new Marker(version, checksum, MinecraftServerStep.Vanilla, ""),
            [var version, var checksum, var type, var build] when type != MinecraftServerStep.Vanilla => new Marker(version, checksum, type, build),
            _ => null,
        };
        return marker is not null && IsRecorded(marker.Version) && MinecraftServerStep.Types.Contains(marker.Type)
            && (marker.Build.Length == 0 ? marker.Type == MinecraftServerStep.Vanilla : IsRecorded(marker.Build))
            && (marker.Checksum == "-" || (marker.Checksum.Length is > 0 and <= 128 && marker.Checksum.All(char.IsAsciiHexDigitLower)))
            && Label(marker).Length <= GameUpdateCheck.MaxBuildLength
            ? marker
            : null;
    }

    private static bool IsRecorded(string text) => text.Length is > 0 and <= 40 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    private async Task InstallJavaAsync(VerifiedDownloader client, string serverDirectory, int major, Func<double, Task> progress,
        CancellationToken cancellationToken)
    {
        var assets = await GetJsonAsync<AdoptiumAsset[]>(client,
            new Uri($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse"),
            sha1: null, cancellationToken);
        var (release, package) = assets.Select(a => (a.ReleaseName, a.Binary?.Package))
            .FirstOrDefault(a => a is { ReleaseName: not null, Package: { Link: not null, Checksum: not null } });
        if (release is null || package is null)
        {
            throw Failed("minecraft.no_java", $"Adoptium has no Java {major} runtime for Windows x64.");
        }

        var installed = ReadMarker(serverDirectory);
        if (installed?.Release == release && files.OpenRead(serverDirectory, $"{JavaDirectory}/bin/java.exe") is { } existing)
        {
            existing.Dispose();
            return; // already there: an update of the same Minecraft version downloads only server.jar
        }

        await using var download = await DownloadAsync(client, Https(package.Link!), HashAlgorithmName.SHA256, package.Checksum!, progress,
            cancellationToken);
        using var archive = new ZipArchive(download, ZipArchiveMode.Read);
        var entries = Entries(archive);

        await progress(100);
        foreach (var old in (installed?.Files ?? []).Append("pulsar-java.txt"))
        {
            try
            {
                files.Delete(serverDirectory, $"{JavaDirectory}/{old}");
            }
            catch (DirectoryNotFoundException)
            {
                // already gone with its folder
            }
        }
        foreach (var (entry, relative) in entries)
        {
            await using var content = BoundedEntryStream.Open(entry);
            files.Write(serverDirectory, $"{JavaDirectory}/{relative}", content);
        }

        var marker = new StringBuilder().Append(release).Append('\n');
        foreach (var (_, relative) in entries)
        {
            marker.Append(relative).Append('\n');
        }

        using var markerContent = new MemoryStream(Encoding.UTF8.GetBytes(marker.ToString()));
        files.Write(serverDirectory, JavaMarker, markerContent);
    }

    /// <summary>The archive's files without its top folder (<c>jdk-25.0.4.1+1-jre/bin/java.exe</c> → <c>bin/java.exe</c>).</summary>
    private static List<(ZipArchiveEntry Entry, string Relative)> Entries(ZipArchive archive)
    {
        var entries = new List<(ZipArchiveEntry, string)>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue;
            }

            var slash = name.IndexOf('/', StringComparison.Ordinal);
            var relative = slash < 0 ? null : name[(slash + 1)..];
            if (relative is null || !ManifestValidator.IsValidRelativePath($"{JavaDirectory}/{relative}") || relative == "pulsar-java.txt")
            {
                throw Failed("minecraft.bad_archive", $"The Java runtime archive has an unexpected entry '{entry.FullName}'.");
            }

            total += entry.Length;
            if (total > MaximumExtracted || entries.Count >= MaximumEntries)
            {
                throw Failed("minecraft.bad_archive", "The Java runtime archive is larger than expected.");
            }

            entries.Add((entry, relative));
        }

        return entries.Any(e => e.Item2 == "bin/java.exe")
            ? entries
            : throw Failed("minecraft.bad_archive", "The Java runtime archive does not contain bin/java.exe.");
    }

    private (string Release, IReadOnlyList<string> Files)? ReadMarker(string serverDirectory)
    {
        using var stream = files.OpenRead(serverDirectory, JavaMarker);
        // The server's account can write it: one larger than a runtime's list of files is not read, and counts as none.
        if (stream is null || stream.Length > MaximumMarkerSize)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0
            ? null
            : (lines[0], [.. lines.Skip(1).Where(l => ManifestValidator.IsValidRelativePath($"{JavaDirectory}/{l}"))]);
    }

    private static Task<T> GetJsonAsync<T>(VerifiedDownloader client, Uri uri, string? sha1, CancellationToken cancellationToken) =>
        client.GetJsonAsync<T>(uri, sha1, cancellationToken);

    private static Task<FileStream> DownloadAsync(VerifiedDownloader client, Uri uri, HashAlgorithmName? algorithm, string? expected,
        Func<double, Task> progress, CancellationToken cancellationToken) =>
        client.DownloadAsync(uri, algorithm, expected, progress, cancellationToken);

    private static Uri Https(string url) => VerifiedDownloader.Https(url, "minecraft");

    private static GameProcessException Failed(string code, string message, Exception? inner = null) => new(code, message, inner);

    private sealed record VersionManifest(LatestVersions? Latest, IReadOnlyList<VersionEntry>? Versions);

    private sealed record LatestVersions(string? Release);

    private sealed record VersionEntry(string? Id, string? Url, string? Sha1);

    private sealed record VersionDetails(VersionDownloads? Downloads, JavaVersionInfo? JavaVersion);

    private sealed record VersionDownloads(Download? Server);

    private sealed record Download(string? Url, string? Sha1, long? Size);

    private sealed record JavaVersionInfo(int MajorVersion);

    private sealed record AdoptiumAsset([property: JsonPropertyName("release_name")] string? ReleaseName, AdoptiumBinary? Binary);

    private sealed record AdoptiumBinary(AdoptiumPackage? Package);

    private sealed record AdoptiumPackage(string? Link, string? Checksum, long? Size);
}
