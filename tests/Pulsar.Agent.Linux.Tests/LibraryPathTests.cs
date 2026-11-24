using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>A Linux game's own libraries (a manifest's libraryPath): folders of its server only, as LD_LIBRARY_PATH.</summary>
public sealed class LibraryPathTests
{
    private const string Root = "/var/lib/pulsar/servers/abcd2345";

    [LinuxFact]
    public void TheFoldersAreTheServersOwnInOrder()
    {
        Assert.Equal($"{Root}/bin:{Root}/bin/linux64", LinuxGameProcessManager.LibraryPath(Root, ["bin", "bin/linux64"]));
        Assert.Equal($"{Root}:{Root}/bin", LinuxGameProcessManager.LibraryPath(Root, [".", "bin"]));
        Assert.Null(LinuxGameProcessManager.LibraryPath(Root, null));
        Assert.Null(LinuxGameProcessManager.LibraryPath(Root, []));
    }

    [LinuxTheory]
    [InlineData("../abcd9999/bin")]
    [InlineData("/usr/lib")]
    [InlineData("bin/../..")]
    [InlineData("bin:/tmp")]
    [InlineData("bin;/tmp")]
    [InlineData("")]
    public void AFolderOutsideTheServerIsRefused(string folder)
    {
        Assert.Throws<GameProcessException>(() => LinuxGameProcessManager.LibraryPath(Root, [folder]));
    }

    [LinuxFact]
    public void TheGameGetsItAndAManifestCannotSetItItself()
    {
        var account = new LinuxAccount("pulsar_abcd2345", 990, 990, Root);

        var environment = LinuxGameProcessManager.BuildEnvironment(new Dictionary<string, string>(), account, Root, null, $"{Root}/bin");

        Assert.Equal($"{Root}/bin", environment["LD_LIBRARY_PATH"]);
        Assert.Throws<GameProcessException>(() =>
            LinuxGameProcessManager.BuildEnvironment(new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = "/tmp" }, account, Root, null));
    }
}
