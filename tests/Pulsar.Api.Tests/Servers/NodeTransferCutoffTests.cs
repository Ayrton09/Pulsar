using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Servers;

public sealed class NodeTransferCutoffTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task ExplicitlyDisablingANodeAbortsItsClaimedAndWaitingTransfersOnly()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var made = await NodeApi.CreateAsync(admin, "transfer-cutoff");
        var node = new NodeId(made.Node.Id);
        var other = NodeId.New();
        var transfers = factory.Services.GetRequiredService<FileTransfers>();
        using var content = new MemoryStream([1, 2]);
        using var claimed = transfers.BeginUpload(node, null, content, 2)!;
        using var waiting = transfers.BeginDownload(node, null)!;
        using var unaffected = transfers.BeginDownload(other, null)!;
        Assert.Same(claimed, transfers.ClaimUpload(claimed.Ticket, node));

        using var disabled = await admin.PostAsync($"/api/v1/nodes/{node.Value}/disable");
        await AccessScenario.EnsureSuccessAsync(disabled);

        Assert.True(claimed.Aborted.IsCancellationRequested);
        Assert.True(waiting.Aborted.IsCancellationRequested);
        Assert.Null(transfers.ClaimDownload(waiting.Ticket, node));
        Assert.False(unaffected.Aborted.IsCancellationRequested);
    }
}
