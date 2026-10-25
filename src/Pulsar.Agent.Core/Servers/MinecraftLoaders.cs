using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// The server types besides Mojang's own (Pulsar 1.0.9), from their publishers' APIs, each download checked against the
/// checksum the publisher lists over the same TLS: Paper (SHA-256, its latest stable build), Purpur (MD5, the only one it
/// publishes), Fabric (its loader and libraries beside Mojang's jar, by SHA-256, and the launcher jar its installer would
/// write), and Forge and NeoForge, whose installer (SHA-256) runs as the server's account and fetches the libraries it
/// lists itself, checking them against its own profile.
/// </summary>
public sealed partial class MinecraftInstaller
{
    private static readonly Uri s_paperProject = new("https://fill.papermc.io/v3/projects/paper");
    private static readonly Uri s_purpurProject = new("https://api.purpurmc.org/v2/purpur");
    private static readonly Uri s_fabricMeta = new("https://meta.fabricmc.net/v2/");
    private static readonly Uri s_fabricMaven = new("https://maven.fabricmc.net/");
    private static readonly Uri s_forgePromotions = new("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json");
    private static readonly Uri s_neoForgeVersions = new("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge");

    /// <summary>Where a Forge or NeoForge installer is put while it runs (the server's account reads it there).</summary>
    private const string InstallerJar = ".pulsar/minecraft-installer.jar";

    /// <summary>The jar Fabric's installer writes to start the server: Mojang's server.jar is beside it.</summary>
    public const string FabricLauncher = "fabric-server-launch.jar";

    // Paper lists every version it ever built: the newest few are enough to find one with a stable build.
    private const int MaximumPaperVersionsTried = 8;

    // A win_args.txt lists a few hundred library paths.
    private const int MaximumArgumentsFile = 256 * 1024;

    // Fabric's loader needs a dozen libraries of a few hundred KiB each.
    private const int MaximumFabricLibraries = 200;
    private const long MaximumFabricLibrary = 64L * 1024 * 1024;

    // Forge's modern installs (an arguments file to start) began with Minecraft 1.17.
    private static readonly int[] s_firstModernForge = [1, 17];

    /// <summary>What a type would install now: the Minecraft version and its build of the type (empty for Mojang's own).</summary>
    private sealed record LoaderTarget(string Type, string Version, string Build, Uri? Download = null, HashAlgorithmName? Algorithm = null,
        string? Checksum = null);

    private static async Task<LoaderTarget> ResolveAsync(VerifiedDownloader client, MinecraftChoice choice, CancellationToken cancellationToken) =>
        choice.TypeOrVanilla switch
        {
            MinecraftServerStep.Paper => await PaperAsync(client, choice.Version, cancellationToken),
            MinecraftServerStep.Purpur => await PurpurAsync(client, choice.Version, cancellationToken),
            MinecraftServerStep.Fabric => await FabricAsync(client, choice, cancellationToken),
            MinecraftServerStep.Forge => await ForgeAsync(client, choice, cancellationToken),
            MinecraftServerStep.NeoForge => await NeoForgeAsync(client, choice, cancellationToken),
            var other => throw Failed("minecraft.unknown_type", $"Pulsar does not install a Minecraft server of type '{other}'."),
        };

