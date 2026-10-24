namespace Pulsar.Contracts.Tests;

/// <summary>The ports in use on a node's machine (1.0.7): asked by the panel, answered in the command's ack.</summary>
public sealed class PortsInUseContractTests
{
    [Fact]
    public void TheCommandAndItsAnswerTravelAsThemselves()
    {
        AgentCommand command = new ListPortsInUseCommand();
        var ack = new CommandAck(Guid.CreateVersion7(), AckStatus.Accepted, null, null,
            new PortsInUseResult([new PortInUse("0.0.0.0", 27015, "udp"), new PortInUse("::", 8443, "tcp")], Truncated: true));

        var back = ProtocolJson.Deserialize<CommandAck>(ProtocolJson.Serialize(ack));

        Assert.Equal(command, ProtocolJson.Deserialize<AgentCommand>(ProtocolJson.Serialize(command)));
        Assert.Equal("node.ports_in_use", AgentCapabilities.Of(command));
        var result = Assert.IsType<PortsInUseResult>(back.Result);
        Assert.Equal(((PortsInUseResult)ack.Result!).Ports, result.Ports);
        Assert.True(result.Truncated);
    }
}
