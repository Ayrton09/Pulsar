using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// The <see cref="MinecraftBedrockStep"/> (docs/architecture/04, "Install/update steps"): Mojang's Windows build of
/// Minecraft: Bedrock Edition's dedicated server, unpacked into the server directory. Mojang lists its downloads but no
/// checksum, so the agent asks only Mojang's own hosts over HTTPS (redirects included), accepts only an address of the
/// form Mojang uses, and installs <c>bedrock_server.exe</c> only with a valid Authenticode signature of Microsoft's,
/// checked on a copy in the agent's own temporary folder before it is put in the server's. An update leaves the
/// server's settings files (<see cref="MinecraftBedrockStep.KeptFiles"/>) and its worlds as they are, and a version
/// already installed is not downloaded again.
/// </summary>
public sealed partial class MinecraftBedrockInstaller(IServerFiles files, HttpMessageHandler handler, ICodeSignatures signatures)
{
    /// <summary>Mojang's list of the current downloads (what minecraft.net's download page reads).</summary>
    public static readonly Uri LinksUrl = new("https://net-secondary.web.minecraft-services.net/api/v1.0/download/links");

    public const string Executable = "bedrock_server.exe";

    /// <summary>The organization that signs <see cref="Executable"/>.</summary>
    public const string Signer = "Microsoft Corporation";

    /// <summary>The version installed, so an update of the same version downloads nothing.</summary>
    public const string VersionMarker = ".pulsar/bedrock-version.txt";

    private const long MaximumDownload = 1024L * 1024 * 1024;
    private const long MaximumExtracted = 4L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private const string WindowsDownload = "serverBedrockWindows";

