using System.Security.Principal;
using Pulsar.Agent.Windows;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>Who may update Pulsar from a node, and which folder the release may be unpacked in (Phase 19).</summary>
public sealed class UpdatePlatformTests
{
    [Fact]
    public void OnlyTheServiceRunningAsSystemUpdates()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert.SkipWhen(identity.IsSystem, "Runs as SYSTEM (the CI step for SYSTEM): the refusal of other accounts is what is tested here.");

        Assert.Equal("update.not_service", new WindowsUpdatePlatform(() => false).CannotUpdate()?.Code);
    }

    [Fact]
    public void AFolderOthersCanChangeIsNotPrivate()
    {
        // A temporary folder inherits the user's own rules: not SYSTEM's and Administrators' alone.
        var folder = Directory.CreateTempSubdirectory("pulsar-update-platform-");
        try
        {
            Assert.False(WindowsUpdatePlatform.IsPrivate(folder));
            var platform = new WindowsUpdatePlatform(() => false);
            var refused = Assert.Throws<InvalidOperationException>(() => platform.PrepareFolder(folder.FullName, "1.0.1-0000"));
            // Refused before anything is made: here a folder above it is the user's to replace, and the folder itself theirs.
            Assert.Matches("could be (changed|replaced) by accounts other than SYSTEM", refused.Message);
            Assert.False(Directory.Exists(Path.Combine(folder.FullName, "1.0.1-0000")));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
