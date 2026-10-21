using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// The free space the panel keeps on a node's drives (docs/architecture/08, "Disk space"): backups and uploads are
/// refused on a drive that would have less than 5 % or 2 GB free, as the agent's last metrics tell.
/// </summary>
public sealed class DiskFloorTests(DiskFloorApiFactory factory) : IClassFixture<DiskFloorApiFactory>
{
    private const long Gigabyte = 1L << 30;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task BackupsAndUploadsAreRefusedOnANearlyFullDrive()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "disk-floor");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        var server = (await ServerApi.CreateAsync(admin, agent.Config!.NodeId, "Disk floor")).Server.Id;
        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        var node = new NodeId(created.Node.Id);
        var upload = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);

        // The backups' drive at 4 % free; the servers' drive has room.
        factory.Disks[node] = [new DiskUsage(@"E:\", 100 * Gigabyte, 4 * Gigabyte, [DiskUses.Backups]),
            new DiskUsage(@"D:\", 100 * Gigabyte, 50 * Gigabyte, [DiskUses.Servers])];
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/backups", new CreateBackupRequest(null)), NodeErrors.DiskLow.Code);
        await EnsureAsync(await PutAsync(admin, server, "first.bin", upload));

        // One drive for both, with 2 GB and 1 MB free: the upload would leave less than 2 GB.
        factory.Disks[node] = [new DiskUsage(@"D:\", 20 * Gigabyte, 2 * Gigabyte + 1024 * 1024, [DiskUses.Servers, DiskUses.Backups])];
        await ProblemAsync(await PutAsync(admin, server, "second.bin", upload), NodeErrors.DiskLow.Code);

        // Under the floor already: what the editor saves still goes through (a config to fix), SFTP writes do not.
        factory.Disks[node] = [new DiskUsage(@"D:\", 20 * Gigabyte, Gigabyte, [DiskUses.Servers, DiskUses.Backups])];
        await EnsureAsync(await PutAsync(admin, server, "server.cfg", Encoding.UTF8.GetBytes("hostname \"Pulsar\"\n")));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            using var content = new MemoryStream(upload);
            var written = await scope.ServiceProvider.GetRequiredService<ServerFiles>().WriteStreamAsync(new ServerId(server), "third.bin", content,
                FileWriteMode.CreateNew, Ct);
            Assert.Equal(NodeErrors.DiskLow, written.Error);
        }

        // Room again.
        factory.Disks.TryRemove(node, out _);
        await EnsureAsync(await PutAsync(admin, server, "second.bin", upload));
        await EnsureAsync(await admin.PostAsync($"/api/v1/servers/{server}/backups", new CreateBackupRequest(null)));
        await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, "backup done");
    }

    private static async Task<HttpResponseMessage> PutAsync(PanelClient client, Guid server, string path, byte[] bytes)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await client.Http.PutAsync(new Uri($"/api/v1/servers/{server}/files/content?path={Uri.EscapeDataString(path)}", UriKind.Relative), content, Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.InsufficientStorage, $"{(int)response.StatusCode}: {body}");
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            }
        }
    }
}
