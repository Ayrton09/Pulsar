using System.Text;
using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>The repository's manifests, and variations of them for tests.</summary>
internal static class TestManifests
{
    public static GameManifest Load(string game) => Parse(File.ReadAllText(PathOf(game)));

    public static GameManifest Parse(string json)
    {
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(json));
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        return result.Manifest!;
    }

    public static IReadOnlyList<ManifestError> Errors(string json)
    {
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(json));
        Assert.NotEmpty(result.Errors);
        return result.Errors;
    }

    public static string Change(string game, Action<JsonObject> change)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(PathOf(game)))!.AsObject();
        change(manifest);
        return manifest.ToJsonString();
    }

    public static string PathOf(string game) => Path.Combine(RepositoryRoot(), "games", game, "manifest.json");

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException($"Could not find Pulsar.slnx above {AppContext.BaseDirectory}.");
    }
}
