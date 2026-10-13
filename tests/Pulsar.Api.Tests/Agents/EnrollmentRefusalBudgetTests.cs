using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Agents;

/// <summary>Enrollments anyone can send refused (an unknown token, a malformed request) fill the audit log only so fast (docs/architecture/08, "Audit").</summary>
public sealed class EnrollmentRefusalBudgetTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheFirstRefusalsOfAMinuteAreRecordedAndTheRestCounted()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var budget = new EnrollmentRefusalBudget(clock, NullLogger<EnrollmentRefusalBudget>.Instance);

        for (var i = 0; i < EnrollmentRefusalBudget.PerMinute; i++)
        {
            Assert.True(budget.TryRecord(out var none));
            Assert.Equal(0, none);
        }

        Assert.False(budget.TryRecord(out _));
        Assert.False(budget.TryRecord(out _));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(budget.TryRecord(out var leftOut));
        Assert.Equal(2, leftOut);
        Assert.True(budget.TryRecord(out var after));
        Assert.Equal(0, after);
    }

    [DatabaseFact]
    public async Task JunkEnrollmentsAreAuditedWithinTheBudget()
    {
        var since = DateTimeOffset.UtcNow;
        using var http = factory.CreateDefaultClient(new Uri("https://localhost"));
        var requests = (EnrollmentRefusalBudget.PerMinute * 2) + 10;
        for (var i = 0; i < requests; i++)
        {
            using var refused = await http.PostAsJsonAsync(AgentProtocol.EnrollPath,
                new EnrollRequest(string.Empty, [1], "software", "0.1.0-test", TestAgent.Inventory, [2]), ProtocolJson.Options, Ct);
            Assert.False(refused.IsSuccessStatusCode);
        }

        // At most two minutes' worth: the test may cross into the next minute.
        await using var scope = factory.Services.CreateAsyncScope();
        var entries = (await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog
            .Where(e => e.Action == AuditActions.Node.EnrollmentFailed && e.OccurredAt >= since).Select(e => e.Metadata).ToListAsync(Ct))
            .Count(m => m!.Contains("\"malformed_token\"", StringComparison.Ordinal));
        Assert.InRange(entries, EnrollmentRefusalBudget.PerMinute, EnrollmentRefusalBudget.PerMinute * 2);
    }
}
