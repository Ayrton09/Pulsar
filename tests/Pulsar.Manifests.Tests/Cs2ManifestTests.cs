namespace Pulsar.Manifests.Tests;

public sealed class Cs2ManifestTests
{
    [Fact]
    public void RendersTheGameModeAndTheOptionalToken()
    {
        var manifest = TestManifests.Load("cs2");
        var ports = new Dictionary<string, PortAssignment> { ["game"] = new("0.0.0.0", 27015) };

        var plan = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234",
            new Dictionary<string, string?> { ["gameMode"] = "deathmatch", ["gslt"] = "0123456789ABCDEF0123456789abcdef" }, ports, RconPassword: "r"));

        Assert.Equal("game/bin/win64/cs2.exe", plan.Executable);
        Assert.Equal("game/bin/win64", plan.WorkingDirectory);
        Assert.Equal(
        [
            "-dedicated", "-condebug", "-usercon", "-ip", "0.0.0.0", "-port", "27015", "-maxplayers", "10", "+game_type", "1", "+game_mode", "2",
            "+map", "de_dust2", "+rcon_password", "r", "+sv_setsteamaccount", "0123456789ABCDEF0123456789abcdef",
        ], plan.Arguments);
        Assert.Equal("game/csgo/console.log", plan.Console.LogFile);

        var withoutToken = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64,
            new ServerBinding("abcd1234", new Dictionary<string, string?>(), ports, RconPassword: "r"));
        Assert.DoesNotContain("+sv_setsteamaccount", withoutToken.Arguments);
        Assert.Equal(["+game_type", "0", "+game_mode", "1"], withoutToken.Arguments.Skip(9).Take(4));
    }
}
