using Pulsar.Agent.Core;
using Pulsar.Contracts;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>One-click updates on Linux: a folder only root can change, the release's tarball and its script in a unit of its own.</summary>
[Collection(RootTests.Name)]
public sealed class LinuxUpdatePlatformTests : IDisposable
{
    // Under /var/lib, root's and closed to others as on any machine: /tmp, open to everyone, is refused.
    private readonly string _base = Path.Combine("/var/lib", "pulsar-update-tests-" + Guid.NewGuid().ToString("N"));

    [LinuxFact]
    public void TheTarballAndScriptAreTheLinuxOnes()
    {
        var platform = new LinuxUpdatePlatform(() => false);

        Assert.Equal("pulsar-1.2.3-linux-x64.tar.gz", platform.ArchiveName(new UpdateVersion(1, 2, 3)));
        Assert.Equal("install-pulsar-agent.sh", platform.InstallerName);
        Assert.Equal("/var/lib/pulsar/updates", platform.UpdatesRoot(new AgentPaths("/srv/elsewhere")));
        Assert.Equal("/var/log/pulsar/setup", platform.SetupLogs(LinuxUpdatePlatform.Root));
    }

    [LinuxTheory]
    [InlineData("Running as unit: pulsar-update-1.service\nFinished with result: exit-code\nMain processes terminated with: code=exited/status=7\n", 0, 7)]
    [InlineData("Finished with result: success\nMain processes terminated with: code=exited/status=0\n", 0, 0)]
    [InlineData("Finished with result: signal\nMain processes terminated with: code=killed/status=TERM\n", 0, 1)]
    [InlineData("", 0, 1)]
    [InlineData("Failed to start transient service unit", 1, 1)]
    public void TheExitCodeIsTheUnits(string said, int own, int expected) => Assert.Equal(expected, LinuxUpdatePlatform.ExitCodeOf(said, own));

    [LinuxRootFact]
    public void OnlyTheServiceTheScriptInstalledUpdates()
    {
        Directory.CreateDirectory(_base);
        var state = Path.Combine(_base, "agent-install.json");
        var development = false;
        LinuxUpdatePlatform Platform(string baseDirectory) =>
            new(() => development) { AgentState = state, PanelState = Path.Combine(_base, "install.json"), BaseDirectory = baseDirectory };

        Assert.Equal("update.not_installed", Platform("/opt/pulsar/agent/").CannotUpdate()?.Code);
        File.WriteAllText(state, "{}");
        Assert.Null(Platform("/opt/pulsar/agent/").CannotUpdate());
        Assert.Equal("update.not_installed", Platform("/srv/my-agent/").CannotUpdate()?.Code);
        development = true;
        Assert.Equal("update.development", Platform("/opt/pulsar/agent/").CannotUpdate()?.Code);

        Assert.False(Platform("/opt/pulsar/agent/").HostsPanel());
        File.WriteAllText(Path.Combine(_base, "install.json"), "{}");
        Assert.True(Platform("/opt/pulsar/agent/").HostsPanel());
    }

    [LinuxRootFact]
    public void ReleasesAreUnpackedInAFolderOnlyRootCanChange()
    {
        Directory.CreateDirectory(_base);
        var platform = new LinuxUpdatePlatform(() => false);
        var root = Path.Combine(_base, "updates");

        var folder = platform.PrepareFolder(root, "1.2.3-abcdef01");

        Assert.Equal(Path.Combine(root, "1.2.3-abcdef01"), folder);
        Assert.Equal((0u, 0x1C0u), (Libc.Lstat(root)!.Value.Uid, Libc.Lstat(root)!.Value.Permissions));
        Assert.Equal((0u, 0x1C0u), (Libc.Lstat(folder)!.Value.Uid, Libc.Lstat(folder)!.Value.Permissions));
        Assert.Throws<IOException>(() => platform.PrepareFolder(root, "1.2.3-abcdef01"));

        // Open to others, or a link: refused.
        File.SetUnixFileMode(root, (UnixFileMode)0x1FF);
        Assert.Throws<InvalidOperationException>(() => platform.PrepareFolder(root, "1.2.3-00000002"));
        var elsewhere = Path.Combine(_base, "elsewhere");
        Directory.CreateDirectory(elsewhere, (UnixFileMode)0x1C0);
        var link = Path.Combine(_base, "linked");
        File.CreateSymbolicLink(link, elsewhere);
        Assert.Throws<InvalidOperationException>(() => platform.PrepareFolder(link, "1.2.3-00000003"));
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));

        // Under a folder others can change: refused before anything is made there.
        var open = LinuxOnly.NewFolder();
        var refused = Assert.Throws<InvalidOperationException>(() => platform.PrepareFolder(Path.Combine(open, "updates"), "1.2.3-00000004"));
        Assert.Contains("could be replaced", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(open, "updates")));
    }

    [LinuxRootFact]
    public async Task TheScriptRunsInAUnitOfItsOwnAndItsExitCodeIsSeen()
    {
        Directory.CreateDirectory(_base, (UnixFileMode)0x1C0);
        var said = Path.Combine(_base, "said.txt");
        var script = Path.Combine(_base, "install-pulsar-agent.sh");
        File.WriteAllText(script, $$"""
            #!/bin/bash
            { printf '%s\n' "$*"; pwd; cat /proc/self/cgroup; } >{{said}}
            exit 3
            """.ReplaceLineEndings("\n"));
        var attempt = Guid.NewGuid();

        var exitCode = await new LinuxUpdatePlatform(() => false).StartInstaller(script, _base, attempt)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(3, exitCode);
        var lines = File.ReadAllLines(said);
        Assert.Equal($"--update --unattended --update-attempt {attempt:D}", lines[0]);
        Assert.Equal(_base, lines[1]);
        // Not in this process's cgroup (the agent service's, which the update stops): its own unit's.
        Assert.Matches(@"/system\.slice/pulsar-update-[0-9a-f]{8}\.service$", lines[2]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }
}
