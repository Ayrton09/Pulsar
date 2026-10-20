using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Auth;
using Pulsar.Api.Sftp;
using Pulsar.Application.Servers;
using Pulsar.Application.Updates;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>The panel against its own fresh PostgreSQL database (created lazily, only when a test uses it).</summary>
public class PulsarApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminUserName = "admin";
    public const string AdminPassword = "correct horse battery staple";

    private readonly SemaphoreSlim _setupLock = new(1, 1);
    private readonly Dictionary<string, Task> _once = [];
    private TestDatabase? _database;
    private bool _adminCreated;

    public TestDatabase Database => _database ?? throw new InvalidOperationException("Database tests are disabled.");

    /// <summary>Data Protection key ring of this panel instance (never the machine-wide default).</summary>
    public string KeysDirectory { get; } = TestKeys.NewDirectory();

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
        TestKeys.Delete(KeysDirectory);
        _setupLock.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Completes the initial setup once per database and returns a client signed in as that administrator.</summary>
    public async Task<PanelClient> CreateAdminClientAsync()
    {
        await _setupLock.WaitAsync();
        try
        {
            if (!_adminCreated)
            {
                using var setupClient = await PanelClient.CreateAsync(this);
                var token = Services.GetRequiredService<InitialSetup>().IssueToken();
                using var response = await setupClient.PostAsync("/api/v1/auth/setup",
                    new SetupRequest(token, AdminUserName, AdminPassword, "Administrator", null));
                response.EnsureSuccessStatusCode();
                _adminCreated = true;
            }
        }
        finally
        {
            _setupLock.Release();
        }

        var client = await PanelClient.CreateAsync(this);
        using var login = await client.LoginAsync(AdminUserName, AdminPassword);
        login.EnsureSuccessStatusCode();
        await client.GetSessionAsync(); // fresh antiforgery token for the signed-in user
        return client;
    }

    /// <summary>Runs <paramref name="action"/> once per database (e.g. creating users shared by a theory's rows).</summary>
    public Task OnceAsync(string key, Func<Task> action)
    {
        lock (_once)
        {
            if (!_once.TryGetValue(key, out var task))
            {
                task = action();
                _once.Add(key, task);
            }

            return task;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Pulsar", _database?.ConnectionString ?? "Host=database-tests-disabled");
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "1000");
        builder.UseSetting("RateLimiting:Auth:EnrollPermitLimit", "1000"); // a test class enrolls an agent per test
        builder.UseSetting("DataProtection:KeysPath", KeysDirectory);
        builder.UseSetting("Pulsar:EnableTestGame", "true");

        // The SFTP server listens only in the tests that drive it (SftpApiFactory), on the loopback; GitHub is never asked
        // for releases (PanelUpdateTests gives its own).
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<SftpOptions>(options => options.Listen = false);
            services.AddScoped<IUpdateSource, OfflineUpdateSource>();
        });
    }
}

/// <summary>The panel without a database, for tests of the HTTP pipeline only.</summary>
public sealed class NoDatabaseApiFactory : WebApplicationFactory<Program>
{
    private readonly string _keys = TestKeys.NewDirectory();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        TestKeys.Delete(_keys);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Pulsar", "Host=no-database-in-these-tests");
        builder.UseSetting("DataProtection:KeysPath", _keys);
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<SftpOptions>(options => options.Listen = false);
            services.AddScoped<IUpdateSource, OfflineUpdateSource>();
            services.AddScoped<IDatabaseInitializer, NoOpDatabaseInitializer>();
            services.AddScoped<IBundledTemplates, NoBundledTemplates>();
        });
    }

    private sealed class NoBundledTemplates : IBundledTemplates
    {
        public Task RegisterBundledAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpDatabaseInitializer : IDatabaseInitializer
    {
        public Task<DatabaseStatus> InitializeAsync(bool applyMigrations, CancellationToken cancellationToken) =>
            Task.FromResult(new DatabaseStatus(HasUsers: true));
    }
}
