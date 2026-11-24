using System.Runtime.Versioning;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;

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

    /// <summary>
    /// As the portable one, but only a regular file is opened, and without waiting: a named pipe the server's account
    /// left at the path would otherwise hold the agent's thread until something writes to it.
    /// </summary>
    public Stream? OpenRead(string serverRoot, string relativePath) => ServerFileIdentity.RunAsOwnerOf(serverRoot, () =>
        _inner is DirectServerFiles ? OpenRegular(InstallSteps.Resolve(serverRoot, relativePath)) : _inner.OpenRead(serverRoot, relativePath));

    public void Write(string serverRoot, string relativePath, Stream content) =>
        Do(serverRoot, () => _inner.Write(serverRoot, relativePath, content));

    public void Delete(string serverRoot, string relativePath) => Do(serverRoot, () => _inner.Delete(serverRoot, relativePath));

    public void CreateDirectory(string serverRoot, string relativePath) => Do(serverRoot, () => _inner.CreateDirectory(serverRoot, relativePath));

    public bool DeleteEmptyDirectory(string serverRoot, string relativePath) =>
        ServerFileIdentity.RunAsOwnerOf(serverRoot, () => _inner.DeleteEmptyDirectory(serverRoot, relativePath));

    public IReadOnlyList<(string Name, DateTimeOffset LastWrite)> ListFiles(string serverRoot, string relativeDirectory, int limit) =>
        ServerFileIdentity.RunAsOwnerOf(serverRoot, () => _inner.ListFiles(serverRoot, relativeDirectory, limit));

    public void SetExecutable(string serverRoot, string relativePath) => Do(serverRoot, () => _inner.SetExecutable(serverRoot, relativePath));

    /// <summary>A regular file opened to read, never waiting; null when it does not exist (<see cref="Libc.OpenRegularFile"/>).</summary>
    internal static Stream? OpenRegular(string path)
    {
        if (Libc.OpenRegularFile(path) is not { } handle)
        {
            return null;
        }

        try
        {
            return new FileStream(handle, FileAccess.Read, bufferSize: 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void Do(string serverRoot, Action action) => ServerFileIdentity.RunAsOwnerOf(serverRoot, () =>
    {
        action();
        return 0;
    });
}
