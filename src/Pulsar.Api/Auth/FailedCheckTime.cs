namespace Pulsar.Api.Auth;

/// <summary>
/// The least time a failed password check takes, in the browser and over SFTP alike, whatever failed: an unknown name, a
/// locked or disabled account and a wrong password cost different database work, which the answer's time would otherwise
/// tell (<see cref="AuthRateLimitOptions.FailedPasswordCheckTime"/>). Counted once it is the account's turn
/// (<see cref="SignInLocks"/>), so waiting for other attempts on the same name is not part of it.
/// </summary>
internal static class FailedCheckTime
{
    /// <summary>More than either path needs: a password hash and a few queries.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromMilliseconds(400);

    /// <summary>Waits until <paramref name="time"/> has passed since <paramref name="from"/>; a cancellation only ends the wait.</summary>
    public static async Task WaitOutAsync(TimeProvider clock, long from, TimeSpan time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var rest = time - clock.GetElapsedTime(from);
        if (rest <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(rest, clock, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The client went away: nobody waits for the answer.
        }
    }
}
