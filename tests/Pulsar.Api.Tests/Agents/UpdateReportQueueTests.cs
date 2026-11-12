using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Api.Agents;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>A node's update reports run off its hub invocation, one after another in the order they arrived.</summary>
public sealed class UpdateReportQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFullNodeQueueRejectsMoreWorkAndOtherNodesStillProceed()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var node = new NodeId(Guid.NewGuid());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var queue = new UpdateReportQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UpdateReportQueue>.Instance)
        {
            Handler = async (id, _) =>
            {
                if (id != node)
                {
                    otherDone.TrySetResult();
                    return;
                }

                await release.Task;
                if (Interlocked.Increment(ref count) == UpdateReportQueue.MaximumPerNode)
                {
                    drained.TrySetResult();
                }
            },
        };
        try
        {
            for (var i = 0; i < UpdateReportQueue.MaximumPerNode; i++)
            {
                Assert.True(queue.Enqueue(node, Report(UpdateStatuses.Downloading)));
            }

            Assert.False(queue.Enqueue(node, Report(UpdateStatuses.Succeeded)));
            Assert.True(queue.Enqueue(new NodeId(Guid.NewGuid()), Report(UpdateStatuses.Succeeded)));
            await otherDone.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        finally
        {
            release.TrySetResult();
        }

        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task OnlyConsumedBoundedReportFieldsAreRetained()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var received = new TaskCompletionSource<UpdateReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new UpdateReportQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UpdateReportQueue>.Instance)
        {
            Handler = (_, report) => { received.TrySetResult(report); return Task.CompletedTask; },
        };
        Assert.True(queue.Enqueue(new NodeId(Guid.NewGuid()), Report(UpdateStatuses.Failed) with
        {
            From = new string('x', 257),
            Parts = [new string('x', 1024)],
            Error = new string('e', 2048),
        }));
        var report = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Null(report.From);
        Assert.Null(report.Parts);
        Assert.Equal(2000, report.Error!.Length);
        Assert.Equal("1.0.1", report.To);
    }

    [Fact]
    public async Task ReportsOfANodeRunInOrderWhileAnotherNodesDoNotWaitForThem()
    {
        var first = new NodeId(Guid.NewGuid());
        var other = new NodeId(Guid.NewGuid());
        var handled = new List<string?>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new UpdateReportQueue(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UpdateReportQueue>.Instance)
        {
            RetryDelay = TimeSpan.FromMilliseconds(10),
            Handler = async (node, report) =>
            {
                if (node == other)
                {
                    otherDone.SetResult();
                    return;
                }

                // The first one takes its time: the next ones of this node wait for it, in order.
                if (report.Status == UpdateStatuses.Downloading)
                {
                    await release.Task;
                }

                lock (handled)
                {
                    handled.Add(report.Status);
                }

                if (report.Status == UpdateStatuses.Succeeded)
                {
                    throw new InvalidOperationException("A failing report is tried again, then ends only itself.");
                }
            },
        };

        queue.Enqueue(first, Report(UpdateStatuses.Downloading));
        queue.Enqueue(first, Report(UpdateStatuses.Installing));
        queue.Enqueue(first, Report(UpdateStatuses.Succeeded));
        queue.Enqueue(first, Report(UpdateStatuses.Failed));
        queue.Enqueue(other, Report(UpdateStatuses.Failed));
        await otherDone.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        lock (handled)
        {
            Assert.Empty(handled);
        }

        release.SetResult();
        await Eventually(() =>
        {
            lock (handled)
            {
                return handled.Count == 3 + UpdateReportQueue.Attempts;
            }
        });
        Assert.Equal([UpdateStatuses.Downloading, UpdateStatuses.Installing, .. Enumerable.Repeat(UpdateStatuses.Succeeded, UpdateReportQueue.Attempts),
            UpdateStatuses.Failed], handled);
    }

    private static UpdateReport Report(string status) => new(status, "1.0.0", "1.0.1", null, null, DateTimeOffset.UtcNow);

    private static async Task Eventually(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Assert.True(waited < 10_000, "The reports were not all handled.");
            await Task.Delay(20, Ct);
        }
    }
}
