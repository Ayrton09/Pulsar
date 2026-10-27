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

/// <summary>
/// Installs Microsoft's .NET runtime of a channel in a server's <see cref="DotnetRuntimeStep.Folder"/> folder
/// (<see cref="DotnetRuntimeStep"/>, Pulsar 1.0.12): the channel's latest runtime from Microsoft's release metadata, its
/// Windows x64 zip checked against the SHA-512 the metadata lists, all from Microsoft's download host. The folder holds that
/// runtime's files and <see cref="Marker"/>, which lists them: an update removes the previous runtime's. Every write goes
/// through the agent's link-safe file access.
/// </summary>
public sealed partial class DotnetRuntimeInstaller(IServerFiles files, HttpMessageHandler handler)
{
    /// <summary>The installed runtime's version, then its files.</summary>
    public const string Marker = DotnetRuntimeStep.Folder + "/pulsar-dotnet.txt";

    /// <summary>Microsoft's host for the release metadata and the runtimes (no redirect leaves it).</summary>
    public const string Host = "builds.dotnet.microsoft.com";

    private const string RuntimeZip = "dotnet-runtime-win-x64.zip";
    private const string Muxer = "dotnet.exe";
    private const long MaximumExtracted = 512L * 1024 * 1024;
    private const int MaximumEntries = 5_000;
    private const int MaximumMarkerSize = 1024 * 1024; // a runtime lists about 250 files

    private static readonly HashSet<string> s_hosts = new(StringComparer.OrdinalIgnoreCase) { Host };

    /// <summary>A download that stops sending for this long is abandoned.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Microsoft's release metadata of <paramref name="channel"/> ("9.0").</summary>
    public static Uri MetadataUri(string channel) => new($"https://{Host}/dotnet/release-metadata/{channel}/releases.json");

    /// <summary>
    /// Installs <paramref name="channel"/>'s latest runtime, unless it is the one installed; returns its version.
    /// </summary>
    /// <exception cref="GameProcessException">With a <c>dotnet.*</c> code.</exception>
    public async Task<string> InstallAsync(string serverDirectory, string channel, Func<string, double?, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(progress);
        using var http = Client();
        var client = Downloader(http);
        try
        {
            await progress(InstallStages.Preparing, null);
            var latest = await LatestAsync(client, channel, cancellationToken);
            var installed = ReadMarker(serverDirectory);
            if (installed?.Version == latest.Version && files.OpenRead(serverDirectory, $"{DotnetRuntimeStep.Folder}/{Muxer}") is { } existing)
            {
                existing.Dispose();
                return latest.Version;
            }

            await using var download = await client.DownloadAsync(VerifiedDownloader.Https(latest.Url, "dotnet"), HashAlgorithmName.SHA512, latest.Sha512,
                percent => progress(InstallStages.Downloading, percent), cancellationToken);
            await progress(InstallStages.Installing, null);
            using var archive = new ZipArchive(download, ZipArchiveMode.Read);
            var entries = Entries(archive);

            // The marker goes first: a removal or an unpacking that fails midway leaves no runtime a retry takes as installed.
            Delete(serverDirectory, Marker);
            var current = entries.Select(e => e.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = (installed?.Files ?? []).Where(f => !current.Contains(f)).ToList();
            foreach (var old in stale)
            {
                Delete(serverDirectory, $"{DotnetRuntimeStep.Folder}/{old}");
            }

            // Folders the previous runtime leaves empty (its shared/Microsoft.NETCore.App/<version>), deepest first.
            foreach (var folder in stale.SelectMany(Parents).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length))
            {
                files.DeleteEmptyDirectory(serverDirectory, $"{DotnetRuntimeStep.Folder}/{folder}");
            }

            foreach (var (entry, relative) in entries)
            {
                await using var content = BoundedEntryStream.Open(entry);
                files.Write(serverDirectory, $"{DotnetRuntimeStep.Folder}/{relative}", content);
            }

            var marker = new StringBuilder().Append(latest.Version).Append('\n');
            foreach (var (_, relative) in entries)
            {
                marker.Append(relative).Append('\n');
            }

            using var markerContent = new MemoryStream(Encoding.UTF8.GetBytes(marker.ToString()));
            files.Write(serverDirectory, Marker, markerContent);
            return latest.Version;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Failed("dotnet.download_failed", $"The .NET {channel} runtime could not be installed: {ex.Message}", ex);
        }
    }

