using Serilog.Events;

namespace Pulsar.Api;

/// <summary>Framework log events that look like errors and are not.</summary>
internal static class LogFilters
{
    /// <summary>
    /// A browser that kept an antiforgery cookie across a key ring change: the framework logs an error with its stack and
    /// then issues a new cookie, which is all that needs to happen (docs/architecture/08).
    /// </summary>
    public static bool IsStaleAntiforgeryCookie(LogEvent logEvent) =>
        logEvent.MessageTemplate.Text == "An exception was thrown while deserializing the token."
        && logEvent.Properties.TryGetValue("SourceContext", out var source)
        && source is ScalarValue { Value: "Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery" };
}
