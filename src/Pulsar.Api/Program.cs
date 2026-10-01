using System.Globalization;
using Pulsar.Api;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services));

    builder.Services.AddProblemDetails();
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.MapHealthChecks("/health/live");

    // API, hub and agent routes must never fall through to the SPA's index.html.
    app.MapNotFoundFallback("/api");
    app.MapNotFoundFallback("/hubs");
    app.MapNotFoundFallback("/agent");

    app.MapStaticAssets();
    app.MapFallbackToFile("index.html");

    await app.RunAsync();
    return 0;
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
