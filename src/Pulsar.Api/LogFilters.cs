using Serilog.Events;

namespace Pulsar.Api;

/// <summary>Framework log events that look like errors and are not.</summary>
public static class LogFilters
{
    /// <summary>
    /// A browser that kept an antiforgery cookie across a key ring change: the framework logs an error with its stack and
    /// then issues a new cookie, which is all that needs to happen (docs/architecture/08).
    /// </summary>
    public static bool IsStaleAntiforgeryCookie(LogEvent logEvent) =>
        logEvent.MessageTemplate.Text == "An exception was thrown while deserializing the token."
        && SourceIs(logEvent, "Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery");

    /// <summary>
    /// A hub method cancelled because its caller left (hub methods only cancel through the connection's own token): an
    /// agent restarting or losing its network in the middle of its Hello. SignalR logs it as a failed invocation.
    /// </summary>
    public static bool IsAbandonedHubInvocation(LogEvent logEvent) =>
        logEvent.Exception is OperationCanceledException
        && SourceIs(logEvent, "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher");

    public static bool IsNoise(LogEvent logEvent) => IsStaleAntiforgeryCookie(logEvent) || IsAbandonedHubInvocation(logEvent);

    private static bool SourceIs(LogEvent logEvent, string source) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string context } && context == source;
}
