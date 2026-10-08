using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// The <see cref="MinecraftServerStep"/> (docs/architecture/04, "Install/update steps"): Mojang's <c>server.jar</c> for
/// the chosen version and the Eclipse Temurin runtime that version asks for, in <c>java/</c>. Only HTTPS from the
/// publishers' own hosts, redirects included, and every download is checked against the checksum its publisher lists
/// in the metadata fetched over that same TLS: SHA-1 for Mojang (the only one it publishes), SHA-256 for Adoptium.
/// </summary>
public sealed class MinecraftInstaller(IServerFiles files, HttpMessageHandler handler)
{
    /// <summary>Mojang's list of versions (the launcher's own source).</summary>
    public static readonly Uri VersionManifestUrl = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    public const string ServerJar = "server.jar";

    public const string JavaDirectory = "java";

    /// <summary>The runtime's release and the files it installed, so an update replaces exactly those.</summary>
    private const string JavaMarker = JavaDirectory + "/pulsar-java.txt";

    private const long MaximumExtracted = 2L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 20_000;

    // GitHub serves Adoptium's binaries: github.com redirects to its asset hosts.
    private static readonly HashSet<string> s_hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "piston-meta.mojang.com", "piston-data.mojang.com", "launchermeta.mojang.com", "launcher.mojang.com",
        "api.adoptium.net", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    /// <summary>A download that stops sending for this long is abandoned.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <param name="version">A Mojang version id, or null or <see cref="MinecraftServerStep.Latest"/> for the latest release.</param>
    /// <param name="progress">Stage and percent, as SteamCMD reports them.</param>
    /// <exception cref="GameProcessException">With a <c>minecraft.*</c> code.</exception>
    public async Task InstallAsync(string serverDirectory, string? version, Func<string, double?, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new VerifiedDownloader(http, s_hosts, "minecraft", "Minecraft or Java") { StallTimeout = StallTimeout };
        try
        {
            await progress(InstallStages.Preparing, null);
            var versions = await GetJsonAsync<VersionManifest>(client, VersionManifestUrl, sha1: null, cancellationToken);
            var id = string.IsNullOrEmpty(version) || version == MinecraftServerStep.Latest ? versions.Latest?.Release : version;
            var entry = versions.Versions?.FirstOrDefault(v => v.Id == id && v.Url is not null)
                ?? throw Failed("minecraft.unknown_version", $"Mojang does not list a Minecraft version '{id}'.");
            // The details name the server's download and its checksum: they are used only when they match Mojang's list.
            var sha1 = entry.Sha1 ?? throw Failed("minecraft.no_checksum", $"Mojang lists no checksum for Minecraft {id}.");
            var details = await GetJsonAsync<VersionDetails>(client, Https(entry.Url!), sha1, cancellationToken);
            var server = details.Downloads?.Server is { Url: not null, Sha1: not null } download
                ? download
                : throw Failed("minecraft.no_server", $"Minecraft {id} has no dedicated server download.");

            await using (var jar = await DownloadAsync(client, Https(server.Url!), HashAlgorithmName.SHA1, server.Sha1,
                             percent => progress(InstallStages.Downloading, percent / 2), cancellationToken))
            {
                await progress(InstallStages.Installing, 50);
                files.Write(serverDirectory, ServerJar, jar);
            }

            await InstallJavaAsync(client, serverDirectory, details.JavaVersion?.MajorVersion ?? 8,
                percent => progress(InstallStages.Downloading, 50 + percent / 2), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Failed("minecraft.download_failed", $"Minecraft could not be installed: {ex.Message}", ex);
        }
    }

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
            await using var content = entry.Open();
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
        if (stream is null)
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
