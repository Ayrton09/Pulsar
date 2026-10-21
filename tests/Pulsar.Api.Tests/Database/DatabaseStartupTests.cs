using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Infrastructure;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Database;

public sealed class DatabaseStartupTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task ReadinessIncludesTheDatabase()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [DatabaseFact]
    public async Task StartupAppliesMigrationsAndCreatesTheAdministratorRoleOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = factory.Server; // starts the panel against the fresh database

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // A second start must be a no-op.
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(applyMigrations: true, ct);
        }

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<PulsarDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
        var systemRoles = await db.Roles.Where(r => r.IsSystem).ToListAsync(ct);
        var administrator = Assert.Single(systemRoles);
        Assert.Equal("Administrator", administrator.Name);
    }
}

public sealed class DatabaseInitializerTests
{
    private static readonly string s_keys = TestKeys.NewDirectory();

    [Fact]
    public void MissingConnectionStringFailsWithAnActionableMessage()
    {
        var configuration = new ConfigurationBuilder().Build();

        var ex = Assert.Throws<DatabaseStartupException>(() => new ServiceCollection().AddPulsarInfrastructure(configuration));

        Assert.Contains("ConnectionStrings:Pulsar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableServerFailsWithHostPortAndNoPassword()
    {
        await using var services = Build("Host=127.0.0.1;Port=1;Database=pulsar;Username=pulsar;Password=s3cret-value;Timeout=3");
        await using var scope = services.CreateAsyncScope();

        var ex = await Assert.ThrowsAsync<DatabaseStartupException>(() => scope.ServiceProvider
            .GetRequiredService<IDatabaseInitializer>().InitializeAsync(applyMigrations: true, TestContext.Current.CancellationToken));

        Assert.Contains("127.0.0.1:1", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-value", ex.Message, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task PendingMigrationsBlockStartupWhenAutomaticMigrationIsDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        await using var services = Build(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();

        var ex = await Assert.ThrowsAsync<DatabaseStartupException>(() => initializer.InitializeAsync(applyMigrations: false, ct));

        Assert.Contains("migrate", ex.Message, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task ADatabaseANewerPulsarMigratedIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        await using var services = Build(database.ConnectionString);
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(applyMigrations: true, ct);
            // A later release's migration, which this one does not know (a backup restored onto an older install).
            await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().Database.ExecuteSqlRawAsync(
                "INSERT INTO \"__EFMigrationsHistory\" (migration_id, product_version) VALUES ('29991231000000_FromTheFuture', '10.0.0')", ct);
        }

        foreach (var apply in (bool[])[true, false])
        {
            await using var scope = services.CreateAsyncScope();
            var ex = await Assert.ThrowsAsync<DatabaseStartupException>(() =>
                scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(apply, ct));
            Assert.Contains("29991231000000_FromTheFuture", ex.Message, StringComparison.Ordinal);
            Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
        }
    }

    [DatabaseFact]
    public async Task FreshDatabaseInitializesWithoutLoggingErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var errors = new ErrorCollector();
        await using var services = Build(database.ConnectionString, errors);

        for (var start = 0; start < 2; start++)
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(applyMigrations: true, ct);
        }

        Assert.Empty(errors.Messages);
    }

    internal static ServiceProvider Build(string connectionString, ILoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Pulsar"] = connectionString,
                ["DataProtection:KeysPath"] = s_keys,
            })
            .Build();
        return new ServiceCollection()
            .AddLogging(builder =>
            {
                if (logs is not null)
                {
                    builder.AddProvider(logs);
                }
            })
            .AddPulsarInfrastructure(configuration)
            .BuildServiceProvider();
    }

    /// <summary>Collects every error-level log line (the test runner does not show output of passing tests).</summary>
    private sealed class ErrorCollector : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lock (Messages)
                {
                    Messages.Add(formatter(state, exception));
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
