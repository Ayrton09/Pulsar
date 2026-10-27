namespace Pulsar.Manifests.Tests;

/// <summary>The values the agent masks in a server's console (<see cref="LaunchPlan.Secrets"/>).</summary>
public sealed class ConsoleSecretsTests
{
    private static readonly Dictionary<string, PortAssignment> s_ports = new()
    {
        ["game"] = new(PortAssignment.AnyAddress, 9876),
        ["query"] = new(PortAssignment.AnyAddress, 9877),
        ["rcon"] = new(PortAssignment.AnyAddress, 25575),
    };

    [Fact]
    public void ThePlanListsTheSecretVariablesAndTheRconPassword()
    {
        var plan = Render(new Dictionary<string, string?> { ["password"] = "hunter22", ["name"] = "Not a secret" });

        Assert.Equal(["hunter22", "generated-rcon-password"], plan.Secrets);
    }

    [Fact]
    public void ShortSecretsAreNotMaskedSinceTheyWouldHideOrdinaryText()
    {
        Assert.Equal(["generated-rcon-password"], Render(new Dictionary<string, string?> { ["password"] = "abc" }).Secrets);
        Assert.Equal(["generated-rcon-password"], Render(new Dictionary<string, string?>()).Secrets);
    }

    private static LaunchPlan Render(Dictionary<string, string?> values) =>
        LaunchPlanRenderer.Render(TestManifests.Load("vrising"), ManifestValidator.WindowsX64,
            new ServerBinding("abcd1234", values, s_ports, RconPassword: "generated-rcon-password"));
}
