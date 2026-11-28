using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;

namespace Pulsar.Agent.Linux;

/// <summary>
/// The node's SteamCMD on Linux (docs/architecture/06, "SteamCMD"): Valve's <c>steamcmd_linux.tar.gz</c>, downloaded once
/// over HTTPS into the agent's state folder (root's alone). Linux programs carry no signature to check, so each file is
/// accepted only with the SHA-256 Pulsar knows for it: Valve has not changed this download since 2018 (its programs
/// update themselves from Steam on their first run), and a download that differs is refused, never run. Every server
/// gets a copy of these files, which then updates itself.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxSteamCmdBootstrapper(AgentPaths paths, ILogger<LinuxSteamCmdBootstrapper> logger)
    : ISteamCmdSource, IDisposable
{
    /// <summary>Valve's official download (developer.valvesoftware.com/wiki/SteamCMD#Linux).</summary>
    public static readonly Uri DownloadUrl = new("https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz");

    private const long MaximumDownload = 16 * 1024 * 1024;
    private const long MaximumFile = 16 * 1024 * 1024;

    /// <summary>The files of Valve's download (as of 2018-01-05), each with its SHA-256 and whether it is a program.</summary>
    internal static readonly IReadOnlyList<(string Name, string Sha256, bool Executable)> KnownFiles =
    [
        ("linux32/steamcmd", "e3e42e79a78f5b404930a143773c66b30354ba827bfe99e7f86d9ac3271074d6", true),
        ("linux32/steamerrorreporter", "ae21f3f403ec6e0b293301de18420ffd36a4830c03e617d6ea9fb391ad74fda0", true),
        ("linux32/libstdc++.so.6", "f187bf8146e73f524d0d885758fe1f6575efd02bae4e90a37214e7b6b6774d52", false),
        ("linux32/crashhandler.so", "d08bf9378b56280f7e35382a90304b2a5655feb3ceffd667c1013040463939de", false),
        ("steamcmd.sh", "9e8a437ca88b7f40a9890f305720286f95327f2f708f3a88b8142f688d1e0a81", true),
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);

    public SteamCmdLayout Layout => SteamCmdLayout.Linux;

    public void Dispose() => _lock.Dispose();

    public async Task<string> GetAsync(CancellationToken cancellationToken) =>
        (await GetFilesAsync(cancellationToken)).Single(f => f.Name == Layout.ExecutableName).Source;

    public async Task<IReadOnlyList<SteamCmdFile>> GetFilesAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var kept = KnownFiles.Select(f => new SteamCmdFile(f.Name, Local(f.Name), f.Executable)).ToList();
            if (kept.All(f => File.Exists(f.Source)))
            {
                // Checked each time: the copies are made from these files, and a server's copy can sign in to Steam.
                foreach (var (name, sha256, _) in KnownFiles)
                {
                    Verify(name, await File.ReadAllBytesAsync(Local(name), cancellationToken), sha256);
                }

                return kept;
            }

            var downloaded = Extract(await DownloadAsync(cancellationToken));
            Directory.CreateDirectory(paths.SteamCmdDirectory);
            foreach (var (name, content) in downloaded)
            {
                var path = Local(name);
                var partial = path + ".download";
                await File.WriteAllBytesAsync(partial, content, cancellationToken);
                File.Move(partial, path, overwrite: true);
            }

            LogDownloaded(paths.SteamCmdDirectory);
            return kept;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The known files of the download, each checked against its SHA-256: only regular files, nothing else is read out
    /// of it (links, folders and other files are skipped).
    /// </summary>
    /// <exception cref="GameProcessException"><c>steamcmd.untrusted</c> when a file differs or is missing.</exception>
    internal static IReadOnlyList<(string Name, byte[] Content)> Extract(byte[] download)
    {
        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var gzip = new GZipStream(new MemoryStream(download, writable: false), CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null
                    || !KnownFiles.Any(f => f.Name == name))
                {
                    continue;
                }

                if (entry.Length > MaximumFile || found.ContainsKey(name))
                {
                    throw new GameProcessException("steamcmd.untrusted", $"Valve's SteamCMD download has an unexpected {name}.");
                }

                using var content = new MemoryStream();
                entry.DataStream.CopyTo(content);
                found[name] = content.ToArray();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
        {
            throw new GameProcessException("steamcmd.download_failed", $"Valve's SteamCMD download could not be read: {ex.Message}", ex);
        }

        var files = new List<(string, byte[])>();
        foreach (var (name, sha256, _) in KnownFiles)
        {
            if (!found.TryGetValue(name, out var content))
            {
                throw new GameProcessException("steamcmd.untrusted", $"Valve's SteamCMD download has no {name}.");
            }

            Verify(name, content, sha256);
            files.Add((name, content));
        }

        return files;
    }

    private static void Verify(string name, byte[] content, string sha256)
    {
        if (!Convert.ToHexStringLower(SHA256.HashData(content)).Equals(sha256, StringComparison.Ordinal))
        {
            throw new GameProcessException("steamcmd.untrusted",
                $"SteamCMD's {name} is not the one Pulsar knows from Valve: it is not run. If Valve changed its download, a newer Pulsar agent knows the new one.");
        }
    }

    private string Local(string name) => Path.Combine(paths.SteamCmdDirectory, name.Replace('/', '-'));

    private static async Task<byte[]> DownloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaximumDownload)
                {
                    throw new GameProcessException("steamcmd.download_failed", "The SteamCMD download is larger than expected.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new GameProcessException("steamcmd.download_failed", $"SteamCMD could not be downloaded from Valve: {ex.Message}", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SteamCMD downloaded from Valve and verified: {Path}")]
    private partial void LogDownloaded(string path);
}
