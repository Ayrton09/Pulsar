using System.Runtime.Versioning;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Linux;

/// <summary>
/// The file manager on Linux (and SFTP, zips, backups and restores, which go through it): the portable implementation,
/// every operation of it run as the server's user (<see cref="ServerFileIdentity"/>), what finishes later (an upload's
/// commit) included. The kernel checks each file against that user's rights, so a link or a folder the server swapped in
/// meanwhile never takes the agent anywhere its user could not go.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxServerFileManager(ServerFileIdentity identity, IServerFileManager? inner = null) : IServerFileManager
{
    private readonly IServerFileManager _inner = inner ?? new DirectServerFileManager();

    public FileListResult List(ProvisionedServer server, string path) => As(server, () => _inner.List(server, path));

    public FileListResult ListPage(ProvisionedServer server, string path, string? after) => As(server, () => _inner.ListPage(server, path, after));

    public FileEntryResult Stat(ProvisionedServer server, string path) => As(server, () => _inner.Stat(server, path));

    public FileEntryResult SetModified(ProvisionedServer server, string path, DateTimeOffset modifiedAt) =>
        As(server, () => _inner.SetModified(server, path, modifiedAt));

    public void CreateDirectory(ProvisionedServer server, string path) => Do(server, () => _inner.CreateDirectory(server, path));

    public void EnsureDirectory(ProvisionedServer server, string path) => Do(server, () => _inner.EnsureDirectory(server, path));

    public TreeWalk Walk(ProvisionedServer server) => As(server, () => _inner.Walk(server));

    public void Move(ProvisionedServer server, string path, string destination, bool replace = false) =>
        Do(server, () => _inner.Move(server, path, destination, replace));

    public void Delete(ProvisionedServer server, string path) => Do(server, () => _inner.Delete(server, path));

    // The file is opened as the server's user; reading what was opened needs nobody's rights.
    public OpenedFile OpenRead(ProvisionedServer server, string path, long maxSize) => As(server, () => _inner.OpenRead(server, path, maxSize));

    public PendingFile Create(ProvisionedServer server, string path, long size, bool overwrite, string? expectedVersion, bool sizeIsLimit = false) =>
        new AsUser(this, server, As(server, () => _inner.Create(server, path, size, overwrite, expectedVersion, sizeIsLimit)));

    public PendingFile Append(ProvisionedServer server, string path, string? expectedVersion) =>
        new AsUser(this, server, As(server, () => _inner.Append(server, path, expectedVersion)));

    private T As<T>(ProvisionedServer server, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(server);
        return identity.Run(server.Identity, action);
    }

    private void Do(ProvisionedServer server, Action action) => As(server, () =>
    {
        action();
        return 0;
    });

    /// <summary>A file being written: its content goes to what was opened, its commit and its abandon as the server's user.</summary>
    private sealed class AsUser(LinuxServerFileManager owner, ProvisionedServer server, PendingFile inner) : PendingFile
    {
        public override Stream Content => inner.Content;

        public override FileEntryResult Commit() => owner.As(server, inner.Commit);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Do(server, inner.Dispose);
            }
        }
    }
}
