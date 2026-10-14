using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
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
/// Phase 6 through the panel and the real agent core: browsing, editing, uploading and downloading a server's files,
/// with the content piped between the browser's request and the agent's (never through the hub, never stored).
/// </summary>
public sealed class ServerFileTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private readonly Dictionary<Guid, string> _shortIds = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task TheEditorOpensAndSavesAFileWithItsVersion()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "editor");
        var server = await InstalledServerAsync(admin, agent, "Editor");

        await EnsureAsync(await admin.PostAsync($"/api/v1/servers/{server}/files/directories", new FilePathRequest("cfg")));
        var created = await WriteAsync(admin, server, "cfg/server.cfg", "hostname \"Pulsar\"\n", ifNoneMatch: true);
        var (text, etag) = await ReadAsync(admin, server, "cfg/server.cfg");
        Assert.Equal("hostname \"Pulsar\"\n", text);
        Assert.Equal($"\"{created.Version}\"", etag);

        var saved = await WriteAsync(admin, server, "cfg/server.cfg", "hostname \"Edited\"\n", ifMatch: created.Version);
        Assert.NotEqual(created.Version, saved.Version);
        Assert.Equal("hostname \"Edited\"\n", (await ReadAsync(admin, server, "cfg/server.cfg")).Text);
        Assert.Equal("hostname \"Edited\"\n", await File.ReadAllTextAsync(Path.Combine(ServerDirectory(agent, server), "cfg", "server.cfg"), Ct));

        var listing = await ListAsync(admin, server, "cfg");
        var entry = Assert.Single(listing.Entries);
        Assert.Equal(new FileEntry("server.cfg", FileEntryKind.File, 18, entry.ModifiedAt), entry);
        Assert.Contains((await ListAsync(admin, server, null)).Entries, e => e.Name == "cfg" && e.Kind == FileEntryKind.Directory);

        var audited = await AuditAsync(server);
        Assert.Equal(2, audited.Count(a => a.Action == AuditActions.Server.FileWritten && a.Outcome == AuditOutcome.Success));
        Assert.Single(audited, a => a.Action == AuditActions.Server.DirectoryCreated);
    }

    [DatabaseFact]
    public async Task ASaveNeverOverwritesAChangeTheEditorDidNotSee()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "conflict");
        var server = await InstalledServerAsync(admin, agent, "Conflict");
        var opened = await WriteAsync(admin, server, "server.cfg", "first", ifNoneMatch: true);
        await WriteAsync(admin, server, "server.cfg", "someone else's change");

        await ProblemAsync(await PutAsync(admin, server, "server.cfg", "mine", ifMatch: opened.Version), HttpStatusCode.PreconditionFailed, FileErrors.Changed);
        await ProblemAsync(await PutAsync(admin, server, "server.cfg", "new", ifNoneMatch: true), HttpStatusCode.Conflict, FileErrors.Exists);
        Assert.Equal("someone else's change", (await ReadAsync(admin, server, "server.cfg")).Text);
        Assert.Single(await AuditAsync(server), a => a.Action == AuditActions.Server.FileWritten && a.Outcome == AuditOutcome.Failure
            && a.Metadata.Contains(FileErrors.Changed, StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task LargeFilesStreamBothWaysButTheEditorOpensSmallOnes()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "large");
        var server = await InstalledServerAsync(admin, agent, "Large");
        var content = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 7);

        using (var upload = new ByteArrayContent(content))
        {
            upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await admin.Http.PutAsync(new Uri($"/api/v1/servers/{server}/files/content?path=maps%2Fde_test.bsp", UriKind.Relative), upload, Ct);
            await ProblemAsync(response, HttpStatusCode.NotFound, FileErrors.NotFound); // its folder does not exist
        }

        await EnsureAsync(await admin.PostAsync($"/api/v1/servers/{server}/files/directories", new FilePathRequest("maps")));
        using (var upload = new ByteArrayContent(content))
        {
            using var response = await admin.Http.PutAsync(new Uri($"/api/v1/servers/{server}/files/content?path=maps%2Fde_test.bsp", UriKind.Relative), upload, Ct);
            await EnsureAsync(response);
        }

        using (var download = await admin.GetAsync($"/api/v1/servers/{server}/files/download?path=maps%2Fde_test.bsp"))
        {
            await EnsureAsync(download);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("de_test.bsp", download.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Equal(content.Length, download.Content.Headers.ContentLength);
            Assert.Equal(SHA256.HashData(content), SHA256.HashData(await download.Content.ReadAsByteArrayAsync(Ct)));
        }

        await ProblemAsync(await admin.GetAsync($"/api/v1/servers/{server}/files/content?path=maps%2Fde_test.bsp"), HttpStatusCode.RequestEntityTooLarge,
            FileErrors.TooLarge);
        Assert.Single(await AuditAsync(server), a => a.Action == AuditActions.Server.FileDownloaded);
        Assert.DoesNotContain(Directory.EnumerateFiles(ServerDirectory(agent, server), "*", SearchOption.AllDirectories),
            f => Path.GetFileName(f).StartsWith(".pulsar-upload-", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task FilesAreRenamedMovedAndDeleted()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "move");
        var server = await InstalledServerAsync(admin, agent, "Move");
        await EnsureAsync(await admin.PostAsync($"/api/v1/servers/{server}/files/directories", new FilePathRequest("addons/plugins")));
        await WriteAsync(admin, server, "addons/plugins/a.smx", "a");

        await EnsureAsync(await admin.PostAsync($"/api/v1/servers/{server}/files/move", new MoveFileRequest("addons/plugins/a.smx", "addons/b.smx")));
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/files/move", new MoveFileRequest("addons", "addons/inside")),
            HttpStatusCode.BadRequest, FileErrors.InvalidPath);
        Assert.Equal(["plugins", "b.smx"], (await ListAsync(admin, server, "addons")).Entries.Select(e => e.Name));

        using (var deleted = await admin.Http.DeleteAsync(new Uri($"/api/v1/servers/{server}/files?path=addons", UriKind.Relative), Ct))
        {
            await EnsureAsync(deleted);
        }

        Assert.False(Directory.Exists(Path.Combine(ServerDirectory(agent, server), "addons")));
        await ProblemAsync(await admin.GetAsync($"/api/v1/servers/{server}/files?path=addons"), HttpStatusCode.NotFound, FileErrors.NotFound);
        var audited = await AuditAsync(server);
        Assert.Single(audited, a => a.Action == AuditActions.Server.FileMoved && a.Outcome == AuditOutcome.Success);
        Assert.Single(audited, a => a.Action == AuditActions.Server.FileDeleted && a.Metadata.Contains("addons", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task PathsAreCheckedBeforeAnythingReachesTheNode()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "paths");
        var server = await InstalledServerAsync(admin, agent, "Paths");

        foreach (var path in new[] { "..", "../x", "C:/Windows", "a\\b", "con.txt", "server.cfg:stream" })
        {
            await ProblemAsync(await admin.GetAsync($"/api/v1/servers/{server}/files?path={Uri.EscapeDataString(path)}"), HttpStatusCode.BadRequest,
                FileErrors.InvalidPath);
        }

        using (var root = await admin.Http.DeleteAsync(new Uri($"/api/v1/servers/{server}/files?path=", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, root.StatusCode);
        }

        using var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("x")));
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/servers/{server}/files/content?path=x.cfg", UriKind.Relative))
        {
            Content = chunked,
        };
        request.Headers.TransferEncodingChunked = true;
        await ProblemAsync(await admin.Http.SendAsync(request, Ct), HttpStatusCode.LengthRequired, "files.length_required");
    }

    [DatabaseFact]
    public async Task ReadingAndWritingNeedTheirOwnPermissions()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "permissions");
        var server = await InstalledServerAsync(scenario.Admin, agent, "Permissions");
        await WriteAsync(scenario.Admin, server, "server.cfg", "x");
        using var viewer = await scenario.SignedInWithAsync("file-viewer", Permissions.Server.View, Permissions.Server.FilesView);
        using var outsider = await scenario.SignedInWithAsync("file-outsider", Permissions.Server.View);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/v1/servers/{server}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/v1/servers/{server}/files/download?path=server.cfg")).StatusCode);
        Assert.NotEmpty((await ListAsync(viewer, server, null)).Entries);
        Assert.Equal("x", (await ReadAsync(viewer, server, "server.cfg")).Text);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(viewer, server, "server.cfg", "y")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/servers/{server}/files/directories", new FilePathRequest("x"))).StatusCode);
    }

    [DatabaseFact]
    public async Task ANodesTransfersAreLimitedBeforeTheNodeIsAsked()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "busy");
        var server = await InstalledServerAsync(admin, agent, "Busy");
        await WriteAsync(admin, server, "server.cfg", "x");
        var node = new NodeId(agent.Config!.NodeId);
        var transfers = factory.Services.GetRequiredService<FileTransfers>();
        using var session = await admin.GetAsync("/api/v1/auth/session");
        var adminId = new UserId((await session.Content.ReadFromJsonAsync<Pulsar.Api.Auth.SessionResponse>(AccessScenario.Json, Ct))!.User!.Id);

        // Three transfers of the user on the node (slow ones, say): a fourth is refused before the node hears of it.
        var held = Enumerable.Range(0, 3).Select(_ => transfers.BeginDownload(node, adminId)!).ToList();
        await ProblemAsync(await PutAsync(admin, server, "new.cfg", "y"), HttpStatusCode.Conflict, "files.transfers_busy");
        await ProblemAsync(await admin.GetAsync($"/api/v1/servers/{server}/files/content?path=server.cfg"), HttpStatusCode.Conflict, "files.transfers_busy");
        Assert.False(File.Exists(Path.Combine(ServerDirectory(agent, server), "new.cfg")));
        held.ForEach(t => t.Dispose());

        // Everyone's transfers together fill the node's share, which leaves the rest of its connections to its hub.
        held = Enumerable.Range(0, FileTransferLimits.Default.PerNode).Select(_ => transfers.BeginDownload(node, null)!).ToList();
        await ProblemAsync(await PutAsync(admin, server, "new.cfg", "y"), HttpStatusCode.Conflict, "files.transfers_busy");
        held[0].Dispose();
        await WriteAsync(admin, server, "new.cfg", "y");
        Assert.Equal("y", (await ReadAsync(admin, server, "new.cfg")).Text);
        held.ForEach(t => t.Dispose());
    }

    private static async Task<FileListing> ListAsync(PanelClient client, Guid server, string? path)
    {
        using var response = await client.GetAsync($"/api/v1/servers/{server}/files" + (path is null ? "" : $"?path={Uri.EscapeDataString(path)}"));
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<FileListing>(AccessScenario.Json, Ct))!;
    }

    private static async Task<(string Text, string? ETag)> ReadAsync(PanelClient client, Guid server, string path)
    {
        using var response = await client.GetAsync($"/api/v1/servers/{server}/files/content?path={Uri.EscapeDataString(path)}");
        await EnsureAsync(response);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadAsStringAsync(Ct), response.Headers.ETag?.ToString());
    }

    private static async Task<FileDetails> WriteAsync(PanelClient client, Guid server, string path, string text, string? ifMatch = null, bool ifNoneMatch = false)
    {
        using var response = await PutAsync(client, server, path, text, ifMatch, ifNoneMatch);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<FileDetails>(AccessScenario.Json, Ct))!;
    }

    private static async Task<HttpResponseMessage> PutAsync(PanelClient client, Guid server, string path, string text, string? ifMatch = null, bool ifNoneMatch = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/servers/{server}/files/content?path={Uri.EscapeDataString(path)}", UriKind.Relative))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)),
        };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{ifMatch}\""));
        }

        if (ifNoneMatch)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }

        return await client.Http.SendAsync(request, Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        }
    }

    private async Task<List<Domain.Audit.AuditEntry>> AuditAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.ServerId == new ServerId(server)).ToListAsync(Ct);
    }

    private string ServerDirectory(InProcessAgent agent, Guid server) => Path.Combine(agent.ServersRoot, _shortIds[server]);

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"files-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    private async Task<Guid> InstalledServerAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        _shortIds[created.Server.Id] = created.Server.ShortId;
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }
}
