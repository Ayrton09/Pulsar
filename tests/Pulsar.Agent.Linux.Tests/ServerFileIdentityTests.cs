using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>
/// The agent (root) reaches a server's folder as the server's user: what that user may not open, the agent does not
/// open for it either, even through a path its checks cannot tell from an ordinary file (a hard link); and what it makes
/// there is the server's.
/// </summary>
[Collection(RootTests.Name)]
public sealed class ServerFileIdentityTests : IDisposable
{
    private readonly string _servers = Path.Combine(LinuxOnly.NewFolder(), "servers");
    private readonly LinuxServerAccounts _accounts = new(NullLogger<LinuxServerAccounts>.Instance);
    private readonly string _shortId = "lf" + Guid.NewGuid().ToString("N")[..8];
    private ProvisionedServer? _server;

    private LinuxServerProvisioner Provisioner => new(_accounts, development: false, NullLogger<LinuxServerProvisioner>.Instance);

    private ProvisionedServer Server => _server ??= Provisioner.Provision(_servers, _shortId);

    private LinuxAccount Account => _accounts.Lookup(LinuxServerAccounts.AccountName(_shortId))!;

    private LinuxServerFileManager Files => new(new ServerFileIdentity(_accounts, development: false, TimeProvider.System));

    public void Dispose()
    {
        if (_server is not null)
        {
            Provisioner.Deprovision(_servers, _shortId);
        }
    }

    [LinuxRootFact]
    public void AFileOnlyRootMayReadIsNotReadThroughTheServersFolder()
    {
        // A secret of root's, linked into the server's folder (root can make that link; the kernel keeps users from it).
        var secret = Path.Combine(LinuxOnly.NewFolder(), "secret.txt");
        File.WriteAllText(secret, "root's");
        File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        SystemdUnits.Run("ln", [secret, Path.Combine(Server.Directory, "secret.txt")]);

        var refused = Assert.ThrowsAny<Exception>(() => Files.OpenRead(Server, "secret.txt", 1024).Dispose());
        Assert.True(refused is FileManagerException or UnauthorizedAccessException, refused.ToString());
        Assert.Throws<UnauthorizedAccessException>(() => new LinuxServerFiles().OpenRead(Server.Directory, "secret.txt")?.Dispose());

        // And this thread is root's again: it reads the secret itself.
        Assert.Equal("root's", File.ReadAllText(secret));
    }

    [LinuxRootFact]
    public void WhatTheAgentMakesInAServersFolderIsTheServers()
    {
        Files.CreateDirectory(Server, "cfg");
        using (var pending = Files.Create(Server, "cfg/server.cfg", 5, overwrite: false, expectedVersion: null))
        {
            pending.Content.Write("hello"u8);
            pending.Commit();
        }

        new LinuxServerFiles().Write(Server.Directory, "cfg/other.cfg", new MemoryStream("x"u8.ToArray()));

        foreach (var path in (string[])["cfg", "cfg/server.cfg", "cfg/other.cfg"])
        {
            var stat = Libc.Lstat(Path.Combine(Server.Directory, path))!.Value;
            Assert.Equal((Account.Uid, Account.Gid), (stat.Uid, stat.Gid));
        }

        Assert.Equal("hello", File.ReadAllText(Path.Combine(Server.Directory, "cfg", "server.cfg")));
    }

    [LinuxRootFact]
    public void AFolderTheServersUserCannotEnterIsNotListedForIt()
    {
        var closed = Path.Combine(Server.Directory, "closed");
        Directory.CreateDirectory(closed);
        File.WriteAllText(Path.Combine(closed, "inside.txt"), "x");
        File.SetUnixFileMode(closed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); // root's 0700

        Assert.ThrowsAny<Exception>(() => Files.List(Server, "closed"));
    }

    [LinuxRootFact]
    public void RootsOwnFoldersAreNotReachedAsAServer()
    {
        var folder = LinuxOnly.NewFolder();

        Assert.Throws<UnauthorizedAccessException>(() => new LinuxServerFiles().ListFiles(folder, "", 10));
    }

    [LinuxRootFact]
    public void AServerWithoutItsOwnAccountIsNotReachedAsRoot()
    {
        var identity = new ServerFileIdentity(_accounts, development: false, TimeProvider.System);

        var refused = Assert.Throws<FileManagerException>(() => identity.Run(ProcessIdentity.CurrentUser, () => 0));
        Assert.Equal(FileErrors.AccessDenied, refused.Code);
    }
}