    /// <summary>Installs <paramref name="target"/>'s files (the Java runtime is the caller's).</summary>
    private async Task InstallTypeAsync(VerifiedDownloader client, string serverDirectory, LoaderTarget target, Func<double, Task> progress,
        CancellationToken cancellationToken)
    {
        switch (target.Type)
        {
            case MinecraftServerStep.Paper or MinecraftServerStep.Purpur:
                string checksum;
                await using (var jar = await DownloadAsync(client, target.Download!, target.Algorithm, target.Checksum, progress, cancellationToken))
                {
                    files.Write(serverDirectory, ServerJar, jar);
                    checksum = target.Checksum!.ToLowerInvariant();
                }

                WriteMarker(serverDirectory, target, checksum);
                break;
            case MinecraftServerStep.Fabric:
                await InstallFabricAsync(client, serverDirectory, target, progress, cancellationToken);
                break;
            default:
                await RunInstallerAsync(client, serverDirectory, target, progress, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Paper's newest stable build of the version (or of the newest version that has one): Fill v3, PaperMC's download
    /// service since v2 was retired, with each jar's SHA-256.
    /// </summary>
    private static async Task<LoaderTarget> PaperAsync(VerifiedDownloader client, string? version, CancellationToken cancellationToken)
    {
        var project = await client.GetJsonAsync<PaperProject>(s_paperProject, null, cancellationToken);
        // Newest first, as Fill lists them; release candidates and pre-releases are not offered.
        var known = (project.Versions ?? []).SelectMany(g => g.Value ?? []).Where(v => v is not null && !v.Contains('-', StringComparison.Ordinal)).ToList();
        var candidates = IsLatest(version) ? known.Take(MaximumPaperVersionsTried) : known.Where(v => v == version);
        foreach (var candidate in candidates)
        {
            CheckVersion(candidate);
            var builds = await client.GetJsonAsync<PaperBuild[]>(new Uri(s_paperProject, $"paper/versions/{candidate}/builds"), null, cancellationToken);
            if (builds.FirstOrDefault(b => b.Channel == "STABLE" && b.Downloads?.Server is { Url: not null, Checksums.Sha256: not null }) is { } build)
            {
                var server = build.Downloads!.Server!;
                return new LoaderTarget(MinecraftServerStep.Paper, candidate, build.Id.ToString(CultureInfo.InvariantCulture), Https(server.Url!),
                    HashAlgorithmName.SHA256, server.Checksums!.Sha256);
            }
        }

        throw IsLatest(version)
            ? Failed("minecraft.no_build", "Paper has no stable build of a recent Minecraft version.")
            : Failed("minecraft.no_build", $"Paper has no stable build of Minecraft {version}.");
    }

    /// <summary>Purpur's latest successful build of the version; it publishes only an MD5, over the same TLS as the jar.</summary>
    private static async Task<LoaderTarget> PurpurAsync(VerifiedDownloader client, string? version, CancellationToken cancellationToken)
    {
        var project = await client.GetJsonAsync<PurpurProject>(s_purpurProject, null, cancellationToken);
        var id = IsLatest(version) ? project.Metadata?.Current : version;
        if (id is null || !(project.Versions ?? []).Contains(id))
        {
            throw Failed("minecraft.no_build", $"Purpur has no build of Minecraft {id ?? "(latest)"}.");
        }

        CheckVersion(id);
        var build = await client.GetJsonAsync<PurpurBuild>(new Uri($"{s_purpurProject}/{id}/latest"), null, cancellationToken);
        if (build is not { Result: "SUCCESS", Build: { } number, Md5: { } md5 } || !IsBuild(number))
        {
            throw Failed("minecraft.no_build", $"Purpur has no successful build of Minecraft {id}.");
        }

        return new LoaderTarget(MinecraftServerStep.Purpur, id, number, new Uri($"{s_purpurProject}/{id}/{number}/download"), HashAlgorithmName.MD5, md5);
    }

    /// <summary>Fabric's loader (the chosen one, or the latest stable) for the version (or the latest stable one).</summary>
    private static async Task<LoaderTarget> FabricAsync(VerifiedDownloader client, MinecraftChoice choice, CancellationToken cancellationToken)
    {
        var games = await client.GetJsonAsync<FabricVersion[]>(new Uri(s_fabricMeta, "versions/game"), null, cancellationToken);
        var id = IsLatest(choice.Version) ? games.FirstOrDefault(g => g.Stable)?.Version : choice.Version;
        if (id is null || !games.Any(g => g.Version == id))
        {
            throw Failed("minecraft.no_build", $"Fabric does not support Minecraft {id ?? "(latest)"}.");
        }

        var loader = string.IsNullOrEmpty(choice.Loader)
            ? (await client.GetJsonAsync<FabricVersion[]>(new Uri(s_fabricMeta, "versions/loader"), null, cancellationToken)).FirstOrDefault(l => l.Stable)?.Version
            : choice.Loader;
        if (loader is null || !IsBuild(loader))
        {
            throw Failed("minecraft.no_build", "Fabric lists no stable loader.");
        }

        CheckVersion(id);
        return new LoaderTarget(MinecraftServerStep.Fabric, id, loader);
    }

    /// <summary>Forge's recommended (else latest) build of the version, or of the newest version Forge has one for.</summary>
    private static async Task<LoaderTarget> ForgeAsync(VerifiedDownloader client, MinecraftChoice choice, CancellationToken cancellationToken)
    {
        var promotions = (await client.GetJsonAsync<ForgePromotions>(s_forgePromotions, null, cancellationToken)).Promos ?? [];
        var versions = promotions.Keys.Select(k => k[..Math.Max(0, k.LastIndexOf('-'))]).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        string id;
        if (IsLatest(choice.Version))
        {
            if (!string.IsNullOrEmpty(choice.Loader))
            {
                throw Failed("minecraft.loader_needs_version", "A Forge version belongs to one Minecraft version: choose that version too.");
            }

            id = versions.Where(v => v.All(c => char.IsAsciiDigit(c) || c == '.')).OrderByDescending(v => v, VersionComparer.Instance).FirstOrDefault()
                ?? throw Failed("minecraft.no_build", "Forge lists no Minecraft version.");
        }
        else
        {
            id = choice.Version!;
        }

        CheckVersion(id);
        if (VersionComparer.Instance.Compare(id, string.Join('.', s_firstModernForge)) < 0)
        {
            throw Failed("minecraft.loader_unsupported", "Pulsar installs Forge for Minecraft 1.17 and later.");
        }

        var forge = string.IsNullOrEmpty(choice.Loader)
            ? promotions.GetValueOrDefault($"{id}-recommended") ?? promotions.GetValueOrDefault($"{id}-latest")
            : choice.Loader;
        if (forge is null || !IsBuild(forge))
        {
            throw Failed("minecraft.no_build", $"Forge has no build of Minecraft {id}.");
        }

        var installer = new Uri($"https://maven.minecraftforge.net/net/minecraftforge/forge/{id}-{forge}/forge-{id}-{forge}-installer.jar");
        return new LoaderTarget(MinecraftServerStep.Forge, id, forge, installer, HashAlgorithmName.SHA256);
    }

    /// <summary>NeoForge's newest release for the version (or the newest release); the chosen one when it is set.</summary>
    private static async Task<LoaderTarget> NeoForgeAsync(VerifiedDownloader client, MinecraftChoice choice, CancellationToken cancellationToken)
    {
        // Listed in no particular order.
        var all = (await client.GetJsonAsync<NeoForgeVersions>(s_neoForgeVersions, null, cancellationToken)).Versions ?? [];
        string neoForge;
        if (!string.IsNullOrEmpty(choice.Loader))
        {
            neoForge = all.Contains(choice.Loader) ? choice.Loader : throw Failed("minecraft.no_build", $"NeoForge has no version {choice.Loader}.");
        }
        else
        {
            var releases = all.Where(v => !v.Contains('-', StringComparison.Ordinal) && NeoForgeMinecraft(v) is not null);
            neoForge = (IsLatest(choice.Version) ? releases : releases.Where(v => NeoForgeMinecraft(v) == choice.Version))
                .OrderByDescending(v => v, VersionComparer.Instance).FirstOrDefault()
                ?? throw Failed("minecraft.no_build", $"NeoForge has no release for Minecraft {choice.Version}.");
        }

        var id = NeoForgeMinecraft(neoForge) ?? throw Failed("minecraft.no_build", $"NeoForge {neoForge} names no Minecraft version Pulsar knows.");
        if (!IsLatest(choice.Version) && id != choice.Version)
        {
            throw Failed("minecraft.loader_mismatch", $"NeoForge {neoForge} is for Minecraft {id}, not {choice.Version}.");
        }

        CheckVersion(id);
        if (!IsBuild(neoForge))
        {
            throw Failed("minecraft.no_build", $"NeoForge has no version {neoForge}.");
        }

        var installer = new Uri($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neoForge}/neoforge-{neoForge}-installer.jar");
        return new LoaderTarget(MinecraftServerStep.NeoForge, id, neoForge, installer, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// The Minecraft version a NeoForge version is for: <c>21.1.265</c> is 1.21.1, <c>21.0.x</c> 1.21, <c>20.4.x</c> 1.20.4;
    /// since Minecraft's yearly versions, the first three numbers are it (<c>26.1.2.124</c> is 26.1.2, <c>26.3.0.99</c> 26.3).
    /// Null for another scheme (snapshots' builds).
    /// </summary>
    public static string? NeoForgeMinecraft(string neoForge)
    {
        ArgumentNullException.ThrowIfNull(neoForge);
        var numbers = neoForge.Split('-')[0].Split('.');
        if (!numbers.All(n => n.Length is > 0 and <= 6 && n.All(char.IsAsciiDigit)))
        {
            return null;
        }

        return numbers switch
        {
            [var year, var release, var patch, _] when int.Parse(year, CultureInfo.InvariantCulture) >= 26 =>
                patch == "0" ? $"{year}.{release}" : $"{year}.{release}.{patch}",
            [var minor, var patch, _] when int.Parse(minor, CultureInfo.InvariantCulture) is >= 20 and < 26 =>
                patch == "0" ? $"1.{minor}" : $"1.{minor}.{patch}",
            _ => null,
        };
    }

    /// <summary>
    /// Fabric's server, as its installer lays it out (checked with installer 1.1.2): Mojang's server.jar, the libraries
    /// Fabric's metadata lists with their SHA-256, its loader and intermediary mappings (their Maven SHA-256), and
    /// <see cref="FabricLauncher"/>, a jar of a manifest naming them.
    /// </summary>
    private async Task InstallFabricAsync(VerifiedDownloader client, string serverDirectory, LoaderTarget target, Func<double, Task> progress,
        CancellationToken cancellationToken)
    {
        var (id, details) = await MojangAsync(client, target.Version, cancellationToken);
        var server = details.Downloads?.Server is { Url: not null, Sha1: not null } download
            ? download
            : throw Failed("minecraft.no_server", $"Minecraft {id} has no dedicated server download.");
        await using (var jar = await DownloadAsync(client, Https(server.Url!), HashAlgorithmName.SHA1, server.Sha1, p => progress(p * 0.6), cancellationToken))
        {
            files.Write(serverDirectory, ServerJar, jar);
        }

        var profile = await client.GetJsonAsync<FabricProfile>(
            new Uri(s_fabricMeta, $"versions/loader/{Uri.EscapeDataString(target.Version)}/{Uri.EscapeDataString(target.Build)}"), null, cancellationToken);
        var libraries = new List<(string Path, Uri Url, string? Sha256)>();
        foreach (var library in (profile.LauncherMeta?.Libraries?.Common ?? []).Concat(profile.LauncherMeta?.Libraries?.Server ?? []))
        {
            if (library.Url != s_fabricMaven.ToString() || library.Sha256 is null)
            {
                throw Failed("minecraft.bad_library", $"Fabric lists a library outside its own Maven, or without a checksum: {library.Name}.");
            }

            var path = MavenPath(library.Name);
            libraries.Add((path, new Uri(s_fabricMaven, path), library.Sha256));
        }

        if (profile.Intermediary?.Maven is { } intermediary && profile.Intermediary.Version != "0.0.0")
        {
            var path = MavenPath(intermediary);
            libraries.Add((path, new Uri(s_fabricMaven, path), null));
        }

        var loader = MavenPath(profile.Loader?.Maven ?? throw Failed("minecraft.bad_library", "Fabric's metadata names no loader."));
        libraries.Add((loader, new Uri(s_fabricMaven, loader), null));
        if (libraries.Count > MaximumFabricLibraries)
        {
            throw Failed("minecraft.bad_library", $"Fabric's metadata lists {libraries.Count} libraries, more than a loader needs.");
        }

        for (var i = 0; i < libraries.Count; i++)
        {
            var (path, url, sha256) = libraries[i];
            sha256 ??= await Sha256SidecarAsync(client, url, cancellationToken);
            await using var library = await client.DownloadAsync(url, HashAlgorithmName.SHA256, sha256, _ => Task.CompletedTask, cancellationToken,
                MaximumFabricLibrary);
            files.Write(serverDirectory, $"libraries/{path}", library);
            await progress(60 + (40.0 * (i + 1) / libraries.Count));
        }

        using (var launcher = new MemoryStream(FabricLauncherJar(libraries.Select(l => $"libraries/{l.Path}"))))
        {
            files.Write(serverDirectory, FabricLauncher, launcher);
        }

        WriteMarker(serverDirectory, target, server.Sha1!.ToLowerInvariant());
    }

    /// <summary>The launcher jar Fabric's installer writes: a manifest naming the libraries, and the loader's main class.</summary>
    public static byte[] FabricLauncherJar(IEnumerable<string> classPath)
    {
        var manifest = new StringBuilder()
            .Append("Manifest-Version: 1.0\r\n")
            .Append("Main-Class: net.fabricmc.loader.impl.launch.server.FabricServerLauncher\r\n")
            .Append(ManifestHeader("Class-Path: " + string.Join(' ', classPath)))
            .Append("\r\n");
        using var jar = new MemoryStream();
        using (var archive = new ZipArchive(jar, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("META-INF/MANIFEST.MF").Open(), new UTF8Encoding(false)))
            {
                writer.Write(manifest.ToString());
            }

            using (var writer = new StreamWriter(archive.CreateEntry("fabric-server-launch.properties").Open(), new UTF8Encoding(false)))
            {
                writer.Write("launch.mainClass=net.fabricmc.loader.impl.launch.knot.KnotServer\n");
            }
        }

        return jar.ToArray();
    }

    /// <summary>A manifest header in lines of 72 bytes at most, continued by a space (the JAR file specification).</summary>
    private static string ManifestHeader(string header)
    {
        var lines = new StringBuilder();
        lines.Append(header.AsSpan(0, Math.Min(72, header.Length))).Append("\r\n");
        for (var at = 72; at < header.Length; at += 71)
        {
            lines.Append(' ').Append(header.AsSpan(at, Math.Min(71, header.Length - at))).Append("\r\n");
        }

        return lines.ToString();
    }

    /// <summary>
    /// Forge's or NeoForge's server: its installer (checked against its Maven SHA-256) run as the server's account, which
    /// downloads Mojang's jar and the libraries its profile lists (it checks them itself) and leaves the Java arguments that
    /// start the server in <c>win_args.txt</c>, copied to <see cref="MinecraftServerStep.ArgumentsFile"/>.
    /// </summary>
    private async Task RunInstallerAsync(VerifiedDownloader client, string serverDirectory, LoaderTarget target, Func<double, Task> progress,
        CancellationToken cancellationToken)
    {
        var name = target.Type == MinecraftServerStep.Forge ? "Forge" : "NeoForge";
        var run = java ?? throw Failed("minecraft.installer_unavailable", $"This agent cannot run {name}'s installer.");
        var sha256 = await Sha256SidecarAsync(client, target.Download!, cancellationToken);
        await using (var installer = await DownloadAsync(client, target.Download!, HashAlgorithmName.SHA256, sha256, progress, cancellationToken))
        {
            files.Write(serverDirectory, InstallerJar, installer);
        }

        try
        {
            await run(["-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-jar", InstallerJar, "--installServer"], cancellationToken);
        }
        finally
        {
            foreach (var leftover in (string[])[InstallerJar, $"{InstallerJar}.log", "minecraft-installer.jar.log", "installer.log"])
            {
                files.Delete(serverDirectory, leftover);
            }
        }

        var arguments = target.Type == MinecraftServerStep.Forge
            ? $"libraries/net/minecraftforge/forge/{target.Version}-{target.Build}/win_args.txt"
            : $"libraries/net/neoforged/neoforge/{target.Build}/win_args.txt";
        using (var content = new MemoryStream())
        {
            await using (var written = files.OpenRead(serverDirectory, arguments))
            {
                if (written is null || written.Length > MaximumArgumentsFile)
                {
                    throw Failed("minecraft.installer_failed", $"{name}'s installer did not leave {arguments}; the console has what it printed.");
                }

                await written.CopyToAsync(content, cancellationToken);
            }

            content.Position = 0;
            files.Write(serverDirectory, MinecraftServerStep.ArgumentsFile, content);
        }

        WriteMarker(serverDirectory, target, "-");
    }

    /// <summary>The SHA-256 a Maven repository publishes beside a file (<c>.sha256</c>, hex, maybe followed by the file name).</summary>
    private static async Task<string> Sha256SidecarAsync(VerifiedDownloader client, Uri file, CancellationToken cancellationToken)
    {
        await using var sidecar = await client.DownloadAsync(new Uri($"{file}.sha256"), null, null, _ => Task.CompletedTask, cancellationToken,
            maximum: 1024);
        var buffer = new byte[256];
        var read = await sidecar.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        var text = Encoding.ASCII.GetString(buffer, 0, read).Trim();
        var hash = text.Split([' ', '\t', '\r', '\n'], 2)[0];
        return hash.Length == 64 && hash.All(char.IsAsciiHexDigit)
            ? hash
            : throw Failed("minecraft.no_checksum", $"No SHA-256 is published beside {file}.");
    }

    /// <summary>A Maven coordinate's path: <c>net.fabricmc:fabric-loader:0.20.1</c> → <c>net/fabricmc/fabric-loader/0.20.1/fabric-loader-0.20.1.jar</c>.</summary>
    public static string MavenPath(string coordinate)
    {
        ArgumentNullException.ThrowIfNull(coordinate);
        var parts = coordinate.Split(':');
        if (parts is not ([_, _, _] or [_, _, _, _]) || parts.Any(p => p.Length is 0 or > 128 || p is "." or ".." || !p.All(IsMavenCharacter)))
        {
            throw Failed("minecraft.bad_library", $"'{coordinate}' is not a Maven coordinate.");
        }

        var (group, artifact, version) = (parts[0], parts[1], parts[2]);
        var classifier = parts.Length == 4 ? $"-{parts[3]}" : "";
        var path = $"{group.Replace('.', '/')}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
        return ManifestValidator.IsValidRelativePath($"libraries/{path}") ? path : throw Failed("minecraft.bad_library", $"'{coordinate}' is not a Maven coordinate.");
    }

    private static bool IsMavenCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+';

    private static bool IsLatest(string? version) => string.IsNullOrEmpty(version) || version == MinecraftServerStep.Latest;

    /// <summary>A version or build goes into addresses and file names: letters, digits, '.', '-', '_' and '+' only.</summary>
    private static bool IsBuild(string build) => build.Length is > 0 and <= 64 && build.All(IsMavenCharacter) && build is not ("." or "..");

    private static void CheckVersion(string version)
    {
        if (!IsBuild(version))
        {
            throw Failed("minecraft.unknown_version", $"'{version}' is not a Minecraft version.");
        }
    }

    /// <summary>Versions compared number by number (<c>1.21.10</c> after <c>1.21.9</c>, <c>26.1</c> after <c>1.21.11</c>).</summary>
    private sealed class VersionComparer : IComparer<string?>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var left = (x ?? "").Split('-')[0].Split('.');
            var right = (y ?? "").Split('-')[0].Split('.');
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var a = i < left.Length ? left[i] : "0";
                var b = i < right.Length ? right[i] : "0";
                var order = int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var na)
                    && int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var nb)
                        ? na.CompareTo(nb)
                        : string.CompareOrdinal(a, b);
                if (order != 0)
                {
                    return order;
                }
            }

            return 0;
        }
    }

    private sealed record PaperProject(Dictionary<string, List<string>?>? Versions);

    private sealed record PaperBuild(int Id, string? Channel, PaperDownloads? Downloads);

    private sealed record PaperDownloads([property: System.Text.Json.Serialization.JsonPropertyName("server:default")] PaperDownload? Server);

    private sealed record PaperDownload(string? Url, PaperChecksums? Checksums);

    private sealed record PaperChecksums(string? Sha256);

    private sealed record PurpurProject(PurpurMetadata? Metadata, List<string>? Versions);

    private sealed record PurpurMetadata(string? Current);

    private sealed record PurpurBuild(string? Build, string? Result, string? Md5);

    private sealed record FabricVersion(string? Version, bool Stable);

    private sealed record FabricProfile(FabricArtifact? Loader, FabricArtifact? Intermediary, FabricLauncherMeta? LauncherMeta);

    private sealed record FabricArtifact(string? Maven, string? Version);

    private sealed record FabricLauncherMeta(FabricLibraries? Libraries);

    private sealed record FabricLibraries(List<FabricLibrary>? Common, List<FabricLibrary>? Server);

    private sealed record FabricLibrary(string Name, string? Url, string? Sha256);

    private sealed record ForgePromotions(Dictionary<string, string>? Promos);

    private sealed record NeoForgeVersions(List<string>? Versions);
}
