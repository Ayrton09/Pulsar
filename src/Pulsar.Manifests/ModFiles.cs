namespace Pulsar.Manifests;

/// <summary>Where a mod catalog's files go in a server (Pulsar 1.0.9): checked by the panel and again by the agent.</summary>
public static class ModFiles
{
    public const int MaxFileNameLength = 128;

    /// <summary>The folders a manifest's catalogs put files into, each with what its files may end with.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Folders(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var folders = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var target in manifest.ModList.SelectMany(s => s.Types.Values).Where(t => t.Layout is null))
        {
            folders[target.Folder] = folders.TryGetValue(target.Folder, out var known)
                ? [.. known.Union(target.ExtensionList, StringComparer.OrdinalIgnoreCase)]
                : target.ExtensionList;
        }

        return folders;
    }

    /// <summary>The BepInEx folders (Pulsar 1.0.14) a manifest's catalogs unpack packages into (Thunderstore's).</summary>
    public static IReadOnlySet<string> PackageFolders(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ModList.SelectMany(s => s.Types.Values).Where(t => t.Layout == ModTargetDefinition.BepInExLayout).Select(t => t.Folder)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A package's own folder of plugins in one of <paramref name="folders"/>: <c>BepInEx/plugins/ValheimModding-Jotunn</c>.</summary>
    public static bool IsPackagePath(string? path, IReadOnlySet<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (path is null || !ManifestValidator.IsValidRelativePath(path))
        {
            return false;
        }

        var marker = path.LastIndexOf(PluginsSegment, StringComparison.Ordinal);
        return marker > 0 && folders.Contains(path[..marker])
            && ManifestValidator.ThunderstorePackagePattern().IsMatch(path[(marker + PluginsSegment.Length)..]);
    }

    /// <summary>The BepInEx folder a package's path is in, and the package: (<c>BepInEx</c>, <c>ValheimModding-Jotunn</c>).</summary>
    public static (string Folder, string Package) SplitPackagePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var marker = path.LastIndexOf(PluginsSegment, StringComparison.Ordinal);
        return (path[..marker], path[(marker + PluginsSegment.Length)..]);
    }

    private const string PluginsSegment = "/plugins/";

    /// <summary>
    /// Whether every one of <paramref name="paths"/> is in a folder of a catalog whose files the running game picks up
    /// (<see cref="ModSourceDefinition.WhileRunning"/>, Pulsar 1.0.14): they can change without stopping it.
    /// </summary>
    public static bool WhileRunning(GameManifest manifest, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(paths);
        var live = manifest.ModList.Where(s => s.WhileRunning).SelectMany(s => s.Types.Values).Select(t => t.Folder).ToHashSet(StringComparer.Ordinal);
        return paths.All(p => p.LastIndexOf('/') is > 0 and var slash && live.Contains(p[..slash]));
    }

    /// <summary>
    /// A file right in one of <paramref name="folders"/>, ending as its files may: <c>mods/lithium-fabric-0.18.1.jar</c>,
    /// <c>oxide/plugins/Kits.cs</c>.
    /// </summary>
    public static bool IsPath(string? path, IReadOnlyDictionary<string, IReadOnlyList<string>> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (path is null || !ManifestValidator.IsValidRelativePath(path))
        {
            return false;
        }

        var slash = path.LastIndexOf('/');
        return slash > 0 && folders.TryGetValue(path[..slash], out var extensions) && IsFileName(path[(slash + 1)..], extensions);
    }

    /// <summary>A catalog file's name, as it may land on the server: a <c>.jar</c> of plain characters.</summary>
    public static bool IsFileName(string? name) => IsFileName(name, [".jar"]);

    /// <summary>A catalog file's name, as it may land on the server: plain characters, ending with one of <paramref name="extensions"/>.</summary>
    public static bool IsFileName(string? name, IReadOnlyList<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        return name is { Length: > 1 and <= MaxFileNameLength } && !name.StartsWith('.')
            && extensions.Any(e => name.Length > e.Length && name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '+' or '(' or ')' or '[' or ']' or '!' or ',')
            && ManifestValidator.IsValidRelativePath(name);
    }
}
