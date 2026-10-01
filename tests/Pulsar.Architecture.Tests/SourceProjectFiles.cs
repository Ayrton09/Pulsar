using System.Xml.Linq;

namespace Pulsar.Architecture.Tests;

internal sealed record SourceProject(string Name, IReadOnlyList<string> ProjectReferences, IReadOnlyList<string> PackageReferences);

/// <summary>Reads every <c>src/*/*.csproj</c> of the repository.</summary>
internal static class SourceProjectFiles
{
    private static readonly Lazy<IReadOnlyDictionary<string, SourceProject>> s_all = new(Load);

    public static IReadOnlyDictionary<string, SourceProject> All => s_all.Value;

    private static Dictionary<string, SourceProject> Load()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");

        return Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Select(Parse)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    private static SourceProject Parse(string path)
    {
        var document = XDocument.Load(path);

        var projectReferences = document.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(NormalizeSeparators((string?)e.Attribute("Include") ?? string.Empty)))
            .ToList();

        var packageReferences = document.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToList();

        return new SourceProject(Path.GetFileNameWithoutExtension(path), projectReferences, packageReferences);
    }

    // Project files use Windows separators; make them parseable on Linux too.
    private static string NormalizeSeparators(string path) => path.Replace('\\', Path.DirectorySeparatorChar);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Could not find Pulsar.slnx above {AppContext.BaseDirectory}.");
    }
}
