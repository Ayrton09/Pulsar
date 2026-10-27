using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The plain file access's listing of a folder (a game's logs named by its start, Pulsar 1.0.12).</summary>
public sealed class DirectServerFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-direct-files-").FullName;
    private readonly DirectServerFiles _files = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AFoldersFilesAreListedWithoutItsFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "instance", "Saves"));
        File.WriteAllText(Path.Combine(_root, "instance", "SpaceEngineersDedicated_1.log"), "one");
        File.WriteAllText(Path.Combine(_root, "instance", "SpaceEngineersDedicated_2.log"), "two");
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "x");

        var listed = _files.ListFiles(_root, "instance", 10);

        Assert.Equal(["SpaceEngineersDedicated_1.log", "SpaceEngineersDedicated_2.log"], listed.Select(f => f.Name).Order());
        Assert.All(listed, f => Assert.True(f.LastWrite > DateTimeOffset.UtcNow.AddMinutes(-5)));
        Assert.Equal(["readme.txt"], _files.ListFiles(_root, "", 10).Select(f => f.Name));
        Assert.Empty(_files.ListFiles(_root, "missing", 10));
    }

    [Fact]
    public void AFolderWithMoreEntriesThanTakenIsRefused()
    {
        // The server's account fills the folder as it likes: its folders count too.
        Directory.CreateDirectory(Path.Combine(_root, "logs", "a"));
        Directory.CreateDirectory(Path.Combine(_root, "logs", "b"));
        File.WriteAllText(Path.Combine(_root, "logs", "server.log"), "x");

        Assert.Single(_files.ListFiles(_root, "logs", 3));
        Assert.Throws<IOException>(() => _files.ListFiles(_root, "logs", 2));
    }
}
