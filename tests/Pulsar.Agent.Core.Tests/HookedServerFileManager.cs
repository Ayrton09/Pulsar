using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>A file manager that runs what the test says before passing a read or a delete on (a failure, a server that starts).</summary>
internal sealed class HookedServerFileManager(IServerFileManager inner) : IServerFileManager
{
    /// <summary>Called with the path of each file opened for reading.</summary>
    public Action<string>? OnRead { get; set; }

    /// <summary>Called with the path of each entry about to be deleted; throwing keeps it.</summary>
    public Action<string>? OnDelete { get; set; }

    public FileListResult List(ProvisionedServer server, string path) => inner.List(server, path);

    public void CreateDirectory(ProvisionedServer server, string path) => inner.CreateDirectory(server, path);

    public void EnsureDirectory(ProvisionedServer server, string path) => inner.EnsureDirectory(server, path);

    /// <summary>Changes what a walk lists (an entry that differs from another only by case, as a case-sensitive folder can hold).</summary>
    public Func<IReadOnlyList<TreeEntry>, IReadOnlyList<TreeEntry>>? OnWalk { get; set; }

    public IReadOnlyList<TreeEntry> Walk(ProvisionedServer server) => OnWalk is { } change ? change(inner.Walk(server)) : inner.Walk(server);

    public void Move(ProvisionedServer server, string path, string destination) => inner.Move(server, path, destination);

    public void Delete(ProvisionedServer server, string path)
    {
        OnDelete?.Invoke(path);
        inner.Delete(server, path);
    }

    /// <summary>Replaces the content of each file opened for reading (one that changes while it is read).</summary>
    public Func<string, Stream, Stream>? OnOpened { get; set; }

    public OpenedFile OpenRead(ProvisionedServer server, string path, long maxSize)
    {
        OnRead?.Invoke(path);
        var opened = inner.OpenRead(server, path, maxSize);
        return OnOpened is { } replace ? new OpenedFile(replace(path, opened.Content), opened.Info) : opened;
    }

    public PendingFile Create(ProvisionedServer server, string path, long size, bool overwrite, string? expectedVersion) =>
        inner.Create(server, path, size, overwrite, expectedVersion);
}
