using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Scheduling;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Servers;

/// <summary>A run is deleted with its task or server: one taken before that is dropped, and the rest of its pass goes on.</summary>
public sealed class SchedulerDeletedRunTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task ARunDeletedAfterItWasTakenIsSimplyDropped()
    {
        var exception = await Record.ExceptionAsync(() =>
            factory.Services.GetRequiredService<Scheduler>().ExecuteAsync(ScheduledRunId.New(), TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }
}
