using Serilog.Events;
using Serilog.Parsing;

namespace Pulsar.Api.Tests;

public sealed class LogFiltersTests
{
    private static readonly MessageTemplateParser s_parser = new();

    [Fact]
    public void AHubInvocationAbandonedByItsCallerIsNoise()
    {
        var abandoned = Event("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", "Failed to invoke hub method '{HubMethod}'.",
            new TaskCanceledException());
        var failed = Event("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", "Failed to invoke hub method '{HubMethod}'.",
            new InvalidOperationException("a real failure"));
        var elsewhere = Event("Pulsar.Application.Servers.ServerManagement", "Something was cancelled", new OperationCanceledException());

        Assert.True(LogFilters.IsNoise(abandoned));
        Assert.False(LogFilters.IsNoise(failed));
        Assert.False(LogFilters.IsNoise(elsewhere));
    }

    private static LogEvent Event(string source, string template, Exception exception) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Error, exception, s_parser.Parse(template),
            [new LogEventProperty("SourceContext", new ScalarValue(source))]);
}
