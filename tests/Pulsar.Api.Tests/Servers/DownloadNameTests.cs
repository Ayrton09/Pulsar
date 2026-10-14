using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Api.Tests.Notifications;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// The name a download carries is the one the user asked for, not the one the node answers with, and it shows nothing a
/// browser's dialog cannot show (docs/architecture/08, "File manager").
/// </summary>
public sealed class DownloadNameTests(NotificationApiFactory factory) : IClassFixture<NotificationApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("server.cfg", "server.cfg")]
    [InlineData("ñandú.cfg", "ñandú.cfg")]
    [InlineData("update\U0000202Efdp.exe", "update_fdp.exe")]
    [InlineData("a\U0000200Bb\U0000FEFFc", "a_b_c")]
    [InlineData("a\U00002028b\U00002029c", "a_b_c")]
    [InlineData("a\U000E0041b", "a_b")] // a tag character, also a format character
    [InlineData("a\nb\u0007c", "a_b_c")]
    [InlineData("\U0001F600.txt", "\U0001F600.txt")]
    public void ADownloadsNameShowsNothingHidden(string name, string expected)
    {
        Assert.Equal(expected, ServerFileEndpoints.DownloadFileName(name));
    }

    [DatabaseFact]
    public async Task ADownloadIsNamedByWhatWasAskedForNotByWhatTheNodeAnswers()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "download-name");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken, AgentCapabilities.ListFiles, AgentCapabilities.SendFile);
        agent.Answer = _ => new FileEntryResult(new FileEntry("update\U0000202Efdp.exe", FileEntryKind.File, 3, DateTimeOffset.UnixEpoch), "3-0");
        var server = await ServerOfAsync(agent);
        var transfers = factory.Services.GetRequiredService<FileTransfers>();
        var delivered = Task.Run(async () =>
        {
            SendFileCommand? send = null;
            for (var i = 0; i < 200 && send is null; i++)
            {
                await Task.Delay(50, Ct);
                lock (agent.ReceivedCommands)
                {
                    send = agent.ReceivedCommands.Select(c => c.Command).OfType<SendFileCommand>().FirstOrDefault();
                }
            }

            await transfers.ClaimDownload(send!.Ticket, new NodeId(agent.NodeId))!.DeliverAsync(new MemoryStream([1, 2, 3]), 3);
        }, Ct);

        using var response = await admin.GetAsync($"/api/v1/servers/{server}/files/download?path=server.cfg");
        await delivered;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("server.cfg", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal("\"3-0\"", response.Headers.ETag!.Tag);
    }

    private async Task<Guid> ServerOfAsync(TestAgent agent)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var templateId = (await db.GameTemplates.AsNoTracking().FirstAsync(t => t.Key == "css", Ct)).Id;
        var server = Server.Create(new NodeId(agent.NodeId), templateId, "Download name", null, Server.NewShortId(), DateTimeOffset.UtcNow).Value!;
        db.Servers.Add(server);
        await db.SaveChangesAsync(Ct);
        return server.Id.Value;
    }
}
