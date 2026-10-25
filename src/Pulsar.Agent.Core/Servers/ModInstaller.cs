using System.Security.Cryptography;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Puts a mod catalog's files into a server and deletes others (<see cref="ChangeModsCommand"/>, Pulsar 1.0.9;
/// docs/architecture/04, "Mods"). Every file is downloaded first, over HTTPS from the catalog's own hosts, and checked
/// against its SHA-512; only then are files deleted and written, so a failed download changes nothing. Files go only
/// into the folders the game's manifest names for its catalogs, as <c>.jar</c> files.
/// </summary>
public sealed class ModInstaller(IServerFiles files, HttpMessageHandler handler)
{
    // Modrinth's files are served from its CDN only.
    private static readonly HashSet<string> s_hosts = new(StringComparer.OrdinalIgnoreCase) { "cdn.modrinth.com" };

    /// <summary>A mod or plugin larger than this is not one.</summary>
    public const long MaximumFile = 256L * 1024 * 1024;

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <param name="folders">The folders the game's manifest puts catalog files into.</param>
    /// <exception cref="GameProcessException">With a <c>mods.*</c> code; nothing was changed unless the code is <c>mods.write_failed</c>.</exception>
    public async Task ChangeAsync(string serverDirectory, IReadOnlySet<string> folders, IReadOnlyList<ModDownload> add, IReadOnlyList<string> remove,
        Func<string, double?, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(progress);
        if (add.Count + remove.Count is 0 || add.Count + remove.Count > ChangeModsCommand.MaxFiles)
        {
            throw Failed("mods.invalid", $"A change adds or removes 1 to {ChangeModsCommand.MaxFiles} files.");
        }

        foreach (var path in add.Select(a => a.Path).Concat(remove))
        {
            if (!ModFiles.IsPath(path, folders))
            {
                throw Failed("mods.invalid_path", $"'{path}' is not a .jar file in one of the game's mod folders.");
            }
        }

        if (add.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != add.Count)
        {
            throw Failed("mods.invalid", "A file is added twice.");
        }

        // The panel replaces only the files it put there (those it removes in the same change): a file someone else put
        // in the folder under the same name stays theirs.
        var removed = new HashSet<string>(remove, StringComparer.OrdinalIgnoreCase);
        foreach (var path in add.Select(a => a.Path).Where(p => !removed.Contains(p)))
        {
            if (Exists(serverDirectory, path))
            {
                throw Failed("mods.file_exists", $"'{path}' is already on the server, and the panel did not put it there: delete or rename it first.");
            }
        }

        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new VerifiedDownloader(http, s_hosts, "mods", "the mod catalog") { StallTimeout = StallTimeout };
        var downloaded = new List<(string Path, FileStream Content)>();
        try
        {
            for (var i = 0; i < add.Count; i++)
            {
                var file = add[i];
                if (file.Size is < 0 or > MaximumFile || file.Sha512 is not { Length: 128 } sha512 || !sha512.All(char.IsAsciiHexDigit))
                {
                    throw Failed("mods.invalid", $"'{file.Path}' has no SHA-512, or is larger than a mod.");
                }

                var index = i;
                downloaded.Add((file.Path, await client.DownloadAsync(VerifiedDownloader.Https(file.Url, "mods"), HashAlgorithmName.SHA512, sha512,
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

        await progress(Steam.InstallStages.Installing, null);
        try
        {
            foreach (var path in remove)
            {
                Delete(serverDirectory, path);
            }

            foreach (var (path, content) in downloaded)
            {
                content.Position = 0;
                files.Write(serverDirectory, path, content);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failed("mods.write_failed", $"The mods could not all be written: {ex.Message}", ex);
        }
        finally
        {
            await DisposeAsync(downloaded);
        }
    }

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

    private static async Task DisposeAsync(List<(string Path, FileStream Content)> downloaded)
    {
        foreach (var (_, content) in downloaded)
        {
            await content.DisposeAsync();
        }

        downloaded.Clear();
    }

    private static GameProcessException Failed(string code, string message, Exception? inner = null) => new(code, message, inner);
}
