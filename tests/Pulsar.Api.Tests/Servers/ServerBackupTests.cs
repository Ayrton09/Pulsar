using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Notifications;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Phase 7 through the panel and the real agent core: backups made on the node, downloaded through a transfer,
/// restored, locked, deleted and pruned by the retention limit.
/// </summary>
public sealed class ServerBackupTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private readonly Dictionary<Guid, string> _shortIds = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ABackupIsMadeDownloadedAndRestored()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "restore");
        var server = await InstalledServerAsync(admin, agent, "Restore me");
        var config = Path.Combine(ServerDirectory(agent, server), "data", "server.cfg");
        await File.WriteAllTextAsync(config, "hostname one", Ct);

        var created = await CreateAsync(admin, server, "Before the change");
        Assert.Equal("Before the change", created.Name);
        var backup = await WaitForBackupAsync(admin, server, created.Id, BackupStatus.Completed);
        Assert.Equal(2, backup.FileCount); // the test game's executable and the configuration
        Assert.Equal(0, backup.SkippedFiles);
        Assert.Equal((BackupCopyMode.Stopped, null, null), (backup.CopyMode, backup.SnapshotError, backup.GameSave)); // nothing was writing
        Assert.Matches("^[0-9a-f]{64}$", backup.Sha256);
        Assert.NotNull(backup.DurationSeconds);
        var archive = Path.Combine(agent.Configs.Paths.BackupsDirectory, _shortIds[server], $"{backup.Id:N}.zip");
        Assert.Equal(backup.Size, new FileInfo(archive).Length);

        byte[] downloaded;
        using (var download = await admin.GetAsync($"/api/v1/servers/{server}/backups/{backup.Id}/download"))
        {
            await EnsureAsync(download);
            Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
            Assert.Matches(@"^Restore me-\d{8}-\d{6}\.zip$", download.Content.Headers.ContentDisposition?.FileNameStar);
            downloaded = await download.Content.ReadAsByteArrayAsync(Ct);
        }

        Assert.Equal(backup.Sha256, Convert.ToHexStringLower(SHA256.HashData(downloaded)));
        using (var zip = new ZipArchive(new MemoryStream(downloaded)))
        {
            Assert.Equal(["bin/Pulsar.TestGameServer.exe", "data/server.cfg"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        await File.WriteAllTextAsync(config, "hostname two", Ct);
        await EnsureAsync(await admin.PostAsync($"/api/v1/servers/{server}/backups/{backup.Id}/restore"));
        var details = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, "restored");
        var restore = details.RecentOperations.First(o => o.Type == OperationType.BackupRestore);
        Assert.Equal(OperationStatus.Succeeded, restore.Status);
        Assert.Equal("hostname one", await File.ReadAllTextAsync(config, Ct));

        var audited = await AuditAsync(server);
        Assert.Single(audited, a => a.Action == AuditActions.Server.BackupRequested);
        Assert.Single(audited, a => a.Action == AuditActions.Server.BackupDownloaded);
        Assert.Single(audited, a => a.Action == AuditActions.Server.BackupRestoreRequested);
    }

    [DatabaseFact]
    public async Task ABackupRunsWhileTheServerDoesButARestoreNeedsItStopped()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "running");
        var server = await InstalledServerAsync(admin, agent, "Running");
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");

        var created = await CreateAsync(admin, server, null);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC$", created.Name);
        var backup = await WaitForBackupAsync(admin, server, created.Id, BackupStatus.Completed);

        // The game saved first (pulsar-test's backup.save); this agent takes no snapshots, so its files were read live.
        Assert.Equal((BackupCopyMode.Live, "snapshot.unsupported", GameSaveResult.Confirmed), (backup.CopyMode, backup.SnapshotError, backup.GameSave));
        Assert.Single(agent.Processes.Started.Last(p => !p.IsSteamCmd).Saves);
        Assert.Equal(ServerRuntimeState.Running, (await ServerApi.GetAsync(admin, server))!.Server.RuntimeState);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/backups/{backup.Id}/restore"), HttpStatusCode.Conflict,
            ServerErrors.MustBeStopped.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/operations/{backup.OperationId}/cancel"), HttpStatusCode.Conflict,
            ServerErrors.OperationNotCancellable.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/backups/{Guid.NewGuid()}/restore"), HttpStatusCode.NotFound,
            ServerErrors.BackupNotFound.Code);
    }

    [DatabaseFact]
    public async Task ACleanRestoreDeletesTheFilesMadeAfterTheBackup()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "clean");
        var server = await InstalledServerAsync(admin, agent, "Clean");
        var data = Path.Combine(ServerDirectory(agent, server), "data");
        await File.WriteAllTextAsync(Path.Combine(data, "server.cfg"), "hostname one", Ct);
        var backup = await MadeAsync(admin, server, "Before exploring");
        await File.WriteAllTextAsync(Path.Combine(data, "server.cfg"), "hostname two", Ct);
        await File.WriteAllTextAsync(Path.Combine(data, "region.dat"), "explored after the backup", Ct);

        await EnsureAsync(await admin.PostAsync($"/api/v1/servers/{server}/backups/{backup.Id}/restore?clean=true"));

        var details = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, "restored");
        Assert.Equal(OperationStatus.Succeeded, details.RecentOperations.First(o => o.Type == OperationType.BackupRestore).Status);
        Assert.Equal("hostname one", await File.ReadAllTextAsync(Path.Combine(data, "server.cfg"), Ct));
        Assert.False(File.Exists(Path.Combine(data, "region.dat")));
        var audited = (await AuditAsync(server)).Single(a => a.Action == AuditActions.Server.BackupRestoreRequested).Metadata;
        Assert.True(JsonDocument.Parse(audited).RootElement.GetProperty("clean").GetBoolean(), audited);
    }

    [DatabaseFact]
    public async Task ABackupThatLeftFilesOutIsNotRestoredClean()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "partial");
        var server = await InstalledServerAsync(admin, agent, "Partial");
        Guid partial;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // Read live while the game held two files open (a node that could not take a snapshot).
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var row = await db.Servers.SingleAsync(s => s.Id == new ServerId(server), Ct);
            var operation = Operation.Create(OperationType.BackupCreate, row.NodeId, row.Id, null, OperationRequester.User, DateTimeOffset.UtcNow);
            operation.Complete(OperationStatus.Succeeded, null, null, DateTimeOffset.UtcNow);
            var backup = Backup.Create(row, "partial", operation, null, OperationRequester.User, DateTimeOffset.UtcNow).Value;
            backup.Completed(10, 3, 2, new byte[32], DateTimeOffset.UtcNow, BackupCopyMode.Live, "snapshot.not_permitted");
            db.Operations.Add(operation);
            db.Backups.Add(backup);
            await db.SaveChangesAsync(Ct);
            partial = backup.Id.Value;
        }

        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/backups/{partial}/restore?clean=true"),
            HttpStatusCode.Conflict, ServerErrors.CleanRestoreIncomplete.Code);
        var listed = (await ListAsync(admin, server)).Backups.Single(b => b.Id == partial);
        Assert.Equal((BackupCopyMode.Live, "snapshot.not_permitted", 2), (listed.CopyMode, listed.SnapshotError, listed.SkippedFiles));
    }

    [DatabaseFact]
    public async Task RetentionKeepsTheNewestUnlockedBackups()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "retention");
        var server = await InstalledServerAsync(admin, agent, "Retention");
        await SettingsAsync(admin, server, 2);

        var first = await MadeAsync(admin, server, "first");
        await EnsureAsync(await admin.PutAsync($"/api/v1/servers/{server}/backups/{first.Id}/lock", new LockBackupRequest(true)));
        var second = await MadeAsync(admin, server, "second");
        var third = await MadeAsync(admin, server, "third");
        var fourth = await MadeAsync(admin, server, "fourth");

        // The locked one stays and does not count; of the others, the two newest stay.
        await InProcessAgent.EventuallyAsync(async () => (await ListAsync(admin, server)).Backups.Count == 3, "the oldest unlocked backup pruned");
        var list = await ListAsync(admin, server);
        Assert.Equal(2, list.Limit);
        Assert.Equal([fourth.Id, third.Id, first.Id], list.Backups.Select(b => b.Id));
        Assert.False(File.Exists(Archive(agent, server, second.Id)));
        Assert.True(File.Exists(Archive(agent, server, first.Id)));

        await ProblemAsync(await admin.DeleteAsync($"/api/v1/servers/{server}/backups/{first.Id}"), HttpStatusCode.Conflict, ServerErrors.BackupLocked.Code);
        await EnsureAsync(await admin.PutAsync($"/api/v1/servers/{server}/backups/{first.Id}/lock", new LockBackupRequest(false)));
        await EnsureAsync(await admin.DeleteAsync($"/api/v1/servers/{server}/backups/{first.Id}"));
        Assert.False(File.Exists(Archive(agent, server, first.Id)));

        // A lower limit applies at once.
        list = await SettingsAsync(admin, server, 1);
        Assert.Equal([fourth.Id], list.Backups.Select(b => b.Id));
        Assert.False(File.Exists(Archive(agent, server, third.Id)));
        using (var invalid = await admin.PutAsync($"/api/v1/servers/{server}/backups/settings", new BackupSettingsRequest(0)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        var pruned = (await AuditAsync(server)).Where(a => a.Action == AuditActions.Server.BackupDeleted).ToList();
        Assert.Equal(3, pruned.Count);
        Assert.Equal(2, pruned.Count(a => a.ActorType == AuditActorType.System && a.Metadata.Contains("retention", StringComparison.Ordinal)));
    }

    [DatabaseFact]
    public async Task ABackupTheNodeLostFails()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "lost");
        var server = await InstalledServerAsync(admin, agent, "Lost");
        Guid lost;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // As if the agent had accepted it and then restarted halfway.
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var row = await db.Servers.SingleAsync(s => s.Id == new ServerId(server), Ct);
            var operation = Operation.Create(OperationType.BackupCreate, row.NodeId, row.Id, null, OperationRequester.Schedule, DateTimeOffset.UtcNow);
            Assert.True(row.BeginOperation(operation, DateTimeOffset.UtcNow).IsSuccess);
            operation.Started(DateTimeOffset.UtcNow);
            var backup = Backup.Create(row, "lost", operation, null, OperationRequester.Schedule, DateTimeOffset.UtcNow).Value;
            db.Operations.Add(operation);
            db.Backups.Add(backup);
            await db.SaveChangesAsync(Ct);
            lost = backup.Id.Value;
        }

        await agent.StopAsync();
        await agent.StartAsync();

        var failed = await WaitForBackupAsync(admin, server, lost, BackupStatus.Failed);
        Assert.Contains("restarted", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Null((await ServerApi.GetAsync(admin, server))!.Server.ActiveOperation);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/backups/{lost}/restore"), HttpStatusCode.Conflict,
            ServerErrors.BackupNotCompleted.Code);
        await EnsureAsync(await admin.DeleteAsync($"/api/v1/servers/{server}/backups/{lost}"));
    }

    [DatabaseFact]
    public async Task DeletingAServerDeletesItsBackupsFromTheNode()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "gone");
        var server = await InstalledServerAsync(admin, agent, "Gone");
        var backup = await MadeAsync(admin, server, "kept until the server goes");
        Assert.True(File.Exists(Archive(agent, server, backup.Id)));

        await EnsureAsync(await admin.DeleteAsync($"/api/v1/servers/{server}"));
        await InProcessAgent.EventuallyAsync(async () => await ServerApi.GetAsync(admin, server) is null, "server deleted");

        Assert.False(Directory.Exists(Path.Combine(agent.Configs.Paths.BackupsDirectory, _shortIds[server])));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Backups.AnyAsync(b => b.ServerId == new ServerId(server), Ct));
    }

    [DatabaseFact]
    public async Task ACopyGoesToRemoteStorageAndComesBackWhenTheNodeLostIt()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "remote");
        var server = await InstalledServerAsync(admin, agent, "Remote copy");
        var config = Path.Combine(ServerDirectory(agent, server), "data", "server.cfg");
        await File.WriteAllTextAsync(config, "hostname remote", Ct);

        var storage = await CreateStorageAsync(admin, "Offsite");
        Assert.Equal("Offsite", storage.Name);
        using (var listed = await admin.GetAsync("/api/v1/backup-storages"))
        {
            Assert.DoesNotContain("storage secret key", await listed.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        var chosen = await ChooseStorageAsync(admin, server, storage.Id);
        Assert.Equal(storage.Id, chosen.StorageId);
        Assert.Contains(chosen.Storages, s => s.Id == storage.Id && s.Name == "Offsite");

        var backup = await MadeAsync(admin, server, "Offsite copy");
        backup = await WaitForAsync(admin, server, backup.Id, b => b.RemoteStatus == RemoteCopyStatus.Copied, "copied");
        var key = $"pulsar-backups/servers/{_shortIds[server]}/{backup.Id:N}.zip";
        Assert.True(agent.RemoteBackups.Objects.ContainsKey(key), string.Join(", ", agent.RemoteBackups.Objects.Keys));
        Assert.Contains("storage secret key", agent.RemoteBackups.SecretsSeen);

        // The node loses its archive: a restore brings it back from remote storage first.
        File.Delete(Archive(agent, server, backup.Id));
        await File.WriteAllTextAsync(config, "hostname changed", Ct);
        await EnsureAsync(await admin.PostAsync($"/api/v1/servers/{server}/backups/{backup.Id}/restore"));
        var details = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, "restored");
        Assert.Equal(OperationStatus.Succeeded, details.RecentOperations.First(o => o.Type == OperationType.BackupRestore).Status);
        Assert.Equal("hostname remote", await File.ReadAllTextAsync(config, Ct));
        Assert.True(File.Exists(Archive(agent, server, backup.Id)));

        // Lost again: a download comes straight from remote storage.
        File.Delete(Archive(agent, server, backup.Id));
        using (var download = await admin.GetAsync($"/api/v1/servers/{server}/backups/{backup.Id}/download"))
        {
            await EnsureAsync(download);
            Assert.Equal(backup.Sha256, Convert.ToHexStringLower(SHA256.HashData(await download.Content.ReadAsByteArrayAsync(Ct))));
        }

        // A storage holding copies cannot go; deleting the backup deletes its copy too.
        await ProblemAsync(await admin.DeleteAsync($"/api/v1/backup-storages/{storage.Id}"), HttpStatusCode.Conflict, "backup_storage.in_use");
        await EnsureAsync(await admin.DeleteAsync($"/api/v1/servers/{server}/backups/{backup.Id}"));
        Assert.False(agent.RemoteBackups.Objects.ContainsKey(key));
        await ChooseStorageAsync(admin, server, null);
        await EnsureAsync(await admin.DeleteAsync($"/api/v1/backup-storages/{storage.Id}"));

        var audited = await AuditAsync(server);
        Assert.Equal(2, audited.Count(a => a.Action == AuditActions.Server.BackupStorageChanged));
    }

    [DatabaseFact]
    public async Task AFailedCopyKeepsTheBackupOnTheNodeAndIsNotified()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "remote-failure");
        var server = await InstalledServerAsync(admin, agent, "Copy fails");
        var storage = await CreateStorageAsync(admin, "Broken storage");
        await ChooseStorageAsync(admin, server, storage.Id);
        await EnsureAsync(await admin.PostAsync("/api/v1/notifications/channels", new NotificationChannelRequest("Backups", NotificationKind.Email, true,
            [NotificationEvent.BackupFailed], [server], "en", null, ["ops@example.com"])));
        agent.RemoteBackups.FailUploads = "Remote storage refused the credentials or the bucket (AccessDenied).";

        var made = await MadeAsync(admin, server, "Local only");
        var backup = await WaitForAsync(admin, server, made.Id, b => b.RemoteStatus == RemoteCopyStatus.Failed, "copy failed");
        Assert.Equal(BackupStatus.Completed, backup.Status);
        Assert.Equal("Remote storage refused the credentials or the bucket (AccessDenied).", backup.RemoteError);
        Assert.True(File.Exists(Archive(agent, server, backup.Id)));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var delivery = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Subject == $"server:{server}:remote", Ct);
        Assert.Equal("Remote copy of a backup of Copy fails failed", delivery.Message.Title);
        // Its copy failed, so the storage keeps nothing of it: the storage can go once no server uses it.
        await ChooseStorageAsync(admin, server, null);
        await EnsureAsync(await admin.DeleteAsync($"/api/v1/backup-storages/{storage.Id}"));
    }

    private static async Task<BackupStorageView> CreateStorageAsync(PanelClient admin, string name)
    {
        using var response = await admin.PostAsync("/api/v1/backup-storages",
            new BackupStorageRequest(name, "https://s3.example.com", "auto", "pulsar-backups", "servers", "AKIDEXAMPLE", "storage secret key", true));
        await EnsureAsync(response);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("storage secret key", body, StringComparison.Ordinal);
        return JsonSerializer.Deserialize<BackupStorageView>(body, AccessScenario.Json)!;
    }

    private static async Task<BackupList> ChooseStorageAsync(PanelClient client, Guid server, Guid? storage)
    {
        using var response = await client.PutAsync($"/api/v1/servers/{server}/backups/storage", new BackupStorageChoiceRequest(storage));
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<BackupList>(AccessScenario.Json, Ct))!;
    }

    private static async Task<BackupView> WaitForAsync(PanelClient client, Guid server, Guid backup, Func<BackupView, bool> condition, string what)
    {
        BackupView? last = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            last = (await ListAsync(client, server)).Backups.SingleOrDefault(b => b.Id == backup);
            return last is not null && condition(last);
        }, () => $"backup {what} (last: {last?.Status} {last?.RemoteStatus} {last?.RemoteError})");
        return last!;
    }

    private static async Task<BackupView> MadeAsync(PanelClient client, Guid server, string name) =>
        await WaitForBackupAsync(client, server, (await CreateAsync(client, server, name)).Id, BackupStatus.Completed);

    private static async Task<BackupView> CreateAsync(PanelClient client, Guid server, string? name)
    {
        using var response = await client.PostAsync($"/api/v1/servers/{server}/backups", new CreateBackupRequest(name));
        await EnsureAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BackupView>(AccessScenario.Json, Ct))!;
    }

    private static async Task<BackupList> ListAsync(PanelClient client, Guid server)
    {
        using var response = await client.GetAsync($"/api/v1/servers/{server}/backups");
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<BackupList>(AccessScenario.Json, Ct))!;
    }

    private static async Task<BackupList> SettingsAsync(PanelClient client, Guid server, int? limit)
    {
        using var response = await client.PutAsync($"/api/v1/servers/{server}/backups/settings", new BackupSettingsRequest(limit));
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<BackupList>(AccessScenario.Json, Ct))!;
    }

    private static async Task<BackupView> WaitForBackupAsync(PanelClient client, Guid server, Guid backup, BackupStatus status)
    {
        BackupView? last = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            last = (await ListAsync(client, server)).Backups.SingleOrDefault(b => b.Id == backup);
            return last?.Status == status;
        }, () => $"backup {status} (last: {last?.Status} {last?.ErrorMessage})");
        return last!;
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

    private async Task<List<AuditEntry>> AuditAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.ServerId == new ServerId(server)).ToListAsync(Ct);
    }

    private string ServerDirectory(InProcessAgent agent, Guid server) => Path.Combine(agent.ServersRoot, _shortIds[server]);

    private string Archive(InProcessAgent agent, Guid server, Guid backup) =>
        Path.Combine(agent.Configs.Paths.BackupsDirectory, _shortIds[server], $"{backup:N}.zip");

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"backups-{name}");
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
