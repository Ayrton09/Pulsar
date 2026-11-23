using System.Runtime.Versioning;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux;

/// <summary>
/// The files the agent itself reads and writes in a server's folder on Linux (configuration files, the game's log, its
/// SteamCMD): as the folder's owner, the server's user (<see cref="ServerFileIdentity.RunAsOwnerOf"/>), with the path
/// checks of the portable implementation on top. A link the server put there leads only where its user may go, and what
/// the agent writes is the server's.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxServerFiles(IServerFiles? inner = null) : IServerFiles
{
    private readonly IServerFiles _inner = inner ?? new DirectServerFiles();

    public Stream? OpenRead(string serverRoot, string relativePath) =>
        ServerFileIdentity.RunAsOwnerOf(serverRoot, () => _inner.OpenRead(serverRoot, relativePath));

    public void Write(string serverRoot, string relativePath, Stream content) =>
        Do(serverRoot, () => _inner.Write(serverRoot, relativePath, content));

    public void Delete(string serverRoot, string relativePath) => Do(serverRoot, () => _inner.Delete(serverRoot, relativePath));

    public void CreateDirectory(string serverRoot, string relativePath) => Do(serverRoot, () => _inner.CreateDirectory(serverRoot, relativePath));

    public bool DeleteEmptyDirectory(string serverRoot, string relativePath) =>
        ServerFileIdentity.RunAsOwnerOf(serverRoot, () => _inner.DeleteEmptyDirectory(serverRoot, relativePath));

    public IReadOnlyList<(string Name, DateTimeOffset LastWrite)> ListFiles(string serverRoot, string relativeDirectory, int limit) =>
        ServerFileIdentity.RunAsOwnerOf(serverRoot, () => _inner.ListFiles(serverRoot, relativeDirectory, limit));

    public void SetExecutable(string serverRoot, string relativePath) => Do(serverRoot, () => _inner.SetExecutable(serverRoot, relativePath));

    private static void Do(string serverRoot, Action action) => ServerFileIdentity.RunAsOwnerOf(serverRoot, () =>
    {
        action();
        return 0;
    });
}
