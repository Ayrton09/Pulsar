namespace Pulsar.Manifests;

/// <summary>Where a mod catalog's files go in a server (Pulsar 1.0.9): checked by the panel and again by the agent.</summary>
public static class ModFiles
{
    public const int MaxFileNameLength = 128;

    /// <summary>The folders a manifest's catalogs put files into.</summary>
    public static IReadOnlySet<string> Folders(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ModList.SelectMany(s => s.Types.Values).Select(t => t.Folder).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A <c>.jar</c> file right in one of <paramref name="folders"/>: <c>mods/lithium-fabric-0.18.1.jar</c>.</summary>
    public static bool IsPath(string? path, IReadOnlySet<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (path is null || !ManifestValidator.IsValidRelativePath(path))
        {
            return false;
        }

        var parts = path.Split('/');
        return parts is [var folder, var name] && folders.Contains(folder) && IsFileName(name);
    }

    /// <summary>A catalog file's name, as it may land on the server: a <c>.jar</c> of plain characters.</summary>
    public static bool IsFileName(string? name) =>
        name is { Length: > 4 and <= MaxFileNameLength } && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !name.StartsWith('.')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '+' or '(' or ')' or '[' or ']' or '!' or ',')
        && ManifestValidator.IsValidRelativePath(name);
}
