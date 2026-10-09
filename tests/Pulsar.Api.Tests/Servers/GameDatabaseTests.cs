using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Pulsar.Api.Databases;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Databases;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Databases;
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

        // A name is one host's: refused before connecting (the password is wrong here), when adding or renaming.
        using (var taken = await admin.PostAsync("/api/v1/database-hosts", HostRequest("checked-renamed") with { Password = "certainly-not-the-password" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Contains("database_host.name_taken", await taken.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        var second = await CreateHostAsync(admin, "checked-second");
        using (var taken = await admin.PutAsync($"/api/v1/database-hosts/{second.Id}", HostRequest("checked-renamed")))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Contains("database_host.name_taken", await taken.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // TLS: not required by default for a database on this machine or the LAN; required when asked (the test server has it),
        // and kept by an update that does not say.
        Assert.False(second.RequireTls);
        var tls = await PutAsync<DatabaseHostView>(admin, $"/api/v1/database-hosts/{second.Id}", HostRequest("checked-second") with { RequireTls = true });
        Assert.True(tls.RequireTls);
        tls = await PutAsync<DatabaseHostView>(admin, $"/api/v1/database-hosts/{second.Id}", HostRequest("checked-second") with { Password = null });
        Assert.True(tls.RequireTls);
        using (var test = await admin.PostAsync($"/api/v1/database-hosts/{second.Id}/test"))
        {
            await AccessScenario.EnsureSuccessAsync(test);
        }

        using (var deleted = await admin.DeleteAsync($"/api/v1/database-hosts/{second.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
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

            // "_" is a wildcard in a grant's database: the user gets its own database, not look-alikes it could create.
            var lookalike = $"s{(await ServerApi.GetAsync(admin, server))!.Server.ShortId}xmain";
            try
            {
                await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(game, $"CREATE DATABASE `{lookalike}`"));
                await using var root = await TestMySql.OpenAsync();
                await ExecuteAsync(root, $"CREATE DATABASE IF NOT EXISTS `{lookalike}`");
                await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(game, $"CREATE TABLE `{lookalike}`.players (name VARCHAR(32))"));
            }
            finally
            {
                await using var root = await TestMySql.OpenAsync();
                await ExecuteAsync(root, $"DROP DATABASE IF EXISTS `{lookalike}`");
            }
        }

        await Assert.ThrowsAsync<MySqlException>(() => TestMySql.OpenAsync(main.Database.UserName, main.Password, otherMain.Database.Name));

        // A rotated password locks the old one out.
        ServerDatabaseCredentials rotated;
        using (var response = await admin.PostAsync($"/api/v1/servers/{server}/databases/{main.Database.Id}/rotate-password"))
        {
            await AccessScenario.EnsureSuccessAsync(response);
            Assert.True(response.Headers.CacheControl?.NoStore);
            rotated = (await response.Content.ReadFromJsonAsync<ServerDatabaseCredentials>(AccessScenario.Json, Ct))!;
        }

        Assert.NotEqual(main.Password, rotated.Password);
        await Assert.ThrowsAsync<MySqlException>(() => TestMySql.OpenAsync(main.Database.UserName, main.Password, main.Database.Name));
        await using (var again = await TestMySql.OpenAsync(main.Database.UserName, rotated.Password, main.Database.Name))
        {
            Assert.Equal(["ada"], await TestMySql.ColumnAsync(again, "SELECT name FROM players"));
        }

        using (var response = await admin.GetAsync($"/api/v1/servers/{server}/databases/{main.Database.Id}/password"))
        {
            await AccessScenario.EnsureSuccessAsync(response);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var read = await response.Content.ReadFromJsonAsync<ServerDatabaseCredentials>(AccessScenario.Json, Ct);
            Assert.Equal(rotated.Password, read!.Password);
        }

        // Reading a password is audited; no audit entry holds one.
        var viewed = Assert.Single(await AuditAsync(AuditActions.Server.DatabasePasswordViewed, server));
        Assert.Contains(main.Database.Name, viewed, StringComparison.Ordinal);
        foreach (var action in new[] { AuditActions.Server.DatabaseCreated, AuditActions.Server.DatabasePasswordRotated, AuditActions.Server.DatabasePasswordViewed })
        {
            Assert.All(await AuditAsync(action, server), e =>
            {
                Assert.DoesNotContain(main.Password, e, StringComparison.Ordinal);
                Assert.DoesNotContain(rotated.Password, e, StringComparison.Ordinal);
            });
        }

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

        // What the host says (its account's name, the panel's address) is for settings managers: a server's managers get a
        // generic answer, and the host's test the details.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE database_hosts SET user_name = 'pulsar-test-nobody' WHERE id = {host.Id}", Ct);
        }

        using (var refused = await admin.PostAsync($"/api/v1/servers/{server}/databases/{database.Id}/rotate-password"))
        {
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync(Ct);
            Assert.Contains("database.host_unreachable", body, StringComparison.Ordinal);
            Assert.DoesNotContain("pulsar-test-nobody", body, StringComparison.Ordinal);
        }

        using (var refused = await admin.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(host.Id, "refused")))
        {
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            Assert.DoesNotContain("pulsar-test-nobody", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var refused = await admin.PostAsync($"/api/v1/database-hosts/{host.Id}/test"))
        {
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            Assert.Contains("pulsar-test-nobody", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Single((await admin.Http.GetFromJsonAsync<ServerDatabaseList>($"/api/v1/servers/{server}/databases", AccessScenario.Json, Ct))!.Databases);

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

    [GameDatabaseFact]
    public async Task AFailedCreateLeavesNothingBehind()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "failed");
        var host = await CreateHostAsync(admin, "failed");
        var server = await InstalledServerAsync(admin, agent, "db-failed");
        var shortId = (await ServerApi.GetAsync(admin, server))!.Server.ShortId;
        var taken = $"s{shortId}_taken";
        var cancelled = $"s{shortId}_cancelled";
        try
        {
            // A step after the database fails (its user exists already): the database goes again, and the place too.
            await using (var root = await TestMySql.OpenAsync())
            {
                await ExecuteAsync(root, $"CREATE USER '{taken}'@'%' IDENTIFIED BY 'SomeoneElsesPassword1'");
            }

            using (var refused = await admin.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(host.Id, "taken")))
            {
                Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            }

            Assert.DoesNotContain(taken, await HostDatabasesAsync());
            Assert.Empty((await admin.Http.GetFromJsonAsync<ServerDatabaseList>($"/api/v1/servers/{server}/databases", AccessScenario.Json, Ct))!.Databases);
            await using (var root = await TestMySql.OpenAsync())
            {
                await ExecuteAsync(root, $"DROP USER '{taken}'@'%'");
            }

            await CreateDatabaseAsync(admin, server, host.Id, "taken");

            // Given up (the request cancelled) after the database was made, while its user waits for the grant tables: the
            // database goes again too.
            var client = factory.Services.GetRequiredService<IDatabaseHostClient>();
            var settings = TestMySql.Settings;
            var connection = new DatabaseHostConnection(settings.Server, (int)settings.Port, settings.UserID, settings.Password, RequireTls: false);
            await using (var root = await TestMySql.OpenAsync())
            {
                await ExecuteAsync(root, "LOCK TABLES mysql.global_priv WRITE");
                using var cancel = new CancellationTokenSource();
                var create = client.CreateAsync(connection, cancelled, "%", ServerDatabase.NewPassword(), cancel.Token);
                await InProcessAgent.EventuallyAsync(async () => (await HostDatabasesAsync()).Contains(cancelled), "database created");
                await cancel.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
                await ExecuteAsync(root, "UNLOCK TABLES");
            }

            Assert.DoesNotContain(cancelled, await HostDatabasesAsync());
        }
        finally
        {
            await using var root = await TestMySql.OpenAsync();
            await ExecuteAsync(root, $"DROP USER IF EXISTS '{cancelled}'@'%'; DROP DATABASE IF EXISTS `{cancelled}`");
            await DeleteServerAsync(admin, server);
        }

        Assert.DoesNotContain(taken, await HostDatabasesAsync());
    }

    [DatabaseFact]
    public async Task TheClientChecksWhatItQuotesAndSaysWhenAHostIsAway()
    {
        var client = factory.Services.GetRequiredService<IDatabaseHostClient>();
        var away = new DatabaseHostConnection("127.0.0.1", 1, "pulsar", "password", RequireTls: false);
        var password = ServerDatabase.NewPassword();

        // Checked before connecting: nothing that could leave its quotes reaches a statement.
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateAsync(away, "sabc_x`; DROP DATABASE mysql; --", "%", password, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateAsync(away, "mysql", "%", password, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateAsync(away, "sabc_main", "%' OR '1", password, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateAsync(away, "sabc_main", "%", "short'password", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DropAsync(away, "sabc_main", "a b", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetPasswordAsync(away, "sabc_main", "%", "x", Ct));

        // A host that does not answer is a DatabaseHostException, whatever the step.
        await Assert.ThrowsAsync<DatabaseHostException>(() => client.CheckAsync(away, Ct));
        await Assert.ThrowsAsync<DatabaseHostException>(() => client.CreateAsync(away, "sabc_main", "%", password, Ct));
        await Assert.ThrowsAsync<DatabaseHostException>(() => client.DropAsync(away, "sabc_main", "%", Ct));
    }

    [DatabaseFact]
    public async Task ACreateThatFailsUnexpectedlyGivesItsPlaceBack()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "unexpected");
        var server = new ServerId(await InstalledServerAsync(scenario.Admin, agent, "db-unexpected"));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var host = await AddHostAsync(scope.ServiceProvider, "unexpected");
            var databases = DatabasesWith(scope.ServiceProvider, new FailingClient(new InvalidOperationException("Not a host's answer.")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => databases.CreateAsync(server, host.Id.Value, "main", Ct));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ServerDatabases.AnyAsync(d => d.ServerId == server, Ct));
        }
    }

    [DatabaseFact]
    public async Task ADeletedServersDatabasesAreTriedOncePerHostThatIsAway()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "away");
        var serverId = await InstalledServerAsync(scenario.Admin, agent, "db-away");
        var shortId = (await ServerApi.GetAsync(scenario.Admin, serverId))!.Server.ShortId;
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IPulsarDbContext>();
        var secrets = services.GetRequiredService<IDatabaseSecretProtector>();
        var first = await AddHostAsync(services, "away-1");
        var second = await AddHostAsync(services, "away-2");
        foreach (var (host, suffix) in new[] { (first, "a"), (first, "b"), (first, "c"), (second, "d") })
        {
            db.ServerDatabases.Add(ServerDatabase.Create(new ServerId(serverId), shortId, host, suffix, secrets.Protect("x"),
                DateTimeOffset.UtcNow).Value);
        }

        await db.SaveChangesAsync(Ct);

        // Each attempt may wait for a connection timeout: a host that failed once is not tried again for this server.
        var client = new FailingClient(new DatabaseHostException("Unable to connect to any of the specified MySQL hosts."));
        var server = await db.Servers.SingleAsync(s => s.Id == new ServerId(serverId), Ct);
        await DatabasesWith(services, client).DropAllAsync(server, CancellationToken.None);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, client.Calls);
        Assert.False(await db.ServerDatabases.AnyAsync(d => d.ServerId == server.Id, Ct));
        var entries = await AuditAsync(AuditActions.Server.DatabaseDeleted, serverId);
        Assert.Equal(4, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.False(Dropped(e));
            Assert.Contains("Unable to connect", e, StringComparison.Ordinal);
        });
    }

    private static async Task<DatabaseHost> AddHostAsync(IServiceProvider services, string name)
    {
        // Saved as is (not checked by connecting): nothing answers on port 1.
        var db = services.GetRequiredService<IPulsarDbContext>();
        var host = DatabaseHost.Create(name, "127.0.0.1", 1, "pulsar", services.GetRequiredService<IDatabaseSecretProtector>().Protect("password"),
            null, null, null, [], DateTimeOffset.UtcNow).Value;
        db.DatabaseHosts.Add(host);
        await db.SaveChangesAsync(Ct);
        return host;
    }

    private static ServerDatabases DatabasesWith(IServiceProvider services, IDatabaseHostClient client) => new(
        services.GetRequiredService<IPulsarDbContext>(), services.GetRequiredService<DatabaseHosts>(), services.GetRequiredService<IDatabaseSecretProtector>(),
        client, services.GetRequiredService<IServerLocks>(), services.GetRequiredService<IAuditLog>(), TimeProvider.System,
        NullLogger<ServerDatabases>.Instance);

    /// <summary>A host client whose every call fails as given, counting them.</summary>
    private sealed class FailingClient(Exception failure) : IDatabaseHostClient
    {
        private int _calls;

        public int Calls => _calls;

        public Task<string> CheckAsync(DatabaseHostConnection host, CancellationToken cancellationToken) => Fail<string>();

        public Task CreateAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken) =>
            Fail<bool>();

        public Task DropAsync(DatabaseHostConnection host, string name, string userHost, CancellationToken cancellationToken) => Fail<bool>();

        public Task SetPasswordAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken) =>
            Fail<bool>();

        private Task<T> Fail<T>()
        {
            Interlocked.Increment(ref _calls);
            return Task.FromException<T>(failure);
        }
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
        Assert.True(response.Headers.CacheControl?.NoStore); // it carries the password
        return (await response.Content.ReadFromJsonAsync<ServerDatabaseCredentials>(AccessScenario.Json, Ct))!;
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
