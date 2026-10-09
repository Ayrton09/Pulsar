using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Pulsar.Api.Databases;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Databases;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>Database hosts and servers' MySQL/MariaDB databases, against a real server (Phase 13).</summary>
public sealed class GameDatabaseTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GameDatabaseFact]
    public async Task HostsAreCheckedBeforeSavingAndNeverShowTheirPassword()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;

        var wrong = HostRequest("wrong-password") with { Password = "certainly-not-the-password" };
        using (var refused = await admin.PostAsync("/api/v1/database-hosts", wrong))
        {
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            Assert.Contains("database_host.unreachable", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        var host = await CreateHostAsync(admin, "checked");
        var listed = await admin.Http.GetStringAsync("/api/v1/database-hosts", Ct);
        Assert.Contains(host.Name, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(TestMySql.Settings.Password, listed, StringComparison.Ordinal);

        // An update without a password keeps the saved one (and is checked with it).
        var renamed = await PutAsync<DatabaseHostView>(admin, $"/api/v1/database-hosts/{host.Id}",
            HostRequest("checked-renamed") with { Password = null, ServerAddress = "db.example.test", ServerPort = 3306 });
        Assert.Equal("checked-renamed", renamed.Name);
        Assert.Equal("db.example.test", renamed.ServerAddress);
        using (var test = await admin.PostAsync($"/api/v1/database-hosts/{host.Id}/test"))
        {
            await AccessScenario.EnsureSuccessAsync(test);
            Assert.False(string.IsNullOrWhiteSpace((await test.Content.ReadFromJsonAsync<DatabaseHostCheck>(AccessScenario.Json, Ct))!.Version));
        }

        using (var invalid = await admin.PostAsync("/api/v1/database-hosts", HostRequest("bad user host") with { UserHost = "x' OR 1" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        // Settings managers only.
        using var viewer = await scenario.SignedInWithAsync("db-host-viewer", Permissions.Server.View, Permissions.Server.DatabasesManage);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.GetAsync("/api/v1/database-hosts", Ct)).StatusCode);

        using (var deleted = await admin.DeleteAsync($"/api/v1/database-hosts/{host.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    [GameDatabaseFact]
    public async Task AServersDatabaseIsItsUsersAloneAndGoesWithTheServer()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "own");
        var host = await CreateHostAsync(admin, "own");
        var server = await InstalledServerAsync(admin, agent, "db-own");
        var other = await InstalledServerAsync(admin, agent, "db-other");

        var main = await CreateDatabaseAsync(admin, server, host.Id, "main");
        var otherMain = await CreateDatabaseAsync(admin, other, host.Id, "main");
        var shortId = (await ServerApi.GetAsync(admin, server))!.Server.ShortId;
        Assert.Equal($"s{shortId}_main", main.Database.Name);
        Assert.Equal(main.Database.Name, main.Database.UserName);
        Assert.Equal(host.Host, main.Database.Address);
        Assert.Equal(24, main.Password.Length);

        // The user works in its database and sees no other one.
        await using (var game = await TestMySql.OpenAsync(main.Database.UserName, main.Password, main.Database.Name))
        {
            await ExecuteAsync(game, "CREATE TABLE players (name VARCHAR(32)); INSERT INTO players VALUES ('ada')");
            // (A host's own leftovers, such as MariaDB's default "test" database open to everyone, are the host's business.)
            var visible = await TestMySql.ColumnAsync(game, "SHOW DATABASES");
            Assert.Contains(main.Database.Name, visible);
            Assert.DoesNotContain(otherMain.Database.Name, visible);
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(game, $"SELECT * FROM `{otherMain.Database.Name}`.players"));
        }

        await Assert.ThrowsAsync<MySqlException>(() => TestMySql.OpenAsync(main.Database.UserName, main.Password, otherMain.Database.Name));

        // A rotated password locks the old one out.
        var rotated = await PostAsync<ServerDatabaseCredentials>(admin, $"/api/v1/servers/{server}/databases/{main.Database.Id}/rotate-password");
        Assert.NotEqual(main.Password, rotated.Password);
        await Assert.ThrowsAsync<MySqlException>(() => TestMySql.OpenAsync(main.Database.UserName, main.Password, main.Database.Name));
        await using (var again = await TestMySql.OpenAsync(main.Database.UserName, rotated.Password, main.Database.Name))
        {
            Assert.Equal(["ada"], await TestMySql.ColumnAsync(again, "SELECT name FROM players"));
        }

        var read = await admin.Http.GetFromJsonAsync<ServerDatabaseCredentials>($"/api/v1/servers/{server}/databases/{main.Database.Id}/password",
            AccessScenario.Json, Ct);
        Assert.Equal(rotated.Password, read!.Password);

        // Two by default; names are the owner's but checked; the same name twice is refused.
        using (var taken = await admin.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(host.Id, "main")))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        }

        using (var invalid = await admin.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(host.Id, "Main;DROP")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        var logs = await CreateDatabaseAsync(admin, server, host.Id, "logs");
        using (var third = await admin.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(host.Id, "third")))
        {
            Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
            Assert.Contains("database.limit_reached", await third.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // Deleting one drops it on the host.
        using (var deleted = await admin.DeleteAsync($"/api/v1/servers/{server}/databases/{logs.Database.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.DoesNotContain(logs.Database.Name, await HostDatabasesAsync());

        // The host cannot go while servers have databases on it.
        using (var inUse = await admin.DeleteAsync($"/api/v1/database-hosts/{host.Id}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        }

        // Deleting the server drops its databases and their users.
        await DeleteServerAsync(admin, server);
        await DeleteServerAsync(admin, other);
        var left = await HostDatabasesAsync();
        Assert.DoesNotContain(main.Database.Name, left);
        Assert.DoesNotContain(otherMain.Database.Name, left);
        await using (var root = await TestMySql.OpenAsync())
        {
            Assert.Empty(await TestMySql.ColumnAsync(root, $"SELECT user FROM mysql.user WHERE user LIKE 's{shortId}%'"));
        }

        Assert.All(await AuditAsync(AuditActions.Server.DatabaseDeleted, server), e => Assert.True(Dropped(e)));
    }

    [GameDatabaseFact]
    public async Task OwnersSeeTheirDatabasesAndOnlyManagersReadPasswordsWithinTheLimit()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "access");
        var host = await CreateHostAsync(admin, "access");
        var server = await InstalledServerAsync(admin, agent, "db-access");

        // One at most, asked for at the same time: one gets it.
        await PutAsync<ServerDetails>(admin, $"/api/v1/servers/{server}/limits", new ServerLimitsRequest(null, null, 1));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            using var response = await admin.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(host.Id, $"race{i}"));
            return response.StatusCode;
        }));
        Assert.Single(attempts, s => s == HttpStatusCode.Created);
        Assert.Equal(3, attempts.Count(s => s == HttpStatusCode.Conflict));

        using var viewer = await scenario.SignedInWithAsync("db-viewer", Permissions.Server.View, Permissions.Server.DatabasesView);
        var list = await viewer.Http.GetFromJsonAsync<ServerDatabaseList>($"/api/v1/servers/{server}/databases", AccessScenario.Json, Ct);
        Assert.Equal(1, list!.Limit);
        var database = Assert.Single(list.Databases);
        Assert.Contains(list.Hosts, h => h.Id == host.Id);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.Http.GetAsync($"/api/v1/servers/{server}/databases/{database.Id}/password", Ct)).StatusCode);
        using (var create = await viewer.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(host.Id, "mine")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        // A host tied to other nodes is not offered, and refused.
        var elsewhere = await CreateHostAsync(admin, "elsewhere", [Guid.CreateVersion7()]);
        list = await admin.Http.GetFromJsonAsync<ServerDatabaseList>($"/api/v1/servers/{server}/databases", AccessScenario.Json, Ct);
        Assert.DoesNotContain(list!.Hosts, h => h.Id == elsewhere.Id);
        await PutAsync<ServerDetails>(admin, $"/api/v1/servers/{server}/limits", new ServerLimitsRequest(null, null, 5));
        using (var refused = await admin.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(elsewhere.Id, "away")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("database.host_not_allowed", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // A host that cannot be reached any more does not hold the server's deletion up: the audit log says what was left.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE database_hosts SET port = 1 WHERE id = {host.Id}", Ct);
        }

        await DeleteServerAsync(admin, server);
        var entry = Assert.Single(await AuditAsync(AuditActions.Server.DatabaseDeleted, server));
        Assert.False(Dropped(entry));
        Assert.Contains(database.Name, entry, StringComparison.Ordinal);
        await using var root = await TestMySql.OpenAsync();
        await ExecuteAsync(root, $"DROP DATABASE `{database.Name}`; DROP USER '{database.Name}'@'%'");
    }

    private static DatabaseHostRequest HostRequest(string name, IReadOnlyList<Guid>? nodes = null)
    {
        var settings = TestMySql.Settings;
        return new DatabaseHostRequest(name, settings.Server, (int)settings.Port, settings.UserID, settings.Password,
            null, null, null, nodes ?? []);
    }

    private static async Task<DatabaseHostView> CreateHostAsync(PanelClient admin, string name, IReadOnlyList<Guid>? nodes = null)
    {
        using var response = await admin.PostAsync("/api/v1/database-hosts", HostRequest(name, nodes));
        await AccessScenario.EnsureSuccessAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DatabaseHostView>(AccessScenario.Json, Ct))!;
    }

    private static async Task<ServerDatabaseCredentials> CreateDatabaseAsync(PanelClient client, Guid server, Guid host, string name)
    {
        using var response = await client.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(host, name));
        await AccessScenario.EnsureSuccessAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ServerDatabaseCredentials>(AccessScenario.Json, Ct))!;
    }

    private static async Task<T> PostAsync<T>(PanelClient client, string path)
    {
        using var response = await client.PostAsync(path);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(AccessScenario.Json, Ct))!;
    }

    private static async Task<T> PutAsync<T>(PanelClient client, string path, object body)
    {
        using var response = await client.PutAsync(path, body);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(AccessScenario.Json, Ct))!;
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<string>> HostDatabasesAsync()
    {
        await using var root = await TestMySql.OpenAsync();
        return await TestMySql.ColumnAsync(root, "SHOW DATABASES");
    }

    private async Task<List<string>> AuditAsync(string action, Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.Action == action && e.ServerId == new ServerId(server)).Select(e => e.Metadata).ToListAsync(Ct);
    }

    private static bool Dropped(string metadata)
    {
        using var json = JsonDocument.Parse(metadata);
        return json.RootElement.GetProperty("dropped").GetBoolean();
    }

    private static async Task DeleteServerAsync(PanelClient admin, Guid server)
    {
        using (var deleted = await admin.DeleteAsync($"/api/v1/servers/{server}"))
        {
            await AccessScenario.EnsureSuccessAsync(deleted);
        }

        await InProcessAgent.EventuallyAsync(async () => await ServerApi.GetAsync(admin, server) is null, "server deleted");
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"databases-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    private static async Task<Guid> InstalledServerAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }
}
