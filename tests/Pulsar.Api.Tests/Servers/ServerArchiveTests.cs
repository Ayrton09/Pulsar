using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// The file manager's zips (Pulsar 1.0.11): operations the node runs as the file manager does, while the game keeps
/// running, with the file manager's rights and paths, audited.
/// </summary>
public sealed class ServerArchiveTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static readonly string[] s_maps = ["maps"];
    private static readonly string[] s_cstrike = ["cstrike"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AFolderIsZippedAndUnzippedWhileTheGameRuns()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "archives");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27140-27145");
        var root = Path.Combine(agent.ServersRoot, (await ServerApi.GetAsync(admin, server))!.Server.ShortId);
        Directory.CreateDirectory(Path.Combine(root, "cstrike", "maps"));
        await File.WriteAllTextAsync(Path.Combine(root, "cstrike", "maps", "de_custom.bsp"), new string('m', 50_000), Ct);

        var zipping = await OperationAsync(admin.PostAsync($"/api/v1/servers/{server}/files/compress",
            new { directory = "cstrike", names = s_maps, archive = "cstrike/maps.zip" }));
        Assert.Equal(OperationType.FilesCompress, zipping.Type);
        Assert.Equal(OperationStatus.Succeeded, (await EndedAsync(admin, server, zipping.Id)).Status);
        using (var zip = ZipFile.OpenRead(Path.Combine(root, "cstrike", "maps.zip")))
        {
            Assert.Equal(["maps/", "maps/de_custom.bsp"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        var unzipping = await OperationAsync(admin.PostAsync($"/api/v1/servers/{server}/files/extract",
            new { archive = "cstrike/maps.zip", destination = "cstrike/copy", overwrite = false }));
        Assert.Equal(OperationStatus.Succeeded, (await EndedAsync(admin, server, unzipping.Id)).Status);
        Assert.Equal(new string('m', 50_000), await File.ReadAllTextAsync(Path.Combine(root, "cstrike", "copy", "maps", "de_custom.bsp"), Ct));

        // A second time without overwrite: the node refuses it, and the operation says why.
        var again = await OperationAsync(admin.PostAsync($"/api/v1/servers/{server}/files/extract",
            new { archive = "cstrike/maps.zip", destination = "cstrike/copy", overwrite = false }));
        var refused = await EndedAsync(admin, server, again.Id);
        Assert.Equal((OperationStatus.Failed, FileErrors.Exists), (refused.Status, refused.ErrorCode));

        Assert.Equal(ServerRuntimeState.Running, (await ServerApi.GetAsync(admin, server))!.Server.RuntimeState);
        var audit = await AuditAsync(server);
        Assert.Single(audit, a => a.Action == AuditActions.Server.FilesCompressRequested);
        Assert.Equal(2, audit.Count(a => a.Action == AuditActions.Server.FilesExtractRequested));
    }

    [DatabaseFact]
    public async Task PathsTheFileManagerRefusesAreRefusedBeforeTheNodeIsAsked()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "archive-paths");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27150-27155");

        foreach (var body in new object[]
        {
            new { directory = "", names = new[] { "cstrike" }, archive = "../out.zip" },
            new { directory = "", names = new[] { ".pulsar" }, archive = "out.zip" },
            new { directory = "", names = new[] { "cstrike" }, archive = ".pulsar/out.zip" },
            new { directory = "", names = new[] { "PULSAR~1" }, archive = "out.zip" },
            new { directory = "", names = new[] { "a/b" }, archive = "out.zip" },
            new { directory = "", names = new[] { "a", "A" }, archive = "out.zip" },
            new { directory = "", names = Enumerable.Range(0, CompressFilesCommand.MaxNames + 1).Select(i => $"f{i}").ToArray(), archive = "out.zip" },
            new { directory = "", names = Array.Empty<string>(), archive = "out.zip" },
        })
        {
            using var response = await admin.PostAsync($"/api/v1/servers/{server}/files/compress", body);
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, $"{JsonSerializer.Serialize(body)}: {(int)response.StatusCode}");
        }

        foreach (var body in new object[]
        {
            new { archive = "x.zip", destination = ".pulsar/steamcmd", overwrite = true },
            new { archive = "x.zip", destination = "a/../..", overwrite = true },
            new { archive = "", destination = "", overwrite = true },
        })
        {
            using var response = await admin.PostAsync($"/api/v1/servers/{server}/files/extract", body);
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, $"{JsonSerializer.Serialize(body)}: {(int)response.StatusCode}");
        }

        Assert.Null((await ServerApi.GetAsync(admin, server))!.Server.ActiveOperation);
        Assert.DoesNotContain(await AuditAsync(server), a => a.Action is AuditActions.Server.FilesCompressRequested or AuditActions.Server.FilesExtractRequested);
    }

    [DatabaseFact]
    public async Task ZipsNeedTheRightToChangeFilesAndAnAgentThatMakesThem()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "archive-rights");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27160-27165");

        using var viewer = await scenario.SignedInWithAsync("archive-viewer", Permissions.Server.View, Permissions.Server.FilesView);
        using (var refused = await viewer.PostAsync($"/api/v1/servers/{server}/files/compress", new { directory = "", names = s_cstrike, archive = "c.zip" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // An agent of 1.0.10 does not announce the zips: the panel never sends one it would not take.
        var node = await NodeApi.CreateAsync(admin, "archive-old");
        await using var old = new TestAgent(factory);
        await old.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await old.ConnectAsync((await old.AuthenticateAsync()).AccessToken, AgentCapabilities.ListFiles);
        var oldServer = await ServerOfAsync(old.NodeId);
        using (var unsupported = await admin.PostAsync($"/api/v1/servers/{oldServer}/files/extract", new { archive = "x.zip", destination = "", overwrite = false }))
        {
            Assert.Equal(HttpStatusCode.Conflict, unsupported.StatusCode);
            using var problem = JsonDocument.Parse(await unsupported.Content.ReadAsStringAsync(Ct));
            Assert.Equal(ServerErrors.ArchivesUnsupported.Code, problem.RootElement.GetProperty("code").GetString());
        }

        Assert.DoesNotContain(old.ReceivedCommands, c => c.Command is CompressFilesCommand or ExtractArchiveCommand);
    }

    private static async Task<OperationView> OperationAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<OperationView>(AccessScenario.Json, Ct))!;
    }

    private static async Task<OperationView> EndedAsync(PanelClient admin, Guid server, Guid operation)
    {
        var details = await ServerApi.WaitForAsync(admin, server,
            s => s.RecentOperations.Any(o => o.Id == operation && o.Status is not (OperationStatus.Queued or OperationStatus.Running)), "operation ended");
        return details.RecentOperations.Single(o => o.Id == operation);
    }

    private async Task<Guid> ServerOfAsync(Guid nodeId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var templateId = (await db.GameTemplates.AsNoTracking().FirstAsync(t => t.Key == "css", Ct)).Id;
        var server = Server.Create(new NodeId(nodeId), templateId, "Old agent", null, Server.NewShortId(), DateTimeOffset.UtcNow).Value!;
        db.Servers.Add(server);
        await db.SaveChangesAsync(Ct);
        return server.Id.Value;
    }

    private async Task<List<AuditEntry>> AuditAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.ServerId == new ServerId(server)).ToListAsync(Ct);
    }
}
