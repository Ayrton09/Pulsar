using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// The node's SteamCMD bootstrapper (docs/architecture/06, "SteamCMD"): downloaded once from Valve over HTTPS into the
/// agent's protected state directory, and accepted only with a valid Authenticode signature whose signer's organization
/// is Valve's (<c>O=Valve</c> in spike 0001, <c>O=Valve Corp.</c> since). Every server gets a copy, which then updates
/// itself.
/// </summary>
public sealed partial class SteamCmdBootstrapper(AgentPaths paths, ILogger<SteamCmdBootstrapper> logger)
    : ISteamCmdSource, IDisposable
{
    /// <summary>Valve's official download (developer.valvesoftware.com/wiki/SteamCMD).</summary>
    public static readonly Uri DownloadUrl = new("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");

    private const long MaximumDownload = 16 * 1024 * 1024;

    // The organization of Valve's code signing certificates: "Valve" (2025, spike 0001), then "Valve Corp." (2026).
    private static readonly HashSet<string> s_valveOrganizations = new(StringComparer.Ordinal) { "Valve", "Valve Corp." };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string ExecutablePath => Path.Combine(paths.SteamCmdDirectory, "steamcmd.exe");

    public void Dispose() => _lock.Dispose();

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(ExecutablePath))
            {
                VerifyValveSignature(ExecutablePath);
                return ExecutablePath;
            }

            Directory.CreateDirectory(paths.SteamCmdDirectory);
            var download = ExecutablePath + ".download";
            await DownloadAsync(download, cancellationToken);
            try
            {
                VerifyValveSignature(download);
                File.Move(download, ExecutablePath, overwrite: true);
            }
            finally
            {
                File.Delete(download);
            }

            LogDownloaded(ExecutablePath);
            return ExecutablePath;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A valid Authenticode signature (chain to a trusted root, file unmodified) whose signer is Valve.</summary>
    /// <exception cref="GameProcessException"><c>steamcmd.untrusted</c> otherwise.</exception>
    public static void VerifyValveSignature(string path)
    {
        if (!AuthenticodeSignatures.HasValidSignature(path))
        {
            throw new GameProcessException("steamcmd.untrusted", $"{Path.GetFileName(path)} does not have a valid Authenticode signature.");
        }

        if (AuthenticodeSignatures.SignerOrganization(path) is not { } organization || !s_valveOrganizations.Contains(organization))
        {
            throw new GameProcessException("steamcmd.untrusted",
                $"{Path.GetFileName(path)} is signed by '{AuthenticodeSignatures.SignerSubject(path)}', not by Valve.");
        }
    }

    private static async Task DownloadAsync(string destination, CancellationToken cancellationToken)
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

            buffer.Position = 0;
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            var entry = zip.Entries.SingleOrDefault(e => e.FullName.Equals("steamcmd.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new GameProcessException("steamcmd.download_failed", "The SteamCMD download does not contain steamcmd.exe.");
            entry.ExtractToFile(destination, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            throw new GameProcessException("steamcmd.download_failed", $"SteamCMD could not be downloaded from Valve: {ex.Message}", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SteamCMD downloaded from Valve and verified: {Path}")]
    private partial void LogDownloaded(string path);
}
