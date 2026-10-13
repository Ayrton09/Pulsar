using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Nodes;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Agents;

/// <summary>Allocations of a node added at the same moment (docs/architecture/02, "node_allocations").</summary>
public sealed class NodeAllocationRaceTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task EveryInterfaceAndOneAddressCannotBothTakeAPortAtOnce()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "allocation-race");

        // A request for every interface in progress, as the panel makes it: the node's row locked, its allocation saved
        // but not committed yet. The unique index cannot tell the two apart (another address).
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM nodes WHERE id = {node.Node.Id} FOR UPDATE", Ct);
        db.NodeAllocations.Add(NodeAllocation.Create(new NodeId(node.Node.Id), "0.0.0.0", 27015, null, DateTimeOffset.UtcNow).Value);
        await db.SaveChangesAsync(Ct);

        var racing = admin.PostAsync($"/api/v1/nodes/{node.Node.Id}/allocations", new CreateAllocationsRequest("10.0.0.5", "27015", null));
        await Task.Delay(TimeSpan.FromSeconds(1), Ct); // the request now waits for the other one, or is past its check
        await transaction.CommitAsync(Ct);

        using var response = await racing;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
