using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>The panel against its own fresh PostgreSQL database (created lazily, only when a test uses it).</summary>
public sealed class PulsarApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private TestDatabase? _database;

    public TestDatabase Database => _database ?? throw new InvalidOperationException("Database tests are disabled.");

    public async ValueTask InitializeAsync()
    {
        if (TestDatabase.IsEnabled)
        {
            _database = await TestDatabase.CreateAsync();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Pulsar", _database?.ConnectionString ?? "Host=database-tests-disabled");
    }
}

/// <summary>The panel without a database, for tests of the HTTP pipeline only.</summary>
public sealed class NoDatabaseApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Pulsar", "Host=no-database-in-these-tests");
        builder.ConfigureTestServices(services => services.AddScoped<IDatabaseInitializer, NoOpDatabaseInitializer>());
    }

    private sealed class NoOpDatabaseInitializer : IDatabaseInitializer
    {
        public Task InitializeAsync(bool applyMigrations, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
