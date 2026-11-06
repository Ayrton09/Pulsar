using Pulsar.Application.Abstractions;

namespace Pulsar.Api.Tests.Infrastructure;

public sealed class CommandTurnsTests
{
    [Fact]
    public async Task ACanceledWaiterDoesNotFreeTheOwnersTurnOrBlockItsSuccessor()
    {
        var turns = new CommandTurns<string>();
        using var first = await turns.AcquireAsync("server", TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        var canceled = turns.AcquireAsync("server", cancel.Token);
        var next = turns.AcquireAsync("server", TestContext.Current.CancellationToken);
        using var other = await turns.AcquireAsync("other-server", TestContext.Current.CancellationToken);
        Assert.False(next.IsCompleted);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);
        Assert.False(next.IsCompleted);
        first.Dispose();
        first.Dispose(); // a repeated dispose never releases the next owner's turn
        using var second = await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var third = turns.AcquireAsync("server", TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted);
        second.Dispose();
        using var last = await third.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
