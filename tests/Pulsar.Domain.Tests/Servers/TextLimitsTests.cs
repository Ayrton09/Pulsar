using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests.Servers;

public sealed class TextLimitsTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abc", 10, "abc")]
    [InlineData("ab😀cd", 3, "ab")] // the cut would fall between the two halves of the emoji
    [InlineData("ab😀cd", 4, "ab😀")]
    [InlineData("😀", 1, "")]
    [InlineData("", 0, "")]
    public void ATextIsNeverCutInTheMiddleOfACharacter(string text, int max, string expected)
    {
        var cut = TextLimits.Truncate(text, max);

        Assert.Equal(expected, cut);
        Assert.False(cut.Length > 0 && char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void AnOperationsStageAndErrorKeepWholeCharacters()
    {
        var operation = Operation.Create(OperationType.ServerInstall, NodeId.New(), null, null, OperationRequester.System, s_now);

        operation.Progressed(new string('a', Operation.MaxStageLength - 1) + "😀", 5);
        Assert.Equal(new string('a', Operation.MaxStageLength - 1), operation.ProgressStage);

        operation.Complete(OperationStatus.Failed, "x", new string('a', Operation.MaxErrorMessageLength - 1) + "😀", s_now);
        Assert.Equal(new string('a', Operation.MaxErrorMessageLength - 1), operation.ErrorMessage);
    }
}
