namespace Pulsar.Api.Auth;

/// <summary>
/// Keeps failed sign-ins from flooding the audit log (docs/architecture/08, "Audit"): the first
/// <c>Audit:FailedLoginsPerMinute</c> (60) of each minute get their own entry; the rest are counted, and the next entry
/// that is recorded says how many were left out. The rate limit (per address) and the lockout (per account) still
/// apply to every attempt; this only bounds what many addresses at once can write.
/// </summary>
public sealed partial class FailedLoginBudget(TimeProvider clock, IConfiguration configuration, ILogger<FailedLoginBudget> logger)
{
    public const int DefaultPerMinute = 60;

    private readonly int _perMinute = configuration.GetValue("Audit:FailedLoginsPerMinute", DefaultPerMinute);
    private readonly Lock _lock = new();
    private long _minute;
    private int _recorded;
    private int _suppressed;

    /// <summary>Whether this failure gets an entry, and how many were left out before it.</summary>
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

            if (_recorded < _perMinute)
            {
                _recorded++;
                leftOut = _suppressed;
                _suppressed = 0;
                return true;
            }

            if (_suppressed++ == 0)
            {
                LogFlood(logger, _perMinute);
            }

            leftOut = 0;
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "More than {Limit} failed sign-ins this minute: the audit log keeps the first ones and counts the rest")]
    private static partial void LogFlood(ILogger logger, int limit);
}
