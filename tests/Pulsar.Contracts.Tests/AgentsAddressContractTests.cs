using System.Security.Cryptography;

namespace Pulsar.Contracts.Tests;

/// <summary>The agents' address (Phase 16): how the panel tells an agent where to present its client certificate.</summary>
public sealed class AgentsAddressContractTests
{
    [Fact]
    public void TheCommandTravelsAsItselfAndNeedsItsCapability()
    {
        AgentCommand command = new UseAgentsAddressCommand(8444, "https://agents.example.com:9444");

        var back = ProtocolJson.Deserialize<AgentCommand>(ProtocolJson.Serialize(command));

        Assert.Equal(command, back);
        Assert.Equal("node.use_agents_address", AgentCapabilities.Of(command));
    }

    [Fact]
    public void AnErrorFromAnOlderPanelHasNoAddress()
    {
        var json = """{"code":"agent.authentication_rejected","message":"No."}"""u8;

        Assert.Null(ProtocolJson.Deserialize<AgentError>(json).AgentsAddress);
    }

    [Fact]
    public void TheAddressInARefusalIsSignedForItsPurposeOnly()
    {
        using var panel = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = panel.ExportSubjectPublicKeyInfo();
        var payload = ProtocolJson.Serialize(new AgentsAddress(Guid.CreateVersion7(), 8444, null, [1, 2, 3]));
        var signed = new SignedMessage(ProtocolCrypto.Fingerprint(publicKey), payload,
            panel.SignData(ProtocolCrypto.SignedData(AgentProtocol.Purposes.AgentsAddress, payload), HashAlgorithmName.SHA256,
                ProtocolCrypto.SignatureFormat));

        var error = ProtocolJson.Deserialize<AgentError>(ProtocolJson.Serialize(new AgentError("agent.certificate_required", "Use the agents' address.", signed)));

        Assert.True(ProtocolCrypto.VerifyMessage(error.AgentsAddress!, AgentProtocol.Purposes.AgentsAddress, publicKey));
        Assert.False(ProtocolCrypto.VerifyMessage(error.AgentsAddress!, AgentProtocol.Purposes.RotationResult, publicKey));
    }
}
