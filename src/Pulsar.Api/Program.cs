using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Pulsar.Api;
using Pulsar.Api.Access;
using Pulsar.Api.Agents;
using Pulsar.Api.Appearance;
using Pulsar.Api.Auth;
using Pulsar.Api.Certificates;
using Pulsar.Api.Databases;
using Pulsar.Api.Nodes;
using Pulsar.Api.Notifications;
using Pulsar.Api.Realtime;
using Pulsar.Api.Security;
using Pulsar.Api.Servers;
using Pulsar.Api.Sftp;
using Pulsar.Api.Updates;
using Pulsar.Application;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Infrastructure;
using Pulsar.Infrastructure.Persistence;
using Serilog;

// Static logger only for messages outside the host (startup failures). The host builds its own logger from
// configuration and leaves this one alone (preserveStaticLogger), so several hosts can live in one process (tests).
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    // "Pulsar.Api migrate" applies pending migrations and exits (for Database:ApplyMigrationsOnStartup = false).
    var migrateOnly = args is ["migrate", ..];
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = migrateOnly ? args[1..] : args,
        // A Windows service starts in System32: settings, the SPA and the bundled games are next to the executable.
        ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
    });
    builder.Services.AddWindowsService(options => options.ServiceName = "PulsarPanel");

    // The console sink is always on, so startup failures are visible even when appsettings.json is missing;
    // configuration adds levels and extra sinks. Pulsar:LogsDirectory adds daily files (a service has no console).
    var logsDirectory = builder.Configuration["Pulsar:LogsDirectory"];
    builder.Services.AddSerilog(
        (services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Filter.ByExcluding(LogFilters.IsNoise)
                .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(logsDirectory))
            {
                configuration.WriteTo.File(Path.Combine(logsDirectory, "panel-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    formatProvider: CultureInfo.InvariantCulture);
            }
        },
        preserveStaticLogger: true);

    builder.Services.AddPulsarApplication();
    builder.Services.AddPulsarInfrastructure(builder.Configuration);
    builder.Services.AddPulsarApi(builder.Configuration);

    var app = builder.Build();

    await using (var scope = app.Services.CreateAsyncScope())
    {
        var applyMigrations = migrateOnly || scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ApplyMigrationsOnStartup;
        var status = await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(applyMigrations, app.Lifetime.ApplicationStopping);

        if (!migrateOnly && !status.HasUsers)
        {
            app.Services.GetRequiredService<InitialSetup>().IssueToken();
        }

        if (!migrateOnly)
        {
            await scope.ServiceProvider.GetRequiredService<IBundledTemplates>().RegisterBundledAsync(app.Lifetime.ApplicationStopping);
        }
    }

    if (migrateOnly)
    {
        Log.Information("Database is up to date");
        return 0;
    }

    // First: the agents' endpoint serves the agent protocol and nothing else.
    app.UseAgentsEndpoint();
    app.UseSecurityHeaders();
    app.UseTransportSecurity(app.Services.GetRequiredService<SecurityOptions>());
    // Through the host's logger: left alone, request logging writes to the static startup logger (console only),
    // skipping the sinks and levels from configuration.
    app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UsePanelHubOriginCheck();

    app.UseAuthentication();
    // Before authorization: requests with an API key count whether or not they are allowed, and before the key is
    // looked up: one over its budget never reaches the database.
    app.UseApiKeyRequestBudget();
    app.UseRateLimiter();
    app.UseAuthorization();

    // Liveness never touches dependencies; readiness includes the database.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains(HealthTags.Ready),
    }).AllowAnonymous().RequireRateLimiting(AuthEndpoints.AnonymousRateLimitPolicy);

    // The OpenAPI document is a development tool (the TypeScript client is generated from a committed copy).
    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    {
        app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
    }

    // Every error is ProblemDetails with a "code" (documented on all operations so the client knows the error shape).
    var api = app.MapGroup("/api/v1")
        .AddEndpointFilter<AntiforgeryValidationFilter>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);
    api.MapGroup("/auth").WithTags("Auth").MapAuthEndpoints();
    api.MapApiKeyEndpoints();
    api.MapSshKeyEndpoints();
    api.MapUserEndpoints();
    api.MapRoleEndpoints();
    api.MapAuditEndpoints();
    api.MapNodeEndpoints();
    api.MapPublicIpCertificateEndpoints();
    api.MapServerEndpoints();
    api.MapServerFileEndpoints();
    api.MapServerBackupEndpoints();
    api.MapServerAddonEndpoints();
    api.MapServerModEndpoints();
    api.MapWorkshopEndpoints();
    api.MapDatabaseEndpoints();
    api.MapServerScheduleEndpoints();
    api.MapGameUpdateEndpoints();
    api.MapNotificationEndpoints();
    api.MapSftpEndpoints();
    api.MapAppearanceEndpoints();
    api.MapUpdateEndpoints();
    api.MapPanelCertificateEndpoints();

    // Agent protocol: its own authentication, never the browser's (docs/architecture/03).
    app.MapAgentEndpoints();
    app.MapHub<AgentHub>(AgentProtocol.HubPath, options =>
    {
        options.Transports = HttpTransportType.WebSockets;
        options.CloseOnAuthenticationExpiration = true;
    }).WithMetadata(AgentEndpointMetadata.Instance);

    // Browsers: live changes, usage and consoles (docs/architecture/07, "Real time").
    app.MapHub<PanelHub>(PanelHub.Path, options =>
    {
        options.Transports = HttpTransportType.WebSockets;
        options.CloseOnAuthenticationExpiration = true;
    }).RequireSignedInUser();

    // The agent's setup, for machines added as nodes (Phase 18): one fixed file, anonymous (docs/architecture/08, T37).
    app.MapDownloadEndpoints();

    // API, hub, agent and download routes must never fall through to the SPA's index.html.
    app.MapNotFoundFallback("/api");
    app.MapNotFoundFallback("/hubs");
    app.MapNotFoundFallback("/agent");
    app.MapNotFoundFallback("/downloads");

    app.MapStaticAssets();
    app.MapFallbackToFile("index.html");

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is DatabaseStartupException or PanelStartupException)
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
