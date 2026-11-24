using System.Formats.Tar;
using System.IO.Compression;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>Valve's Linux SteamCMD download: only its known files, each with the SHA-256 Pulsar knows.</summary>
public sealed class SteamCmdBootstrapperTests
{
    [LinuxFact]
    public void AFileThatDiffersFromValvesIsRefused()
    {
        var download = Tarball(writer =>
        {
            foreach (var (name, _, _) in LinuxSteamCmdBootstrapper.KnownFiles)
            {
                Add(writer, name, "not Valve's");
            }
        });

        var refused = Assert.Throws<GameProcessException>(() => LinuxSteamCmdBootstrapper.Extract(download));

        Assert.Equal("steamcmd.untrusted", refused.Code);
    }

    [LinuxFact]
    public void ALinkIsNeverReadAsAFile()
    {
        var download = Tarball(writer =>
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "linux32/steamcmd") { LinkName = "/etc/shadow" }));

        var refused = Assert.Throws<GameProcessException>(() => LinuxSteamCmdBootstrapper.Extract(download));

        Assert.Equal("steamcmd.untrusted", refused.Code);
        Assert.Contains("has no linux32/steamcmd", refused.Message, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void AFileTwiceIsRefused()
    {
        var download = Tarball(writer =>
        {
            Add(writer, "steamcmd.sh", "one");
            Add(writer, "./steamcmd.sh", "two");
        });

        Assert.Equal("steamcmd.untrusted", Assert.Throws<GameProcessException>(() => LinuxSteamCmdBootstrapper.Extract(download)).Code);
    }

    [LinuxFact]
    public void WhatIsNotATarballIsADownloadFailure()
    {
        var refused = Assert.Throws<GameProcessException>(() => LinuxSteamCmdBootstrapper.Extract("<html>not found</html>"u8.ToArray()));

        Assert.Equal("steamcmd.download_failed", refused.Code);
    }

    /// <summary>Valve's own download, when the machine has it (PULSAR_STEAMCMD_TARBALL): every file is the one Pulsar knows.</summary>
    [LinuxFact]
    public void ValvesDownloadIsAccepted()
    {
        if (Environment.GetEnvironmentVariable("PULSAR_STEAMCMD_TARBALL") is not { Length: > 0 } path || !File.Exists(path))
        {
            return;
        }

        var files = LinuxSteamCmdBootstrapper.Extract(File.ReadAllBytes(path));

        Assert.Equal(LinuxSteamCmdBootstrapper.KnownFiles.Select(f => f.Name), files.Select(f => f.Name));
    }

    private static byte[] Tarball(Action<TarWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            write(writer);
        }

        return buffer.ToArray();
    }

    private static void Add(TarWriter writer, string name, string content) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)) });
}
