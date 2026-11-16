using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Auth;

/// <summary>
/// When a user last used the panel (Pulsar 1.0.45): a session that goes on for days, remembered on the device, signs in
/// once, so the time of the last sign-in alone said a user who comes every day had not come in weeks. A request in a
/// session moves the user's <c>LastLoginAt</c> too, at most once every <see cref="Precision"/> per user, without touching
/// the row's concurrency stamp (the sign-in steps' own updates of the row go on as they were).
/// </summary>
public sealed partial class UserActivity(TimeProvider clock, ILogger<UserActivity> logger)
{
    public static readonly TimeSpan Precision = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<UserId, DateTimeOffset> _written = new();

    public async Task SeenAsync(UserId user, PulsarDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var now = clock.GetUtcNow();
        if (_written.TryGetValue(user, out var last) && now - last < Precision)
        {
            return;
        }

        _written[user] = now;
        var before = now - Precision;
        try
        {
            await db.Users.Where(u => u.Id == user && (u.LastLoginAt == null || u.LastLoginAt < before))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, now), cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or OperationCanceledException
            or System.Data.Common.DbException)
        {
            // Only a time shown in the users' list: the request goes on, and the next one after Precision tries again.
            _written.TryRemove(new KeyValuePair<UserId, DateTimeOffset>(user, now));
            NotSaved(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The user's last activity was not saved")]
    private static partial void NotSaved(ILogger logger, Exception exception);
}
