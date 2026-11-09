using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using Pulsar.Api.Databases;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Databases;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Databases;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Databases;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Database hosts and servers' MySQL/MariaDB databases (Phase 13) and PostgreSQL ones (1.0.37), against real servers.
/// </summary>
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
                // The user was someone else's: it stays.
                Assert.Equal([taken], await TestMySql.ColumnAsync(root, $"SELECT user FROM mysql.user WHERE user = '{taken}'"));
                await ExecuteAsync(root, $"DROP USER '{taken}'@'%'");
            }

            await CreateDatabaseAsync(admin, server, host.Id, "taken");

            // Given up (the request cancelled) after the database was made, while its user waits for the grant tables: the
            // database goes again too, and so would the user had the server made it before the request was given up.
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
                await InProcessAgent.EventuallyAsync(async () => !(await HostDatabasesAsync()).Contains(cancelled), "database dropped");
                // Dropping the user waits for the grant tables too.
                await ExecuteAsync(root, "UNLOCK TABLES");
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
                Assert.Empty(await TestMySql.ColumnAsync(root, $"SELECT user FROM mysql.user WHERE user = '{cancelled}'"));
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
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await DatabasesWith(services, client).DropAllAsync(server, CancellationToken.None);
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(2, client.Calls);
        Assert.False(await db.ServerDatabases.AnyAsync(d => d.ServerId == server.Id, Ct));
        var entries = await AuditAsync(AuditActions.Server.DatabaseDeleted, serverId);
        Assert.Equal(4, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.False(Dropped(e));
            // What the host said (it names the panel's account there and the host's address) stays in the log.
            Assert.DoesNotContain("Unable to connect", e, StringComparison.Ordinal);
            Assert.Contains(DatabaseErrors.HostUnreachable.Code, e, StringComparison.Ordinal);
        });
    }

    [DatabaseFact]
    public async Task OverlappingRotationsLeaveTheHostAndThePanelWithTheSamePassword()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "rotations");
        var serverId = new ServerId(await InstalledServerAsync(scenario.Admin, agent, "db-rotations"));
        var shortId = (await ServerApi.GetAsync(scenario.Admin, serverId.Value))!.Server.ShortId;
        ServerDatabase database;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var host = await AddHostAsync(scope.ServiceProvider, "rotations");
            var protector = scope.ServiceProvider.GetRequiredService<IDatabaseSecretProtector>();
            database = ServerDatabase.Create(serverId, shortId, host, "main", protector.Protect(ServerDatabase.NewPassword()), DateTimeOffset.UtcNow).Value;
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            db.ServerDatabases.Add(database);
            await db.SaveChangesAsync(Ct);
        }

        // The first rotation's ALTER USER reaches the host first and its answer comes back last.
        var client = new SlowFirstPasswordClient();
        async Task RotateAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var rotated = await DatabasesWith(scope.ServiceProvider, client).RotatePasswordAsync(serverId, database.Id.Value, Ct);
            Assert.True(rotated.IsSuccess);
        }

        var first = RotateAsync();
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(client.Password is not null), "first rotation on the host");
        await Task.WhenAll(first, RotateAsync());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ServerDatabases.AsNoTracking()
                .SingleAsync(d => d.Id == database.Id, Ct);
            Assert.Equal(client.Password, scope.ServiceProvider.GetRequiredService<IDatabaseSecretProtector>().Unprotect(saved.PasswordProtected));
        }
    }

    [DatabaseFact]
    public async Task ADatabaseCreatedWhileItsServerWasDeletedIsDroppedAgain()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "late");
        var serverId = await InstalledServerAsync(admin, agent, "db-late");
        var client = new GatedCreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var host = await AddHostAsync(scope.ServiceProvider, "late");

        // The host is still creating it when the node confirms the server's deletion.
        var create = DatabasesWith(scope.ServiceProvider, client).CreateAsync(new ServerId(serverId), host.Id.Value, "late", Ct);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await DeleteServerAsync(admin, serverId);
        client.Release.SetResult();

        var created = await create;
        Assert.Equal(ServerErrors.NotFound, created.Error);
        Assert.NotNull(client.Created);
        Assert.Equal(client.Created, Assert.Single(client.Dropped));
    }

    [DatabaseFact]
    public async Task ARotationMeetingItsServersDeletionIsWaitedFor()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "rotated-away");
        var (serverId, database, deletion) = await DeletingServerWithDatabaseAsync(scenario.Admin, agent, "rotated-away");
        var client = new DeletionRaceClient();

        // A rotation is at the host when the node confirms the deletion: the database's row goes once the rotation saved it.
        var rotation = Task.Run(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await DatabasesWith(scope.ServiceProvider, client).RotatePasswordAsync(serverId, database.Id.Value, Ct);
        }, Ct);
        await client.Changing.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var completion = CompleteDeletionAsync(new NodeId(agent.Config!.NodeId), deletion, client);
        await client.Dropping.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        client.Release.SetResult();

        Assert.True((await rotation).IsSuccess);
        await completion;
        await AssertDeletedAsync(scenario.Admin, serverId, database, deletion);
        Assert.Equal([database.Name], client.Dropped);
        Assert.Single(await AuditAsync(AuditActions.Server.DatabasePasswordRotated, serverId.Value));
    }

    [DatabaseFact]
    public async Task AServersDeletionThatConflictsIsAppliedAgainWholly()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "deleted-twice");
        var node = new NodeId(agent.Config!.NodeId);
        var (serverId, database, deletion) = await DeletingServerWithDatabaseAsync(scenario.Admin, agent, "deleted-twice");

        // The deletion's progress is saved while its databases are dropped: the deletion's save conflicts, and it is
        // applied again (its databases dropped again) in a new transaction.
        var client = new DeletionRaceClient();
        client.Release.SetResult();
        client.OnFirstDrop = async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AgentServerEvents>()
                .OperationProgressAsync(node, new OperationProgress(deletion.Id.Value, "removing-account", null, null), Ct);
        };
        await CompleteDeletionAsync(node, deletion, client);

        await AssertDeletedAsync(scenario.Admin, serverId, database, deletion);
        Assert.Equal([database.Name, database.Name], client.Dropped);
    }

    /// <summary>An installed server with a database, whose deletion its node has started.</summary>
    private async Task<(ServerId Server, ServerDatabase Database, Operation Deletion)> DeletingServerWithDatabaseAsync(PanelClient admin,
        InProcessAgent agent, string name)
    {
        var serverId = new ServerId(await InstalledServerAsync(admin, agent, $"db-{name}"));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var server = await db.Servers.SingleAsync(s => s.Id == serverId, Ct);
        var host = await AddHostAsync(scope.ServiceProvider, name);
        var database = ServerDatabase.Create(serverId, server.ShortId, host, "main",
            scope.ServiceProvider.GetRequiredService<IDatabaseSecretProtector>().Protect(ServerDatabase.NewPassword()), DateTimeOffset.UtcNow).Value;
        db.ServerDatabases.Add(database);
        var deletion = Operation.Create(OperationType.ServerDelete, server.NodeId, serverId, null, OperationRequester.User, DateTimeOffset.UtcNow);
        Assert.True(server.BeginOperation(deletion, DateTimeOffset.UtcNow).IsSuccess);
        deletion.Started(DateTimeOffset.UtcNow);
        db.Operations.Add(deletion);
        await db.SaveChangesAsync(Ct);
        return (serverId, database, deletion);
    }

    /// <summary>The node reports the deletion done; the panel drops the server's databases through <paramref name="client"/>.</summary>
    private Task CompleteDeletionAsync(NodeId node, Operation deletion, IDatabaseHostClient client) => Task.Run(async () =>
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var events = ActivatorUtilities.CreateInstance<AgentServerEvents>(scope.ServiceProvider, DatabasesWith(scope.ServiceProvider, client));
        await events.OperationCompletedAsync(node, new OperationCompleted(deletion.Id.Value, deletion.ServerId!.Value.Value, OperationOutcome.Succeeded,
            null, null), Ct);
    }, Ct);

    /// <summary>The server and its database are gone, the deletion succeeded, and each is audited once.</summary>
    private async Task AssertDeletedAsync(PanelClient admin, ServerId serverId, ServerDatabase database, Operation deletion)
    {
        Assert.Null(await ServerApi.GetAsync(admin, serverId.Value));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            Assert.False(await db.ServerDatabases.AnyAsync(d => d.Id == database.Id, Ct));
            Assert.Equal(OperationStatus.Succeeded, (await db.Operations.AsNoTracking().SingleAsync(o => o.Id == deletion.Id, Ct)).Status);
        }

        Assert.True(Dropped(Assert.Single(await AuditAsync(AuditActions.Server.DatabaseDeleted, serverId.Value))));
        Assert.Single(await AuditAsync(AuditActions.Server.Deleted, serverId.Value));
    }

    [DatabaseFact]
    public async Task ThePanelsLastDatabaseHostGoesToOneRequest()
    {
        var client = new RecordingClient();
        var names = new List<string>();
        async Task<DomainError?> CreateAsync(string name)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var hosts = new DatabaseHosts(services.GetRequiredService<IPulsarDbContext>(), services.GetRequiredService<IDatabaseSecretProtector>(), client,
                services.GetRequiredService<IRowLocks>(), services.GetRequiredService<IAuditLog>(), TimeProvider.System);
            lock (names)
            {
                names.Add(name);
            }

            return (await hosts.CreateAsync(new DatabaseHostDefinition(name, "127.0.0.1", 1, "pulsar", "password", null, null, null, []), Ct)).Error;
        }

        try
        {
            int existing;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                existing = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().DatabaseHosts.CountAsync(Ct);
            }

            for (var i = existing; i < DatabaseHost.MaxHosts - 1; i++)
            {
                Assert.Null(await CreateAsync($"limit-{i}"));
            }

            // Checking each host takes a while: the last place is counted again once they are checked.
            var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => CreateAsync($"limit-race-{i}")));
            Assert.Single(answers, e => e is null);
            Assert.All(answers.Where(e => e is not null), e => Assert.Equal(DatabaseErrors.TooManyHosts, e));
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().DatabaseHosts.Where(h => names.Contains(h.Name)).ExecuteDeleteAsync(Ct);
        }
    }

    [DatabaseFact]
    public async Task TheSavedPasswordGoesOnlyToTheServerAndAccountItWasGivenFor()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var host = await AddHostAsync(services, "moved");
        var client = new RecordingClient();
        var hosts = new DatabaseHosts(services.GetRequiredService<IPulsarDbContext>(), services.GetRequiredService<IDatabaseSecretProtector>(), client,
            services.GetRequiredService<IRowLocks>(), services.GetRequiredService<IAuditLog>(), TimeProvider.System);

        // Another address, port or account without a password: refused before connecting anywhere.
        foreach (var (address, port, user) in new[] { ("203.0.113.9", 1, "pulsar"), ("127.0.0.1", 3306, "pulsar"), ("127.0.0.1", 1, "root") })
        {
            var moved = await hosts.UpdateAsync(host.Id.Value, new DatabaseHostDefinition("moved", address, port, user, null, null, null, null, []), Ct);
            Assert.Equal(DatabaseErrors.HostPasswordRequired, moved.Error);
        }

        Assert.Empty(client.Connections);

        // The same account on the same server keeps it (checked with it).
        var renamed = await hosts.UpdateAsync(host.Id.Value,
            new DatabaseHostDefinition("moved-renamed", "127.0.0.1", 1, "pulsar", null, "db.example.test", null, null, []), Ct);
        Assert.True(renamed.IsSuccess);
        var connection = Assert.Single(client.Connections);
        Assert.Equal(("127.0.0.1", 1, "pulsar", "password"), (connection.Host, connection.Port, connection.UserName, connection.Password));
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

    [Fact]
    public void ARolesPasswordGoesToPostgresAsTheVerifierItKeeps()
    {
        // Worked out with Python's hashlib and hmac for RFC 7677's password and salt (an implementation of its own).
        Assert.Equal(
            "SCRAM-SHA-256$4096:W22ZaJ0SNY7soEsUEjb6gQ==$WG5d8oPm3OtcPnkdi4Uo7BkeZkBFzpcXkuLmtbsT4qY=:wfPLwcE6nTWhTAmQ7tl2KeoiWGPlZqQxSrmfPwDl2dU=",
            Pulsar.Infrastructure.Databases.PostgresDatabaseHostClient.ScramVerifier("pencil", Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ==")));
    }

    [DatabaseFact]
    public async Task ThePanelsOwnDatabaseLetsNoOtherRoleIn()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var open = await db.Database.SqlQueryRaw<bool>(
                """SELECT has_database_privilege('public', current_database(), 'CONNECT') OR has_database_privilege('public', current_database(), 'TEMPORARY') AS "Value" """)
            .SingleAsync(Ct);
        Assert.False(open);
    }

    [DatabaseFact]
    public async Task ThePostgresClientChecksWhatItQuotesAndSaysWhenAHostIsAway()
    {
        var client = factory.Services.GetRequiredService<IDatabaseHostClient>();
        var away = new DatabaseHostConnection("127.0.0.1", 1, "pulsar", "password", RequireTls: false, DatabaseEngine.Postgres);
        var password = ServerDatabase.NewPassword();

        // Checked before connecting: nothing that could leave its quotes reaches a statement.
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateAsync(away, "sabc_x\"; DROP DATABASE postgres; --", "%", password, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateAsync(away, "postgres", "%", password, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateAsync(away, "sabc_main", "%", "short'password", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DropAsync(away, "pg_monitor", "%", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetPasswordAsync(away, "sabc_main", "%", "x", Ct));

        // A host that does not answer is a DatabaseHostException, whatever the step.
        await Assert.ThrowsAsync<DatabaseHostException>(() => client.CheckAsync(away, Ct));
        await Assert.ThrowsAsync<DatabaseHostException>(() => client.CreateAsync(away, "sabc_main", "%", password, Ct));
        await Assert.ThrowsAsync<DatabaseHostException>(() => client.DropAsync(away, "sabc_main", "%", Ct));
    }

    [PostgresHostFact]
    public async Task APostgresDatabaseIsItsRolesAloneAndGoesWithTheServer()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;
        var settings = TestPostgresHost.Settings;
        var request = new DatabaseHostRequest("postgres", settings.Host!, settings.Port, settings.Username!, settings.Password, null, null, "10.0.0.%", [],
            Engine: DatabaseEngine.Postgres);

        using (var refused = await admin.PostAsync("/api/v1/database-hosts", request with { Password = "certainly-not-the-password" }))
        {
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        }

        DatabaseHostView host;
        using (var response = await admin.PostAsync("/api/v1/database-hosts", request))
        {
            await AccessScenario.EnsureSuccessAsync(response);
            host = (await response.Content.ReadFromJsonAsync<DatabaseHostView>(AccessScenario.Json, Ct))!;
        }

        Assert.Equal(DatabaseEngine.Postgres, host.Engine);
        Assert.Equal(DatabaseHost.AnyHost, host.UserHost); // PostgreSQL's own pg_hba.conf says where roles connect from
        using (var test = await admin.PostAsync($"/api/v1/database-hosts/{host.Id}/test"))
        {
            await AccessScenario.EnsureSuccessAsync(test);
            Assert.StartsWith("PostgreSQL", (await test.Content.ReadFromJsonAsync<DatabaseHostCheck>(AccessScenario.Json, Ct))!.Version, StringComparison.Ordinal);
        }

        // What a host runs is fixed.
        using (var changed = await admin.PutAsync($"/api/v1/database-hosts/{host.Id}", request with { Password = null, Engine = DatabaseEngine.Mysql }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
            Assert.Contains("database_host.engine_fixed", await changed.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        await using var agent = await ConnectedAgentAsync(admin, "postgres");
        var server = await InstalledServerAsync(admin, agent, "pg-own");
        var other = await InstalledServerAsync(admin, agent, "pg-other");
        var main = await CreateDatabaseAsync(admin, server, host.Id, "main");
        var otherMain = await CreateDatabaseAsync(admin, other, host.Id, "main");
        Assert.Equal(DatabaseEngine.Postgres, main.Database.Engine);
        Assert.Equal(main.Database.Name, main.Database.UserName);

        // The role works in its own database, may do nothing else, and cannot get into anyone else's, nor the panel's.
        await using (var game = await TestPostgresHost.OpenAsync(main.Database.UserName, main.Password, main.Database.Name))
        {
            await TestPostgresHost.ScalarAsync(game, "CREATE TABLE players (name text); INSERT INTO players VALUES ('ada')");
            Assert.Equal("ada", await TestPostgresHost.ScalarAsync(game, "SELECT name FROM players"));
            Assert.Equal(main.Database.Name, await TestPostgresHost.ScalarAsync(game,
                "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()"));
            Assert.False((bool)(await TestPostgresHost.ScalarAsync(game,
                "SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR rolinherit FROM pg_roles WHERE rolname = current_user"))!);
            Assert.False((bool)(await TestPostgresHost.ScalarAsync(game, "SELECT has_database_privilege('public', current_database(), 'CONNECT')"))!);
            // One game cannot use up the connections the panel's own database needs.
            Assert.Equal(16, (int)(await TestPostgresHost.ScalarAsync(game, "SELECT rolconnlimit FROM pg_roles WHERE rolname = current_user"))!);
        }

        await TestPostgresHost.AssertRefusedAsync(main.Database.UserName, main.Password, otherMain.Database.Name);
        string panelDatabase;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            panelDatabase = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Database.GetDbConnection().Database;
        }

        await TestPostgresHost.AssertRefusedAsync(main.Database.UserName, main.Password, panelDatabase);

        // A rotated password locks the old one out.
        ServerDatabaseCredentials rotated;
        using (var response = await admin.PostAsync($"/api/v1/servers/{server}/databases/{main.Database.Id}/rotate-password"))
        {
            await AccessScenario.EnsureSuccessAsync(response);
            rotated = (await response.Content.ReadFromJsonAsync<ServerDatabaseCredentials>(AccessScenario.Json, Ct))!;
        }

        await TestPostgresHost.AssertRefusedAsync(main.Database.UserName, main.Password, main.Database.Name);
        await using (var again = await TestPostgresHost.OpenAsync(main.Database.UserName, rotated.Password, main.Database.Name))
        {
            Assert.Equal("ada", await TestPostgresHost.ScalarAsync(again, "SELECT name FROM players"));
        }

        // Deleting it drops the database and the role, even with a session still open; so does deleting a server.
        await using (var open = await TestPostgresHost.OpenAsync(main.Database.UserName, rotated.Password, main.Database.Name))
        {
            using var deleted = await admin.DeleteAsync($"/api/v1/servers/{server}/databases/{main.Database.Id}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await DeleteServerAsync(admin, other);
        await using (var root = await TestPostgresHost.OpenAsync())
        {
            foreach (var name in new[] { main.Database.Name, otherMain.Database.Name })
            {
                Assert.Null(await TestPostgresHost.ScalarAsync(root, "SELECT 1 FROM pg_database WHERE datname = $1", name));
                Assert.Null(await TestPostgresHost.ScalarAsync(root, "SELECT 1 FROM pg_roles WHERE rolname = $1", name));
            }
        }

        await DeleteServerAsync(admin, server);
        using (var deleted = await admin.DeleteAsync($"/api/v1/database-hosts/{host.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    /// <summary>A game that can keep its data in a PostgreSQL database (1.0.37), installed with no program of its own.</summary>
    private const string DatabaseGameManifest = """
        {
          "schemaVersion": 1, "key": "db-game", "version": "1.0.0", "name": "Database game", "platforms": ["windows-x64"],
          "install": { "steps": [ { "type": "ensureDirectory", "path": "data" } ] },
          "runtime": { "windows-x64": { "executable": "bin/game.exe", "console": { "mode": "pty" } } },
          "readiness": { "type": "logPattern", "pattern": "^ready", "timeoutSeconds": 60 },
          "stop": { "steps": [ { "type": "ctrlC" } ], "timeoutSeconds": 15 },
          "database": { "engine": "postgres", "environment": {
            "GAME_DB": "{{database.user}}@{{database.host}}:{{database.port}}/{{database.name}}", "GAME_DB_PASSWORD": "{{database.password}}" } }
        }
        """;

    [DatabaseFact]
    public async Task AGameKeepsItsDataInADatabaseOfItsServerChosenAndPutBack()
    {
        // A panel whose database hosts answer as PostgreSQL would, without one: the flow is the panel's.
        var hosts = new GameHostClient();
        await AccessScenario.CreateAsync(factory);
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IDatabaseHostClient>(hosts)));
        using var admin = await PanelClient.CreateAsync(app);
        using (var login = await admin.LoginAsync(PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword))
        {
            Assert.True(login.IsSuccessStatusCode);
        }

        var node = await NodeApi.CreateAsync(admin, "databases-game");
        await using var agent = new InProcessAgent(app);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");

        DatabaseHostView host;
        using (var created = await admin.PostAsync("/api/v1/database-hosts",
                   new DatabaseHostRequest("game-postgres", "127.0.0.1", 5432, "pulsar_games", "host password", null, null, null, [], Engine: DatabaseEngine.Postgres)))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            host = (await created.Content.ReadFromJsonAsync<DatabaseHostView>(AccessScenario.Json, Ct))!;
        }

        using (var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(DatabaseGameManifest)))
        {
            await AccessScenario.EnsureSuccessAsync(imported);
        }

        var template = (await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct))!
            .Single(t => t.Key == "db-game");
        Guid server;
        using (var created = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(node.Node.Id, template.Id, "db-game", null)))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            server = (await created.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
        }

        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        var shortId = (await ServerApi.GetAsync(admin, server))!.Server.ShortId;

        // Its own files to begin with.
        var list = await ListAsync(admin, server);
        Assert.Equal(new GameDatabaseChoice(DatabaseEngine.Postgres, null, true), list.Game);
        Assert.Null(agent.StoredSpec(server).Database);

        // A database chosen: made on the host, sent to the node, not counted in the limit, not deleted while the game's.
        using (var set = await admin.PutAsync($"/api/v1/servers/{server}/databases/game", new GameDatabaseRequest(host.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        }

        list = await ListAsync(admin, server);
        var game = Assert.Single(list.Databases);
        Assert.Equal(($"s{shortId}_game", true, DatabaseEngine.Postgres), (game.Name, game.ForGame, game.Engine));
        Assert.Equal(game.Id, list.Game!.DatabaseId);
        var password = await PasswordAsync(admin, server, game.Id);
        Assert.Equal(password, hosts.Passwords[game.Name]);
        var spec = agent.StoredSpec(server).Database!;
        Assert.Equal(("postgres", host.Host, host.Port, game.Name, password), (spec.Engine, spec.Host, spec.Port, spec.Name, spec.Password));

        await CreateDatabaseAsync(admin, server, host.Id, "main");
        await CreateDatabaseAsync(admin, server, host.Id, "logs");
        using (var third = await admin.PostAsync($"/api/v1/servers/{server}/databases", new CreateDatabaseRequest(host.Id, "third")))
        {
            Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        }

        using (var deleted = await admin.DeleteAsync($"/api/v1/servers/{server}/databases/{game.Id}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
            Assert.Contains("database.in_use_by_game", await deleted.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(game.Name, hosts.Dropped);

        // A new password reaches the node too.
        using (var rotated = await admin.PostAsync($"/api/v1/servers/{server}/databases/{game.Id}/rotate-password"))
        {
            await AccessScenario.EnsureSuccessAsync(rotated);
            password = (await rotated.Content.ReadFromJsonAsync<ServerDatabaseCredentials>(AccessScenario.Json, Ct))!.Password;
        }

        Assert.Equal(password, agent.StoredSpec(server).Database!.Password);

        // Back on its own files: the database stays (with its data), and is the game's again when chosen again.
        using (var back = await admin.PutAsync($"/api/v1/servers/{server}/databases/game", new GameDatabaseRequest(null)))
        {
            Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        }

        Assert.Null(agent.StoredSpec(server).Database);
        list = await ListAsync(admin, server);
        Assert.Null(list.Game!.DatabaseId);
        Assert.False(list.Databases.Single(d => d.Id == game.Id).ForGame);
        using (var again = await admin.PutAsync($"/api/v1/servers/{server}/databases/game", new GameDatabaseRequest(host.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        Assert.Equal(game.Id, (await ListAsync(admin, server)).Game!.DatabaseId);
        Assert.Equal(game.Name, agent.StoredSpec(server).Database!.Name);
        Assert.Single(hosts.Created, name => name == game.Name); // made once

        // A host of another engine is not the game's; nor is a game that takes none; nor is a clone made while it has one.
        using (var back = await admin.PutAsync($"/api/v1/servers/{server}/databases/game", new GameDatabaseRequest(null)))
        {
            Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        }

        DatabaseHostView mysql;
        using (var created = await admin.PostAsync("/api/v1/database-hosts",
                   new DatabaseHostRequest("game-mysql", "127.0.0.1", 3306, "pulsar", "host password", null, null, null, [])))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            mysql = (await created.Content.ReadFromJsonAsync<DatabaseHostView>(AccessScenario.Json, Ct))!;
        }

        using (var mismatch = await admin.PutAsync($"/api/v1/servers/{server}/databases/game", new GameDatabaseRequest(mysql.Id)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            Assert.Contains("database.engine_mismatch", await mismatch.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        var plain = (await ServerApi.CreateAsync(admin, node.Node.Id, "db-plain")).Server.Id;
        await ServerApi.WaitForAsync(admin, plain, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        Assert.Null((await ListAsync(admin, plain)).Game);
        using (var unsupported = await admin.PutAsync($"/api/v1/servers/{plain}/databases/game", new GameDatabaseRequest(host.Id)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
            Assert.Contains("database.game_unsupported", await unsupported.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var set = await admin.PutAsync($"/api/v1/servers/{server}/databases/game", new GameDatabaseRequest(host.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        }

        using (var clone = await admin.PostAsync($"/api/v1/servers/{server}/clone", new { nodeId = node.Node.Id, name = "db-game copy" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, clone.StatusCode);
            Assert.Contains("server.clone_game_database", await clone.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // A host whose nodes are narrowed after the choice no longer reaches the node: a new password is said not to.
        var elsewhere = await NodeApi.CreateAsync(admin, "databases-game-elsewhere");
        using (var narrowed = await admin.PutAsync($"/api/v1/database-hosts/{host.Id}",
                   new DatabaseHostRequest("game-postgres", "127.0.0.1", 5432, "pulsar_games", null, null, null, null, [elsewhere.Node.Id])))
        {
            await AccessScenario.EnsureSuccessAsync(narrowed);
        }

        using (var rotated = await admin.PostAsync($"/api/v1/servers/{server}/databases/{game.Id}/rotate-password"))
        {
            Assert.Equal(HttpStatusCode.Conflict, rotated.StatusCode);
            Assert.Contains("database.password_not_given", await rotated.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var widened = await admin.PutAsync($"/api/v1/database-hosts/{host.Id}",
                   new DatabaseHostRequest("game-postgres", "127.0.0.1", 5432, "pulsar_games", null, null, null, null, [])))
        {
            await AccessScenario.EnsureSuccessAsync(widened);
        }

        // Two choices at once (a double click): one database, the game's.
        var twice = (await admin.PostAsync("/api/v1/servers", new CreateServerRequest(node.Node.Id, template.Id, "db-game-twice", null)))
            is var response && response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id
            : throw new InvalidOperationException(await response.Content.ReadAsStringAsync(Ct));
        response.Dispose();
        await ServerApi.WaitForAsync(admin, twice, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        var both = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            admin.PutAsync($"/api/v1/servers/{twice}/databases/game", new GameDatabaseRequest(host.Id))));
        Assert.All(both, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        foreach (var answer in both)
        {
            answer.Dispose();
        }

        var twiceGame = Assert.Single((await ListAsync(admin, twice)).Databases);
        Assert.True(twiceGame.ForGame);
        Assert.Single(hosts.Created, name => name == twiceGame.Name);

        // A database of the server named for its game on another host is not taken over.
        using (var back = await admin.PutAsync($"/api/v1/servers/{twice}/databases/game", new GameDatabaseRequest(null)))
        {
            Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        }

        using (var deleted = await admin.DeleteAsync($"/api/v1/servers/{twice}/databases/{twiceGame.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await CreateDatabaseAsync(admin, twice, mysql.Id, "game");
        using (var taken = await admin.PutAsync($"/api/v1/servers/{twice}/databases/game", new GameDatabaseRequest(host.Id)))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Contains("database.game_name_taken", await taken.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        await DeleteServerAsync(admin, twice);

        // Audited, never with a password; the server's deletion drops them all.
        var changes = await AuditAsync(AuditActions.Server.GameDatabaseChanged, server);
        Assert.NotEmpty(changes);
        Assert.All(changes, e => Assert.DoesNotContain(password, e, StringComparison.Ordinal));
        await DeleteServerAsync(admin, server);
        await DeleteServerAsync(admin, plain);
        Assert.Contains(game.Name, hosts.Dropped);
    }

    [DatabaseFact]
    public async Task TheGamesDatabaseGoesWithItsServersBackupsAndComesBackFirst()
    {
        var hosts = new GameHostClient();
        var dumps = new FakeDumps();
        await AccessScenario.CreateAsync(factory);
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDatabaseHostClient>(hosts);
            services.AddSingleton<IGameDatabaseDumps>(dumps);
        }));
        using var admin = await PanelClient.CreateAsync(app);
        using (var login = await admin.LoginAsync(PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword))
        {
            Assert.True(login.IsSuccessStatusCode);
        }

        var node = await NodeApi.CreateAsync(admin, "databases-backups");
        await using var agent = new InProcessAgent(app);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        DatabaseHostView host;
        using (var created = await admin.PostAsync("/api/v1/database-hosts",
                   new DatabaseHostRequest("backup-postgres", "127.0.0.1", 5432, "pulsar_games", "host password", null, null, null, [], Engine: DatabaseEngine.Postgres)))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            host = (await created.Content.ReadFromJsonAsync<DatabaseHostView>(AccessScenario.Json, Ct))!;
        }

        using (var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(DatabaseGameManifest.Replace("\"db-game\"", "\"db-backups\"",
                   StringComparison.Ordinal))))
        {
            await AccessScenario.EnsureSuccessAsync(imported);
        }

        var template = (await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct))!
            .Single(t => t.Key == "db-backups");
        Guid server;
        using (var created = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(node.Node.Id, template.Id, "db-backups", null)))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            server = (await created.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
        }

        var installed = await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        var directory = Path.Combine(agent.ServersRoot, installed.Server.ShortId);
        var config = Path.Combine(directory, "data", "server.cfg");
        await File.WriteAllTextAsync(config, "hostname one", Ct);
        using (var set = await admin.PutAsync($"/api/v1/servers/{server}/databases/game", new GameDatabaseRequest(host.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        }

        var game = Assert.Single((await ListAsync(admin, server)).Databases);

        // A backup holds the dump the panel made as the game's role, kept as Pulsar's own (not one of the server's files).
        dumps.Next = [.. Enumerable.Range(0, 200_000).Select(i => (byte)(i * 13))];
        var backup = await BackupAsync(admin, server);
        Assert.True(backup.HasGameDatabase);
        Assert.Equal(game.Name, Assert.Single(dumps.Dumped).Name);
        Assert.Equal("127.0.0.1", dumps.Dumped[0].Host); // the address the panel uses
        using (var zip = ZipFile.OpenRead(Path.Combine(agent.Configs.Paths.BackupsDirectory, installed.Server.ShortId, $"{backup.Id:N}.zip")))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "data/server.cfg");
            var entry = Assert.Single(zip.Entries, e => e.FullName == BackupDatabaseDump.Entry);
            await using var content = await entry.OpenAsync(Ct);
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, Ct);
            Assert.Equal(dumps.Next, copy.ToArray());
        }

        // Its file goes once the node took it.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(dumps.Files.TrueForAll(f => !File.Exists(f))), "the dump's file deleted");

        // A full restore gives it back to the panel, which restores it before the node writes a file.
        await File.WriteAllTextAsync(config, "hostname two", Ct);
        dumps.BeforeRestore = () => dumps.ConfigWhenRestored = File.ReadAllText(config);
        await RestoreAsync(admin, server, $"/api/v1/servers/{server}/backups/{backup.Id}/restore", OperationStatus.Succeeded);
        var restored = Assert.Single(dumps.Restored);
        Assert.Equal(game.Name, restored.Name);
        Assert.Equal(dumps.Next, restored.Content);
        Assert.Equal("hostname two", dumps.ConfigWhenRestored);
        Assert.Equal("hostname one", await File.ReadAllTextAsync(config, Ct));
        Assert.False(File.Exists(Path.Combine(directory, ".pulsar", "database.dump")));

        // A restore of some files leaves the database.
        await RestoreAsync(admin, server, $"/api/v1/servers/{server}/backups/{backup.Id}/restore-files", OperationStatus.Succeeded,
            new RestoreBackupFilesRequest(["data"], false));
        Assert.Single(dumps.Restored);

        // One the panel cannot restore ends the restore with every file as it was, and says why.
        await File.WriteAllTextAsync(config, "hostname three", Ct);
        dumps.Refuse = true;
        var failed = await RestoreAsync(admin, server, $"/api/v1/servers/{server}/backups/{backup.Id}/restore", OperationStatus.Failed);
        Assert.Equal(BackupErrors.DatabaseFailed, failed.ErrorCode);
        Assert.Contains("administrator", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("hostname three", await File.ReadAllTextAsync(config, Ct));

        // A restore that ends before giving it back (its archive changed on the node) frees its place among the node's transfers.
        dumps.Refuse = false;
        var archive = Path.Combine(agent.Configs.Paths.BackupsDirectory, installed.Server.ShortId, $"{backup.Id:N}.zip");
        var bytes = await File.ReadAllBytesAsync(archive, Ct);
        bytes[^40] ^= 0xFF;
        await File.WriteAllBytesAsync(archive, bytes, Ct);
        await RestoreAsync(admin, server, $"/api/v1/servers/{server}/backups/{backup.Id}/restore", OperationStatus.Failed);
        var transfers = app.Services.GetRequiredService<FileTransfers>();
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(transfers.Running(new NodeId(node.Node.Id)) == 0), "the restore's ticket freed");
        Assert.Single(dumps.Restored, r => r.Name == game.Name);

        // A dump that cannot be made makes no backup; with the game back on its own files, backups hold none.
        dumps.Refuse = true;
        using (var refused = await admin.PostAsync($"/api/v1/servers/{server}/backups", new CreateBackupRequest(null)))
        {
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            Assert.Contains("backups.database_failed", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        dumps.Refuse = false;
        using (var back = await admin.PutAsync($"/api/v1/servers/{server}/databases/game", new GameDatabaseRequest(null)))
        {
            Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        }

        Assert.False((await BackupAsync(admin, server)).HasGameDatabase);
        Assert.Single(dumps.Dumped);
        await DeleteServerAsync(admin, server);
    }

    [PostgresHostFact]
    public async Task PostgresOwnProgramsDumpAGameDatabaseAndRestoreItWhole()
    {
        var settings = TestPostgresHost.Settings;
        var admin = new DatabaseHostConnection(settings.Host!, settings.Port, settings.Username!, settings.Password!, RequireTls: false,
            DatabaseEngine.Postgres);
        var client = factory.Services.GetRequiredService<IDatabaseHostClient>();
        var name = $"s{Guid.NewGuid().ToString("N")[..8]}_game";
        var password = ServerDatabase.NewPassword();
        await client.CreateAsync(admin, name, "%", password, Ct);
        try
        {
            await using (var game = await TestPostgresHost.OpenAsync(name, password, name))
            {
                await TestPostgresHost.ScalarAsync(game, "CREATE TABLE players (name text PRIMARY KEY); INSERT INTO players VALUES ('ada'), ('grace')");
            }

            // The programs of the PostgreSQL the tests use (CI's are where Pulsar looks; a development one names its folder).
            var dumps = new PostgresDumps(Options.Create(new GameDatabaseOptions
            {
                PostgresBin = Environment.GetEnvironmentVariable("PULSAR_TEST_PGBIN"),
                DumpDirectory = Path.Combine(Path.GetTempPath(), "pulsar-test-dumps"),
            }), NullLogger<PostgresDumps>.Instance);
            var target = new GameDatabaseTarget(admin.Host, admin.Port, name, password, RequireTls: false);
            var dump = await dumps.DumpAsync(target, Ct);
            try
            {
                Assert.True(dump.Size > 0);
                await using (var game = await TestPostgresHost.OpenAsync(name, password, name))
                {
                    await TestPostgresHost.ScalarAsync(game, "DELETE FROM players; CREATE TABLE later (x int)");
                }

                // A session of the game's still open: ended for the swap.
                await using var lingering = await TestPostgresHost.OpenAsync(name, password, name);
                await using (var content = File.OpenRead(dump.Path))
                {
                    await dumps.RestoreAsync(target, admin, content, dump.Size, () => true, Ct);
                }
            }
            finally
            {
                File.Delete(dump.Path);
            }

            await using (var game = await TestPostgresHost.OpenAsync(name, password, name))
            {
                Assert.Equal(2L, await TestPostgresHost.ScalarAsync(game, "SELECT count(*) FROM players"));
                Assert.Equal(DBNull.Value, await TestPostgresHost.ScalarAsync(game, "SELECT to_regclass('later')::text"));
                Assert.Equal(name, await TestPostgresHost.ScalarAsync(game, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()"));
                Assert.False((bool)(await TestPostgresHost.ScalarAsync(game, "SELECT has_database_privilege('public', current_database(), 'CONNECT')"))!);
            }

            await using (var root = await TestPostgresHost.OpenAsync())
            {
                Assert.Equal(0L, await TestPostgresHost.ScalarAsync(root, "SELECT count(*) FROM pg_database WHERE datname IN ($1, $2)", name + "-r", name + "-o"));
            }

            // A dump that is not whole changes nothing.
            await using (var cut = new MemoryStream([1, 2, 3]))
            {
                await Assert.ThrowsAsync<GameDatabaseDumpException>(() => dumps.RestoreAsync(target, admin, cut, 10, () => true, Ct));
            }

            await using (var game = await TestPostgresHost.OpenAsync(name, password, name))
            {
                Assert.Equal(2L, await TestPostgresHost.ScalarAsync(game, "SELECT count(*) FROM players"));
            }
        }
        finally
        {
            await client.DropAsync(admin, name, "%", CancellationToken.None);
        }
    }

    private static async Task<BackupView> BackupAsync(PanelClient client, Guid server)
    {
        Guid id;
        using (var response = await client.PostAsync($"/api/v1/servers/{server}/backups", new CreateBackupRequest(null)))
        {
            await AccessScenario.EnsureSuccessAsync(response);
            id = (await response.Content.ReadFromJsonAsync<BackupView>(AccessScenario.Json, Ct))!.Id;
        }

        BackupView? made = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            made = (await client.Http.GetFromJsonAsync<BackupList>(new Uri($"/api/v1/servers/{server}/backups", UriKind.Relative), AccessScenario.Json, Ct))!
                .Backups.SingleOrDefault(b => b.Id == id);
            return made?.Status is BackupStatus.Completed or BackupStatus.Failed;
        }, () => $"backup made (last: {made?.Status} {made?.ErrorMessage})");
        Assert.Equal(BackupStatus.Completed, made!.Status);
        return made;
    }

    private static async Task<OperationView> RestoreAsync(PanelClient client, Guid server, string path, OperationStatus expected, object? body = null)
    {
        using (var response = await client.PostAsync(path, body))
        {
            await AccessScenario.EnsureSuccessAsync(response);
        }

        var details = await ServerApi.WaitForAsync(client, server, s => s.Server.ActiveOperation is null, "restored");
        var restore = details.RecentOperations.First(o => o.Type == OperationType.BackupRestore);
        Assert.Equal(expected, restore.Status);
        return restore;
    }

    /// <summary>A database whose dumps are the bytes given, and whose restores are kept (or refused).</summary>
    private sealed class FakeDumps : IGameDatabaseDumps
    {
        public byte[] Next { get; set; } = [1, 2, 3];

        public bool Refuse { get; set; }

        public Action? BeforeRestore { get; set; }

        public string? ConfigWhenRestored { get; set; }

        public List<GameDatabaseTarget> Dumped { get; } = [];

        public List<string> Files { get; } = [];

        public List<(string Name, byte[] Content)> Restored { get; } = [];

        public async Task<GameDatabaseDump> DumpAsync(GameDatabaseTarget target, CancellationToken cancellationToken)
        {
            if (Refuse)
            {
                throw new GameDatabaseDumpException("pg_dump failed (1): refused for the test");
            }

            Dumped.Add(target);
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pulsar-dumps")).FullName;
            var file = Path.Combine(folder, $"{Guid.NewGuid():N}.dump");
            await File.WriteAllBytesAsync(file, Next, cancellationToken);
            Files.Add(file);
            return new GameDatabaseDump(file, Next.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Next)));
        }

        public async Task RestoreAsync(GameDatabaseTarget target, DatabaseHostConnection admin, Stream content, long length, Func<bool> received,
            CancellationToken receiving)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, receiving);
            if (!received())
            {
                throw new GameDatabaseDumpException("given up");
            }
            if (Refuse)
            {
                throw new GameDatabaseDumpException("pg_restore failed (1): refused for the test");
            }

            BeforeRestore?.Invoke();
            Restored.Add((target.Name, copy.ToArray()));
        }
    }

    private static async Task<ServerDatabaseList> ListAsync(PanelClient client, Guid server) =>
        (await client.Http.GetFromJsonAsync<ServerDatabaseList>(new Uri($"/api/v1/servers/{server}/databases", UriKind.Relative), AccessScenario.Json, Ct))!;

    private static async Task<string> PasswordAsync(PanelClient client, Guid server, Guid database)
    {
        using var response = await client.GetAsync($"/api/v1/servers/{server}/databases/{database}/password");
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ServerDatabaseCredentials>(AccessScenario.Json, Ct))!.Password;
    }

    private static ServerDatabases DatabasesWith(IServiceProvider services, IDatabaseHostClient client) => new(
        services.GetRequiredService<IPulsarDbContext>(), services.GetRequiredService<DatabaseHosts>(), services.GetRequiredService<IDatabaseSecretProtector>(),
        client, services.GetRequiredService<IRowLocks>(), services.GetRequiredService<IAuditLog>(), services.GetRequiredService<IAgentConnections>(),
        services.GetRequiredService<AgentSessionStore>(), services.GetRequiredService<GameDatabaseTurns>(), TimeProvider.System,
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

    /// <summary>A host that answers every check (after a moment, as over a network), keeping where it was asked to connect.</summary>
    private sealed class RecordingClient : RecordingClientBase
    {
        public List<DatabaseHostConnection> Connections { get; } = [];

        public override async Task<string> CheckAsync(DatabaseHostConnection host, CancellationToken cancellationToken)
        {
            lock (Connections)
            {
                Connections.Add(host);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            return await base.CheckAsync(host, cancellationToken);
        }
    }

    /// <summary>A PostgreSQL-like host that keeps what it was asked to make, drop and set.</summary>
    private sealed class GameHostClient : RecordingClientBase
    {
        public List<string> Created { get; } = [];

        public List<string> Dropped { get; } = [];

        public Dictionary<string, string> Passwords { get; } = [];

        public override Task<string> CheckAsync(DatabaseHostConnection host, CancellationToken cancellationToken) => Task.FromResult("PostgreSQL 18.6");

        public override Task CreateAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken)
        {
            lock (Created)
            {
                Created.Add(name);
                Passwords[name] = password;
            }

            return Task.CompletedTask;
        }

        public override Task DropAsync(DatabaseHostConnection host, string name, string userHost, CancellationToken cancellationToken)
        {
            lock (Created)
            {
                Dropped.Add(name);
            }

            return Task.CompletedTask;
        }

        public override Task SetPasswordAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken)
        {
            lock (Created)
            {
                Passwords[name] = password;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A host whose first password change takes effect at once but is answered late.</summary>
    private sealed class SlowFirstPasswordClient : RecordingClientBase
    {
        private int _changes;

        public string? Password { get; private set; }

        public override async Task SetPasswordAsync(DatabaseHostConnection host, string name, string userHost, string password,
            CancellationToken cancellationToken)
        {
            Password = password;
            if (Interlocked.Increment(ref _changes) == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            }
        }
    }

    /// <summary>A host whose CREATE waits for the test, keeping what it created and dropped.</summary>
    private sealed class GatedCreateClient : RecordingClientBase
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Created { get; private set; }

        public List<string> Dropped { get; } = [];

        public override async Task CreateAsync(DatabaseHostConnection host, string name, string userHost, string password,
            CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Created = name;
        }

        public override Task DropAsync(DatabaseHostConnection host, string name, string userHost, CancellationToken cancellationToken)
        {
            lock (Dropped)
            {
                Dropped.Add(name);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A host whose password change waits for the test, keeping what it dropped; its first drop signals and runs
    /// <see cref="OnFirstDrop"/>.
    /// </summary>
    private sealed class DeletionRaceClient : RecordingClientBase
    {
        public TaskCompletionSource Changing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Dropping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<Task>? OnFirstDrop { get; set; }

        public List<string> Dropped { get; } = [];

        public override async Task SetPasswordAsync(DatabaseHostConnection host, string name, string userHost, string password,
            CancellationToken cancellationToken)
        {
            Changing.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }

        public override async Task DropAsync(DatabaseHostConnection host, string name, string userHost, CancellationToken cancellationToken)
        {
            int count;
            lock (Dropped)
            {
                Dropped.Add(name);
                count = Dropped.Count;
            }

            Dropping.TrySetResult();
            if (count == 1 && OnFirstDrop is { } first)
            {
                await first();
            }
        }
    }

    /// <summary>A host that answers every call (overridden to watch some).</summary>
    private abstract class RecordingClientBase : IDatabaseHostClient
    {
        public virtual Task<string> CheckAsync(DatabaseHostConnection host, CancellationToken cancellationToken) => Task.FromResult("11.8.3-MariaDB");

        public virtual Task CreateAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public virtual Task DropAsync(DatabaseHostConnection host, string name, string userHost, CancellationToken cancellationToken) => Task.CompletedTask;

        public virtual Task SetPasswordAsync(DatabaseHostConnection host, string name, string userHost, string password,
            CancellationToken cancellationToken) => Task.CompletedTask;
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