    /// <summary>Whether <paramref name="channel"/>'s latest runtime is another than the installed one (an update check).</summary>
    /// <exception cref="GameProcessException">With a <c>dotnet.*</c> code.</exception>
    public async Task<GameBuild> CheckAsync(string serverDirectory, string channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        using var http = Client();
        try
        {
            var latest = await LatestAsync(Downloader(http), channel, cancellationToken);
            var installed = ReadMarker(serverDirectory)?.Version;
            return new GameBuild(installed is null ? null : $".NET {installed}", $".NET {latest.Version}", installed != latest.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Failed("dotnet.download_failed", $"Microsoft's .NET {channel} releases could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>The channel's latest runtime and its Windows x64 zip, as Microsoft's metadata lists them.</summary>
    private static async Task<(string Version, string Url, string Sha512)> LatestAsync(VerifiedDownloader client, string channel,
        CancellationToken cancellationToken)
    {
        var metadata = await client.GetJsonAsync<ReleaseIndex>(MetadataUri(channel), sha1: null, cancellationToken);
        // The version goes into the marker and the console: only one shaped as the channel's ("9.0.22", "10.0.0-rc.2...").
        if (metadata.LatestRuntime is not { } version || !VersionPattern().IsMatch(version)
            || !version.StartsWith(channel[..^1], StringComparison.Ordinal))
        {
            throw Failed("dotnet.no_runtime", $"Microsoft lists no runtime for .NET {channel}.");
        }

        var file = (metadata.Releases ?? []).Where(r => r.Runtime?.Version == version).SelectMany(r => r.Runtime!.Files ?? [])
            .FirstOrDefault(f => f.Rid == "win-x64" && f.Name == RuntimeZip);
        if (file is not { Url: { } url, Hash: { } hash } || !Sha512Pattern().IsMatch(hash))
        {
            throw Failed("dotnet.no_runtime", $"Microsoft lists no Windows x64 runtime with a checksum for .NET {version}.");
        }

        return (version, url, hash);
    }

    /// <summary>The archive's files, which are the runtime's folder (<c>dotnet.exe</c>, <c>host/</c>, <c>shared/</c>).</summary>
    private static List<(ZipArchiveEntry Entry, string Relative)> Entries(ZipArchive archive)
    {
        var entries = new List<(ZipArchiveEntry, string)>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.EndsWith('/'))
            {
                continue;
            }

            if (!ManifestValidator.IsValidRelativePath($"{DotnetRuntimeStep.Folder}/{relative}") || $"{DotnetRuntimeStep.Folder}/{relative}".Equals(Marker, StringComparison.OrdinalIgnoreCase))
            {
                throw Failed("dotnet.bad_archive", $"The .NET runtime archive has an unexpected entry '{entry.FullName}'.");
            }

            total += entry.Length;
            if (total > MaximumExtracted || entries.Count >= MaximumEntries)
            {
                throw Failed("dotnet.bad_archive", "The .NET runtime archive is larger than expected.");
            }

            entries.Add((entry, relative));
        }

        return entries.Any(e => e.Item2 == Muxer)
            ? entries
            : throw Failed("dotnet.bad_archive", $"The .NET runtime archive does not contain {Muxer}.");
    }

    private (string Version, IReadOnlyList<string> Files)? ReadMarker(string serverDirectory)
    {
        using var stream = files.OpenRead(serverDirectory, Marker);
        // The server's account can write it: one larger than a runtime's list of files is not read, and counts as none.
        if (stream is null || stream.Length > MaximumMarkerSize)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0
            ? null
            : (lines[0], [.. lines.Skip(1).Where(l => ManifestValidator.IsValidRelativePath($"{DotnetRuntimeStep.Folder}/{l}"))]);
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

    private static IEnumerable<string> Parents(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            yield return path[..slash];
        }
    }

    private HttpClient Client()
    {
        var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pulsar-Agent", "1"));
        return http;
    }

    private VerifiedDownloader Downloader(HttpClient http) => new(http, s_hosts, "dotnet", ".NET") { StallTimeout = StallTimeout };

    private static GameProcessException Failed(string code, string message, Exception? inner = null) => new(code, message, inner);

    [GeneratedRegex(@"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,4}(-[A-Za-z0-9.]{1,40})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[0-9A-Fa-f]{128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha512Pattern();

    private sealed record ReleaseIndex(
        [property: JsonPropertyName("latest-runtime")] string? LatestRuntime,
        IReadOnlyList<Release>? Releases);

    private sealed record Release(RuntimeRelease? Runtime);

    private sealed record RuntimeRelease(string? Version, IReadOnlyList<RuntimeFile>? Files);

    private sealed record RuntimeFile(string? Name, string? Rid, string? Url, string? Hash);
}
