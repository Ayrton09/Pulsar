using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Domain.Tests.Nodes;

public sealed class NodeTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x200B)] // zero-width space
    [InlineData(0x202E)] // right-to-left override
    [InlineData(0x2066)] // left-to-right isolate
    [InlineData(0x2028)] // line separator
    [InlineData(0x2029)] // paragraph separator
    [InlineData(0x000A)] // line feed
    [InlineData(0xE0001)] // language tag (a format character outside the BMP, a surrogate pair)
    [InlineData(0xE0041)] // tag Latin capital letter A
    public void ANodesNameHasNothingThatHidesOrBreaksIt(int character)
    {
        var name = "GAME" + char.ConvertFromUtf32(character) + "-01";

        Assert.Equal(NodeErrors.NameInvalid, Node.Create(name, null, null, s_now).Error);
        Assert.Equal(NodeErrors.NameInvalid, Node.Create("GAME-00", null, null, s_now).Value.Update(name, null, null, s_now).Error);
        Assert.True(Node.Create("Nodo Córdoba 2", null, null, s_now).IsSuccess);
        Assert.True(Node.Create("GAME \U0001F680 01", null, null, s_now).IsSuccess); // a symbol outside the BMP is shown
    }
}
