using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// The Steam client's libraries copied beside a game from its SteamCMD (1.0.25): Counter-Strike 2 cannot start without
/// them on a node whose server account has no Steam client.
/// </summary>
public sealed class SteamClientCopyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-steamclient-").FullName;
    private readonly FakeSignatures _signatures = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void CopiesEachLibraryOnceItIsSignedByValveAndReplacesOlderOnes()
    {
        SteamCmdHas("new");
        Directory.CreateDirectory(Path.Combine(_root, "game", "bin", "win64"));
        File.WriteAllText(Path.Combine(_root, "game", "bin", "win64", "steamclient64.dll"), "old");

        Steps().CopySteamClient(_root, "game/bin/win64");

        foreach (var name in InstallSteps.SteamClientFiles)
        {
            Assert.Equal($"{name} new", File.ReadAllText(Path.Combine(_root, "game", "bin", "win64", name)));
        }

        // Checked on the agent's own copies, which are gone, as Valve's.
        Assert.Equal(3, _signatures.Checked.Count);
        Assert.All(_signatures.Checked, c => Assert.False(File.Exists(c.Path)));
        Assert.All(_signatures.Checked, c => Assert.False(c.Path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)));
        Assert.All(_signatures.Checked, c => Assert.Equal("Valve Corp.", c.Organization));
    }

    [Fact]
    public void ALibraryNotSignedByValveIsNotCopied()
    {
        SteamCmdHas("planted");
        _signatures.Signed = false;

        var error = Assert.Throws<GameProcessException>(() => Steps().CopySteamClient(_root, "game/bin/win64"));

        Assert.Equal("steamclient.untrusted", error.Code);
        Assert.False(File.Exists(Path.Combine(_root, "game", "bin", "win64", "steamclient64.dll")));
        // Both of Valve's names were asked for.
        Assert.Equal(["Valve Corp.", "Valve"], _signatures.Checked.Select(c => c.Organization));
    }

    [Fact]
    public void AMissingLibraryFailsTheInstall()
    {
        SteamCmdHas("new");
        File.Delete(Path.Combine(_root, ".pulsar", "steamcmd", "vstdlib_s64.dll"));

        var error = Assert.Throws<GameProcessException>(() => Steps().CopySteamClient(_root, "game/bin/win64"));

        Assert.Equal("steamclient.missing", error.Code);
    }

    [Fact]
    public void AnAgentThatCannotCheckSignaturesCopiesNothing()
    {
        SteamCmdHas("new");

        var error = Assert.Throws<GameProcessException>(() => new InstallSteps(new InstallOptions(), new DirectServerFiles()).CopySteamClient(_root, "game/bin/win64"));

        Assert.Equal("steamclient.untrusted", error.Code);
        Assert.False(Directory.Exists(Path.Combine(_root, "game")));
    }

    [Fact]
    public void ATargetOutsideTheServerIsRefused()
    {
        SteamCmdHas("new");

        var error = Assert.Throws<GameProcessException>(() => Steps().CopySteamClient(_root, "../elsewhere"));

        Assert.Equal("install.invalid_path", error.Code);
    }

    private InstallSteps Steps() => new(new InstallOptions { CodeSignatures = _signatures }, new DirectServerFiles());

    private void SteamCmdHas(string version)
    {
        var steamCmd = Path.Combine(_root, ".pulsar", "steamcmd");
        Directory.CreateDirectory(steamCmd);
        foreach (var name in InstallSteps.SteamClientFiles)
        {
            File.WriteAllText(Path.Combine(steamCmd, name), $"{name} {version}");
        }
    }

    private sealed class FakeSignatures : ICodeSignatures
    {
        public bool Signed { get; set; } = true;

        public List<(string Path, string Organization)> Checked { get; } = [];

        public bool IsSignedBy(string path, string organization)
        {
            Checked.Add((path, organization));
            return Signed && File.Exists(path);
        }
    }
}
