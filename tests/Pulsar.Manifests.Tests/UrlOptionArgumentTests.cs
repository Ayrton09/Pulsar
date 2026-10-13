namespace Pulsar.Manifests.Tests;

/// <summary>
/// ARK takes its settings as URL options (<c>Map?listen?SessionName=…?Port=…</c>): an extra argument may set none of
/// the ones Pulsar sets, nor a sensitive one (it would be kept in clear in the server's settings).
/// </summary>
public sealed class UrlOptionArgumentTests
{
    [Theory]
    [InlineData("?ServerAdminPassword=x")]
    [InlineData("?serveradminpassword=x")]
    [InlineData("?ServerPassword=x")]
    [InlineData("?SetServerPassword=x")]
    [InlineData("?Port=7778")]
    [InlineData("?SessionName=Mine")]
    [InlineData("TheCenter_WP?SessionName=Mine")]
    [InlineData("?MaxPlayers=10?ServerAdminPassword=x")]
    [InlineData("?MaxPlayers=10&ServerAdminPassword=x")]
    [InlineData("-log ?ServerAdminPassword=x")]
    [InlineData("?SpectatorPassword=x")]
    [InlineData("-SpectatorPassword=x")]
    public void ExtraArgumentsCannotSetWhatPulsarOrTheGamesSettingsSet(string extra)
    {
        Assert.NotNull(Check([extra]));
    }

    [Theory]
    [InlineData("?AllowCrateSpawnsOnTopOfStructures=True")]
    [InlineData("?MaxPlayers=10")]
    [InlineData("-NoTransferFromFiltering")]
    [InlineData("?QueryPort=27016")] // not a secret
    [InlineData("-QueryPort=27016")]
    public void OtherExtraArgumentsAreAccepted(string extra)
    {
        Assert.Null(Check([extra]));
    }

    private static string? Check(IReadOnlyList<string> extra) => LaunchPlanRenderer.Check(TestManifests.Load("ark-sa"), ManifestValidator.WindowsX64,
        new ServerBinding("abcd1234", new Dictionary<string, string?>(),
            new Dictionary<string, PortAssignment> { ["game"] = new(PortAssignment.AnyAddress, 7777) }, extra, RconPassword: "generated"));
}
