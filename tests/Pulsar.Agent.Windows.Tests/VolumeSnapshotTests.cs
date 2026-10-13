using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Backups;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Contracts;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// Takes real volume snapshots, so it only runs where that is acceptable: an elevated, disposable machine that opts in
/// with <c>PULSAR_SNAPSHOT_TESTS=1</c> (Windows Sandbox, the CI runner). A snapshot is deleted when the test ends, or with
/// the test process.
/// </summary>
public sealed class SnapshotFactAttribute : FactAttribute
{
    public SnapshotFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Takes volume snapshots: needs an elevated, disposable machine with PULSAR_SNAPSHOT_TESTS=1.";
        SkipUnless = nameof(Enabled);
        SkipType = typeof(SnapshotFactAttribute);
    }

    public static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PULSAR_SNAPSHOT_TESTS") == "1"
        && WindowsDirectorySecurity.IsElevated();
}

/// <summary>Backups of running games read a snapshot of their volume (docs/architecture/06, "Backup snapshots").</summary>
public sealed class VolumeSnapshotTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pulsar-snapshot-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly WindowsVolumeSnapshots _snapshots = new(NullLogger<WindowsVolumeSnapshots>.Instance);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [WindowsFact]
    public void ADirectoryIsFoundOnItsVolumeByTheVolumesName()
    {
        var (volume, relative) = WindowsVolumeSnapshots.Locate(_root);

        Assert.Matches(@"^\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\\z", volume);
        // The path on the volume is the long one: TEMP may be in 8.3 form (C:\Users\RUNNER~1), the two last names are not.
        Assert.EndsWith(Path.Join("pulsar-snapshot-tests", Path.GetFileName(_root)), relative, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Join(Path.GetPathRoot(_root), relative)), relative); // below the drive's root
    }

    [WindowsFact]
    public async Task AnAgentWithoutAdministratorRightsCannotTakeOne()
    {
        // Elevated, this would take a real snapshot of a developer's disk: that is the opt-in test below.
        Assert.SkipWhen(WindowsDirectorySecurity.IsElevated(), "Elevated: a snapshot would be taken.");

        var refused = await Assert.ThrowsAsync<SnapshotException>(() => _snapshots.TakeAsync(_root, TestContext.Current.CancellationToken));

        Assert.Equal(BackupSnapshotErrors.NotPermitted, refused.Code);
    }

    [WindowsFact]
    public async Task AMissingDirectoryHasNoSnapshot()
    {
        var refused = await Assert.ThrowsAsync<SnapshotException>(() =>
            _snapshots.TakeAsync(Path.Combine(_root, "missing"), TestContext.Current.CancellationToken));

        Assert.Equal(BackupSnapshotErrors.Failed, refused.Code);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), BackupSnapshotErrors.NotPermitted)]
    [InlineData(unchecked((int)0x8004230C), BackupSnapshotErrors.Unsupported)]
    [InlineData(unchecked((int)0x8004231F), BackupSnapshotErrors.NoSpace)]
    [InlineData(unchecked((int)0x80042317), BackupSnapshotErrors.NoSpace)]
    [InlineData(unchecked((int)0x80042316), BackupSnapshotErrors.Busy)]
    [InlineData(unchecked((int)0x80070422), BackupSnapshotErrors.Unavailable)]
    [InlineData(unchecked((int)0x80042302), BackupSnapshotErrors.Failed)]
    public void WhatWindowsAnswersIsToldInTheBackupsTerms(int hresult, string reason) => Assert.Equal(reason, WindowsVolumeSnapshots.Reason(hresult));

    [WindowsFact]
    public void AServerDirectoryCanBeNamedInTheLongForm()
    {
        // A snapshot's directory is \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\...: the file manager reads such paths.
        Directory.CreateDirectory(Path.Combine(_root, "world"));
        File.WriteAllText(Path.Combine(_root, "world", "level.dat"), "the world");
        var files = new WindowsServerFileManager(new ServerImpersonation(TestAccounts.Create(), TimeProvider.System, development: true));
        var server = new ProvisionedServer(@"\\?\" + _root, ProcessIdentity.CurrentUser);

        Assert.Contains(files.Walk(server), e => e.Path == "world/level.dat");
        using var opened = files.OpenRead(server, "world/level.dat", 100);
        Assert.Equal("the world", new StreamReader(opened.Content).ReadToEnd());
        Assert.Equal(FileErrors.InvalidPath, Assert.Throws<FileManagerException>(() => files.OpenRead(server, "world/../world/level.dat", 100)).Code);
    }

    [SnapshotFact]
    public async Task ASnapshotHoldsTheFilesAsTheyWereIncludingOnesTheGameHasOpen()
    {
        Directory.CreateDirectory(Path.Combine(_root, "world"));
        File.WriteAllText(Path.Combine(_root, "world", "level.dat"), "before");
        await using var held = new FileStream(Path.Combine(_root, "world", "session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        held.Write("locked"u8);
        held.Flush(flushToDisk: true);
        var files = new WindowsServerFileManager(new ServerImpersonation(TestAccounts.Create(), TimeProvider.System, development: true));

        string directory;
        await using (var snapshot = await _snapshots.TakeAsync(_root, TestContext.Current.CancellationToken))
        {
            directory = snapshot.Directory;
            File.WriteAllText(Path.Combine(_root, "world", "level.dat"), "after");
            var server = new ProvisionedServer(snapshot.Directory, ProcessIdentity.CurrentUser);

            Assert.StartsWith(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy", snapshot.Directory, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["world/level.dat", "world/session.lock"], files.Walk(server).Where(e => e.Kind == FileEntryKind.File).Select(e => e.Path).Order());
            Assert.Equal("before", Read(files, server, "world/level.dat"));
            Assert.Equal("locked", Read(files, server, "world/session.lock")); // live, the game's lock keeps it out of a backup
            Assert.ThrowsAny<Exception>(() => files.Create(server, "world/new.dat", 1, overwrite: true, expectedVersion: null));
        }

        Assert.False(Directory.Exists(directory));
    }

    private static string Read(WindowsServerFileManager files, ProvisionedServer server, string path)
    {
        using var opened = files.OpenRead(server, path, 1024);
        return new StreamReader(opened.Content).ReadToEnd();
    }
}
