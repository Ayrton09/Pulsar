using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// Files the agent itself reads or writes inside a server directory (configuration files, the game's log, its SteamCMD
/// copy). The agent has more rights than the server's account, which controls that directory, so an implementation
/// never goes through a junction or link out of it and never writes into an existing file (a hard link could make it
/// someone else's): it replaces the file instead.
/// </summary>
public interface IServerFiles
{
    /// <summary>Opens a file for reading, shared with a process writing it; null when it does not exist.</summary>
    Stream? OpenRead(string serverRoot, string relativePath);

    /// <summary>Replaces the file with <paramref name="content"/>, creating its directories.</summary>
    void Write(string serverRoot, string relativePath, Stream content);

    /// <summary>Removes the file if it exists (a link is removed, never its target).</summary>
    void Delete(string serverRoot, string relativePath);

    /// <summary>Creates the directory and its parents if they do not exist.</summary>
    void CreateDirectory(string serverRoot, string relativePath);

    /// <summary>Removes the directory if it is empty; false when it is missing, holds anything, or is a link.</summary>
    bool DeleteEmptyDirectory(string serverRoot, string relativePath);
}

/// <summary>
/// Plain file access with the server path checks of <see cref="InstallSteps.Resolve"/>. For tests and platforms
/// without a stronger implementation: it checks each path component, but cannot rule out a link swapped in between.
/// </summary>
public sealed class DirectServerFiles : IServerFiles
{
    public Stream? OpenRead(string serverRoot, string relativePath)
    {
        var path = InstallSteps.Resolve(serverRoot, relativePath);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
    }

    public void Write(string serverRoot, string relativePath, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = InstallSteps.Resolve(serverRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        content.CopyTo(file);
    }

    public void Delete(string serverRoot, string relativePath) => File.Delete(InstallSteps.Resolve(serverRoot, relativePath));

    public void CreateDirectory(string serverRoot, string relativePath) => Directory.CreateDirectory(InstallSteps.Resolve(serverRoot, relativePath));

    public bool DeleteEmptyDirectory(string serverRoot, string relativePath)
    {
        var directory = new DirectoryInfo(InstallSteps.Resolve(serverRoot, relativePath));
        if (!directory.Exists || directory.LinkTarget is not null || directory.EnumerateFileSystemInfos().Any())
        {
            return false;
        }

        directory.Delete(recursive: false);
        return true;
    }
}
