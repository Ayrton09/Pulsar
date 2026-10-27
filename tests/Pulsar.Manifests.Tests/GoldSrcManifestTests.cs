using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>GoldSrc's RCON over UDP, through the Counter-Strike 1.6 and Half-Life Deathmatch manifests.</summary>
public sealed class GoldSrcManifestTests
{
    private const string Rcon = "Rc0nPassw0rdRc0nPassw0rdRc0nPass";

    [Theory]
    [InlineData("cs16", "cstrike")]
    [InlineData("hldm", "valve")]
    public void RendersHldsWithGoldSrcRcon(string game, string folder)
    {
        var plan = LaunchPlanRenderer.Render(TestManifests.Load(game), ManifestValidator.WindowsX64, new ServerBinding("abcd1234",
            new Dictionary<string, string?>(), new Dictionary<string, PortAssignment> { ["game"] = new("0.0.0.0", 27115) }, RconPassword: Rcon));

        Assert.Equal("hlds.exe", plan.Executable);
        Assert.Equal(["-console", "-game", folder], plan.Arguments.Take(3));
        Assert.Equal(new RconEndpoint(RconProtocol.GoldSrc, "127.0.0.1", 27115, Rcon), plan.Rcon);
        Assert.Equal("qconsole.log", plan.Console.LogFile);
        Assert.Contains(Rcon, plan.Secrets);
        Assert.Equal($"{folder}/server.cfg", Assert.Single(plan.ConfigFiles).Path);
        Assert.Equal("quit", Assert.IsType<RconCommandStop>(Assert.Single(plan.Stop.Steps)).Command);
        Assert.True(ManifestFeatures.UsesGoldSrcRcon(TestManifests.Load(game)));
        Assert.False(ManifestFeatures.UsesGoldSrcRcon(TestManifests.Load("css")));
    }

    [Fact]
    public void ItsRconNeedsTheGamePortsUdp()
    {
        var errors = TestManifests.Errors(TestManifests.Change("cs16", m => m["ports"]![0]!["protocols"] = new JsonArray("tcp")));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.console.rcon.port");
    }

    [Theory]
    [InlineData("mode", "pipes")]
    [InlineData("logFile", "logs/*.log")]
    public void ItsRconNeedsALogFileNamedInFull(string property, string value)
    {
        // The log is kept from whoever reads the server's files by its name: the game writes the password there.
        var errors = TestManifests.Errors(TestManifests.Change("cs16", m => m["runtime"]!["windows-x64"]!["console"]![property] = value));

        Assert.Contains(errors, e => e.Path.StartsWith("runtime.windows-x64.console", StringComparison.Ordinal));
    }

    [Fact]
    public void ItsLogIsNeverShownInPlaceOfTheAgentsCopy()
    {
        var errors = TestManifests.Errors(TestManifests.Change("cs16", m => m["runtime"]!["windows-x64"]!["console"]!["rcon"]!["logsCommands"] = true));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.console.rcon.logsCommands");
    }

    [Theory]
    [InlineData("cstrike/logs/*.log")]
    [InlineData(".pulsar/steamcmd")]
    [InlineData("../logs")]
    public void PrivatePathsAreTheServersOwnNamedInFull(string value)
    {
        var errors = TestManifests.Errors(TestManifests.Change("cs16", m => m["runtime"]!["windows-x64"]!["console"]!["privatePaths"] = new JsonArray(value)));

        Assert.Contains(errors, e => e.Path.StartsWith("runtime.windows-x64.console.privatePaths", StringComparison.Ordinal));
    }

    [Fact]
    public void PrivatePathsBelongToGoldSrcsRcon()
    {
        var errors = TestManifests.Errors(TestManifests.Change("css", m => m["runtime"]!["windows-x64"]!["console"]!["privatePaths"] = new JsonArray("cstrike/logs")));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.console.privatePaths");
    }

    [Fact]
    public void TheProtocolIsWrittenGoldsrc()
    {
        Assert.Equal(RconProtocol.GoldSrc, TestManifests.Load("hldm").Runtime[ManifestValidator.WindowsX64].Console.Rcon!.Protocol);
        Assert.Contains("\"goldsrc\"", File.ReadAllText(TestManifests.PathOf("hldm")), StringComparison.Ordinal);
    }
}
