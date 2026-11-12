using System.Text;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Tests;

public sealed class BoundedFileContentsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(512)]
    [InlineData(4096)]
    public void ExactBudgetIsAcceptedAndAnExcessByteIsRejected(int limit)
    {
        using var exact = new MemoryStream(new byte[limit]);
        using var result = BoundedFileContents.Read(exact, limit);
        Assert.NotNull(result);
        Assert.Equal(limit, result.Length);
        Assert.Equal(0, result.Position);
        Assert.True(exact.CanRead);

        using var excess = new MemoryStream(new byte[limit + 32]);
        Assert.Null(BoundedFileContents.Read(excess, limit));
        Assert.Equal(limit + 1, excess.Position);
    }

    [Fact]
    public void ConsumptionDoesNotTrustTheSourcesLengthProperty()
    {
        using var source = new MisreportedLength(Encoding.UTF8.GetBytes(new string('x', 513)));
        Assert.Equal(1, source.Length);
        Assert.Null(BoundedFileContents.Read(source, 512));
        Assert.Equal(513, source.Position);
    }

    private sealed class MisreportedLength(byte[] bytes) : MemoryStream(bytes)
    {
        public override long Length => 1;
    }
}
