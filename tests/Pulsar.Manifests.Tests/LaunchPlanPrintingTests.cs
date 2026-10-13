namespace Pulsar.Manifests.Tests;

public sealed class LaunchPlanPrintingTests
{
    private const string Secret = "s3cr3t-RC0N-value";

    // Its arguments and environment carry the RCON password, admin passwords and the game server login token.
    [Fact]
    public void ALaunchPlanPrintsNoArgumentOrEnvironmentValue()
    {
        var plan = new LaunchPlan("srcds.exe", @"C:\servers\abcd2345", ["+rcon_password", Secret, "+sv_setsteamaccount", Secret],
            new Dictionary<string, string> { ["TOKEN"] = Secret }, null!, null!, null!)
        {
            Rcon = new RconEndpoint(RconProtocol.Source, "127.0.0.1", 27015, Secret),
        };

        var printed = plan.ToString();

        Assert.DoesNotContain(Secret, printed, StringComparison.Ordinal);
        Assert.Contains("srcds.exe", printed, StringComparison.Ordinal);
        Assert.Contains("4", printed, StringComparison.Ordinal); // the number of arguments
    }
}
