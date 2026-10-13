using Microsoft.Extensions.Logging;

namespace Pulsar.Application.Nodes;

/// <summary>
/// Keeps refused enrollments that name no node (an unknown token, a malformed request: anyone can send them) from
/// flooding the audit log (docs/architecture/08, "Audit"): the first <see cref="PerMinute"/> of each minute, panel-wide,
/// get an entry, and the next entry says how many were left out. Each address is also held to the enrollment rate limit.
/// </summary>
public sealed partial class EnrollmentRefusalBudget(TimeProvider clock, ILogger<EnrollmentRefusalBudget> logger)
{
    public const int PerMinute = 20;

    private readonly Lock _lock = new();
    private long _minute;
    private int _recorded;
    private int _suppressed;

    /// <summary>Whether this refusal gets an entry, and how many were left out before it.</summary>
    public bool TryRecord(out int leftOut)
    {
        lock (_lock)
        {
            var minute = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
            if (minute != _minute)
            {
                _minute = minute;
                _recorded = 0;
            }

            if (_recorded < PerMinute)
            {
                _recorded++;
                leftOut = _suppressed;
                _suppressed = 0;
                return true;
            }

            if (_suppressed++ == 0)
            {
                LogFlood(logger, PerMinute);
            }

            leftOut = 0;
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "More than {Limit} enrollments naming no node refused this minute: the audit log keeps the first ones and counts the rest")]
    private static partial void LogFlood(ILogger logger, int limit);
}