    private static readonly HashSet<string> s_hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "net-secondary.web.minecraft-services.net", "www.minecraft.net",
    };

    /// <summary>A download that stops sending for this long is abandoned.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <param name="version">A Bedrock version ("1.21.50.07"), or null or <see cref="MinecraftBedrockStep.Latest"/> for the latest release.</param>
    /// <param name="progress">Stage and percent, as SteamCMD reports them.</param>
    /// <exception cref="GameProcessException">With a <c>bedrock.*</c> code.</exception>
    public async Task InstallAsync(string serverDirectory, string? version, Func<string, double?, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new VerifiedDownloader(http, s_hosts, "bedrock", "the Minecraft Bedrock server") { StallTimeout = StallTimeout };
        try
        {
            await progress(InstallStages.Preparing, null);
            var download = string.IsNullOrEmpty(version) || version == MinecraftBedrockStep.Latest
                ? await LatestAsync(client, cancellationToken)
                : AddressOf(version);
            var id = VersionOf(download) ?? throw Failed("bedrock.unexpected_link", "Mojang listed a Bedrock server download Pulsar does not recognize.");
            if (ReadMarker(serverDirectory) == id && files.OpenRead(serverDirectory, Executable) is { } installed)
            {
                installed.Dispose();
                return; // already this version: nothing to download
            }

            await using var archiveFile = await client.DownloadAsync(download, null, null, percent => progress(InstallStages.Downloading, percent),
                cancellationToken, MaximumDownload);
            using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read);
            var entries = Entries(archive);
            var executable = entries.Single(e => e.Relative == Executable).Entry;

            await progress(InstallStages.Verifying, null);
            var verified = Path.GetTempFileName();
            try
            {
                await using (var copy = File.Create(verified))
                await using (var content = BoundedEntryStream.Open(executable))
                {
                    await content.CopyToAsync(copy, cancellationToken);
                }

                if (!signatures.IsSignedBy(verified, Signer))
                {
                    throw Failed("bedrock.untrusted", $"The downloaded {Executable} is not signed by {Signer}; it was not installed.");
                }

                await progress(InstallStages.Installing, null);
                foreach (var (entry, relative) in entries)
                {
                    if (relative == Executable)
                    {
                        await using var checkedCopy = File.OpenRead(verified);
                        files.Write(serverDirectory, relative, checkedCopy);
                        continue;
                    }

                    if (MinecraftBedrockStep.KeptFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)
                        && files.OpenRead(serverDirectory, relative) is { } existing)
                    {
                        existing.Dispose();
                        continue; // the server's own settings
                    }

                    await using var content = BoundedEntryStream.Open(entry);
                    files.Write(serverDirectory, relative, content);
                }
            }
            finally
            {
                File.Delete(verified);
            }

            using var marker = new MemoryStream(Encoding.UTF8.GetBytes(id));
            files.Write(serverDirectory, VersionMarker, marker);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Failed("bedrock.download_failed", $"The Minecraft Bedrock server could not be installed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Whether an update would install another version (an update check, Pulsar 1.0.9): the version the server follows
    /// (Mojang's latest, or the one chosen) against the one this installer recorded.
    /// </summary>
    /// <exception cref="GameProcessException">With a <c>bedrock.*</c> code.</exception>
    public async Task<GameBuild> CheckAsync(string serverDirectory, string? version, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new VerifiedDownloader(http, s_hosts, "bedrock", "the Minecraft Bedrock server") { StallTimeout = StallTimeout };
        try
        {
            var latest = string.IsNullOrEmpty(version) || version == MinecraftBedrockStep.Latest
                ? VersionOf(await LatestAsync(client, cancellationToken))!
                : VersionOf(AddressOf(version))!;
            var installed = ReadMarker(serverDirectory);
            return new GameBuild(installed, latest, installed != latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Failed("bedrock.check_failed", $"Mojang's latest Minecraft Bedrock server could not be looked up: {ex.Message}", ex);
        }
    }

    /// <summary>Where Mojang serves a version's Windows build.</summary>
    public static Uri AddressOf(string version) => VersionPattern().IsMatch(version)
        ? new Uri($"https://www.minecraft.net/bedrockdedicatedserver/bin-win/bedrock-server-{version}.zip")
        : throw Failed("bedrock.unknown_version", $"'{version}' is not a Minecraft Bedrock version.");

    /// <summary>The version in an address of Mojang's Windows builds; null for any other address.</summary>
    public static string? VersionOf(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var match = DownloadPattern().Match(address.AbsoluteUri);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static async Task<Uri> LatestAsync(VerifiedDownloader client, CancellationToken cancellationToken)
    {
        var links = await client.GetJsonAsync<LinksAnswer>(LinksUrl, sha1: null, cancellationToken);
        var url = links.Result?.Links?.FirstOrDefault(l => l.DownloadType == WindowsDownload)?.DownloadUrl
            ?? throw Failed("bedrock.unexpected_link", "Mojang lists no Bedrock server for Windows.");
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && VersionOf(uri) is not null
            ? uri
            : throw Failed("bedrock.unexpected_link", "Mojang listed a Bedrock server download Pulsar does not recognize.");
    }

    /// <summary>
    /// The archive's files with their paths in the server directory, checked: no paths outside it or into Pulsar's own
    /// folder (by its name or a short name), no name twice (Windows names ignore case: a second "Bedrock_Server.exe" would
    /// replace the checked one), bounded.
    /// </summary>
    private static List<(ZipArchiveEntry Entry, string Relative)> Entries(ZipArchive archive)
    {
        var entries = new List<(ZipArchiveEntry, string)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue; // folders come with their files
            }

            var first = name.Split('/')[0];
            if (!ManifestValidator.IsValidRelativePath(name) || first.Equals(".pulsar", StringComparison.OrdinalIgnoreCase)
                || first.Contains('~', StringComparison.Ordinal) || !names.Add(name))
            {
                throw Failed("bedrock.bad_archive", $"The Bedrock server archive has an unexpected entry '{entry.FullName}'.");
            }

            total += entry.Length;
            if (total > MaximumExtracted || entries.Count >= MaximumEntries)
            {
                throw Failed("bedrock.bad_archive", "The Bedrock server archive is larger than expected.");
            }

            entries.Add((entry, name));
        }

        return entries.Count(e => e.Item2 == Executable) == 1
            ? entries
            : throw Failed("bedrock.bad_archive", $"The Bedrock server archive does not contain {Executable}.");
    }

    private string? ReadMarker(string serverDirectory)
    {
        using var stream = files.OpenRead(serverDirectory, VersionMarker);
        // The server's account can write it: anything but a short version counts as none (the server is installed again).
        if (stream is null || stream.Length > 64)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd().Trim();
        return VersionPattern().IsMatch(text) ? text : null;
    }

    private static GameProcessException Failed(string code, string message, Exception? inner = null) => new(code, message, inner);

    [GeneratedRegex(@"\A[0-9]{1,4}(\.[0-9]{1,4}){2,3}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\Ahttps://www\.minecraft\.net/bedrockdedicatedserver/bin-win/bedrock-server-(?<version>[0-9]{1,4}(\.[0-9]{1,4}){2,3})\.zip\z",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DownloadPattern();

    private sealed record LinksAnswer(LinksResult? Result);

    private sealed record LinksResult(IReadOnlyList<DownloadLink>? Links);

    private sealed record DownloadLink(string? DownloadType, string? DownloadUrl);
}
