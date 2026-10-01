using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Pulsar.Api;
using Pulsar.Infrastructure;
using Pulsar.Infrastructure.Persistence;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    // "Pulsar.Api migrate" applies pending migrations and exits (for Database:ApplyMigrationsOnStartup = false).
    var migrateOnly = args is ["migrate", ..];
    var builder = WebApplication.CreateBuilder(migrateOnly ? args[1..] : args);

    // The console sink is always on, so startup failures are visible even when appsettings.json is missing;
    // configuration adds levels and extra sinks.
    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

    builder.Services.AddProblemDetails();
    builder.Services.AddPulsarInfrastructure(builder.Configuration);

    var app = builder.Build();

    await using (var scope = app.Services.CreateAsyncScope())
    {
        var applyMigrations = migrateOnly || scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ApplyMigrationsOnStartup;
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(applyMigrations, app.Lifetime.ApplicationStopping);
    }

    if (migrateOnly)
    {
        Log.Information("Database is up to date");
        return 0;
    }

    app.UseSerilogRequestLogging();

    // Liveness never touches dependencies; readiness includes the database.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains(DependencyInjection.ReadyHealthTag),
    });

    // API, hub and agent routes must never fall through to the SPA's index.html.
    app.MapNotFoundFallback("/api");
    app.MapNotFoundFallback("/hubs");
    app.MapNotFoundFallback("/agent");

    app.MapStaticAssets();
    app.MapFallbackToFile("index.html");

    await app.RunAsync();
    return 0;
}
catch (DatabaseStartupException ex)
{
    // Expected configuration/environment problem: a clear message, no stack trace.
    Log.Fatal("Pulsar panel cannot start: {Reason}", ex.Message);
    return 1;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Pulsar panel terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
