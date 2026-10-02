using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Infrastructure;
using Serilog.Core;
using Serilog.Events;

namespace Pulsar.Api.Tests.Database;

public sealed class ReadinessTests
{
    [DatabaseFact]
    public async Task ACallerThatLeavesDuringTheDatabaseCheckIsNotLoggedAsAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new RecordingApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();
        using var caller = new CancellationTokenSource();
        // The caller gives up while the check runs (a monitoring probe's timeout, as in the agent install check).
        factory.Logs.CheckStarted = caller.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync(new Uri("/health/ready", UriKind.Relative), caller.Token));

        var request = await factory.Logs.WaitForRequestAsync("/health/ready", ct);
        Assert.Equal(LogEventLevel.Information, request.Level);
        Assert.Equal("499", request.Properties["StatusCode"].ToString());
        Assert.Empty(factory.Logs.Errors);
    }

    [Fact]
    public async Task AnUnreachableDatabaseMakesThePanelUnready()
    {
        await using var services = DatabaseInitializerTests.Build("Host=127.0.0.1;Port=1;Database=pulsar;Username=pulsar;Timeout=3");

        var report = await services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(check => check.Tags.Contains(HealthTags.Ready), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["database"].Status);
    }

    private sealed class RecordingApiFactory : PulsarApiFactory
    {
        public RecordingSink Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // The health check service announces each check at debug level, right before running it.
            builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.Extensions.Diagnostics.HealthChecks", "Debug");
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(Logs));
        }
    }

    private sealed class RecordingSink : ILogEventSink
    {
        private readonly List<LogEvent> _requests = [];
        private readonly List<string> _all = [];

        public Action? CheckStarted { get; set; }

        public List<string> Errors { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.MessageTemplate.Text == "Running health check {HealthCheckName}")
            {
                CheckStarted?.Invoke();
            }

            lock (_requests)
            {
                _all.Add(logEvent.MessageTemplate.Text);
                if (logEvent.Level >= LogEventLevel.Error)
                {
                    Errors.Add(logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture));
                }

                if (logEvent.Properties.ContainsKey("RequestPath") && logEvent.Properties.ContainsKey("Elapsed"))
                {
                    _requests.Add(logEvent);
                }
            }
        }

        public async Task<LogEvent> WaitForRequestAsync(string path, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_requests)
                {
                    var request = _requests.Find(e => e.Properties["RequestPath"].ToString() == $"\"{path}\"");
                    if (request is not null)
                    {
                        return request;
                    }
                }

                await Task.Delay(50, cancellationToken);
            }

            lock (_requests)
            {
                throw new TimeoutException($"The panel never logged the request to {path}. Logged: {string.Join(" | ", _all)}");
            }
        }
    }
}
