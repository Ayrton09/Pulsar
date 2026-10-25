using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>What the panel does when a server's game has an update.</summary>
public enum GameUpdatePolicy
{
    /// <summary>Never checked.</summary>
    Off,

    /// <summary>Checked and shown (and notified); updated only when someone asks.</summary>
    Notify,

    /// <summary>Checked and installed: the players are warned, the server stops, updates and starts again.</summary>
    Install,
}

/// <summary>
/// How a server follows its game's updates (Pulsar 1.0.9), as AMP and WindowsGSM do: its node asks the publisher (Steam
/// through SteamCMD, Mojang) every <see cref="CheckInterval"/>, and the panel shows, notifies or installs what is new.
/// One per server, made the first time it is needed; a server without one is checked and notified.
/// </summary>
public sealed class GameUpdateWatch
{
    public const int MaxWarningMinutes = 60;
    public const int MaxBuildLength = 64;
    public const int MaxErrorCodeLength = 64;
    public const int MaxMessageLength = 300;
    public const int MaxWarningMessageLength = 100;

    /// <summary>Stands for how long until the restart in a warning ("5 min").</summary>
    public const string TimePlaceholder = "{time}";

    public const string DefaultWarningMessage = "Restarting in {time} for a game update";

    /// <summary>How often a server's node is asked.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>A node that took a check and did not answer is asked again after this.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Updates of a server are notified at most this often: a node is not trusted with how often the channels hear from it
    /// (one could answer every check with a new build).
    /// </summary>
    public static readonly TimeSpan NotifiedAtMostEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// Automatic updates of a server start at most this often: what a check finds comes from files the server's account
    /// controls, which could report a new build at every check (that account can stop its own game anyway, but not make
    /// the panel restart it, download and notify over and over).
    /// </summary>
    public static readonly TimeSpan AutoUpdatedAtMostEvery = TimeSpan.FromHours(1);

    private GameUpdateWatch()
    {
        // For EF Core materialization.
    }

    private GameUpdateWatch(ServerId serverId, DateTimeOffset now)
    {
        ServerId = serverId;
        Policy = GameUpdatePolicy.Notify;
        NextCheckAt = now;
        UpdatedAt = now;
    }

    public ServerId ServerId { get; private set; }

    public GameUpdatePolicy Policy { get; private set; }

    /// <summary>An automatic update waits until nobody is playing (the game's query says so).</summary>
    public bool WhenEmpty { get; private set; }

    /// <summary>Minutes the players are warned before an automatic update stops the server; 0: no warning.</summary>
    public int WarningMinutes { get; private set; }

    /// <summary>What the players are told, with <see cref="TimePlaceholder"/>; null: <see cref="DefaultWarningMessage"/>.</summary>
    public string? WarningMessage { get; private set; }

    /// <summary>What the server has, as the last check found (a Steam build id, a Minecraft version); null when unknown.</summary>
    public string? Installed { get; private set; }

    /// <summary>What the publisher offers, as the last check found; null when unknown.</summary>
    public string? Latest { get; private set; }

    /// <summary>Whether an update would bring something new; null until a check answered.</summary>
    public bool? Available { get; private set; }

    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>Why the last check failed; null when it did not.</summary>
    public string? CheckErrorCode { get; private set; }

    public string? CheckError { get; private set; }

    /// <summary>When the node took the check it has not answered yet.</summary>
    public DateTimeOffset? CheckRequestedAt { get; private set; }

    public DateTimeOffset NextCheckAt { get; private set; }

    /// <summary>The latest build a notification was sent for (each new one is notified once).</summary>
    public string? NotifiedLatest { get; private set; }

    public DateTimeOffset? NotifiedAt { get; private set; }

    /// <summary>The latest build an automatic update was started for: one that failed is not tried again until a newer one.</summary>
    public string? AttemptedLatest { get; private set; }

    /// <summary>The automatic update running now.</summary>
    public ServerSequenceId? SequenceId { get; private set; }

    /// <summary>When the last automatic update started (one an hour at most).</summary>
    public DateTimeOffset? AutoUpdatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>An automatic update should start (when the server allows it): an update the panel did not try yet.</summary>
    public bool AutoUpdateDue => Policy == GameUpdatePolicy.Install && Available == true && SequenceId is null && Latest != AttemptedLatest;

    /// <summary>When the next automatic update may start (one an hour at most); null: now.</summary>
    public DateTimeOffset? AutoUpdateNotBefore => AutoUpdatedAt is { } last ? last + AutoUpdatedAtMostEvery : null;

    /// <summary>An automatic update is due and may start at <paramref name="now"/>.</summary>
    public bool AutoUpdateMayStart(DateTimeOffset now) => AutoUpdateDue && (AutoUpdateNotBefore is not { } notBefore || now >= notBefore);

    public static GameUpdateWatch Create(ServerId serverId, DateTimeOffset now) => new(serverId, now);

    public Result Change(GameUpdatePolicy policy, bool whenEmpty, int warningMinutes, string? warningMessage, DateTimeOffset now)
    {
        var message = string.IsNullOrWhiteSpace(warningMessage) ? null : warningMessage.Trim();
        if (!Enum.IsDefined(policy) || warningMinutes is < 0 or > MaxWarningMinutes
            || message is { Length: > MaxWarningMessageLength } || (message is not null && !DisplayNames.IsDisplayable(message)))
        {
            return ServerErrors.GameUpdatesInvalid;
        }

        if (policy != GameUpdatePolicy.Off && Policy == GameUpdatePolicy.Off)
        {
            NextCheckAt = now; // turned on: checked now
        }

        Policy = policy;
        WhenEmpty = whenEmpty;
        WarningMinutes = warningMinutes;
        WarningMessage = message == DefaultWarningMessage ? null : message;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Whether the node should be asked now.</summary>
    public bool CheckDue(DateTimeOffset now) =>
        Policy != GameUpdatePolicy.Off && NextCheckAt <= now && (CheckRequestedAt is not { } asked || now - asked >= AnswerTimeout);

    /// <summary>The node took a check.</summary>
    public void Asked(DateTimeOffset now)
    {
        CheckRequestedAt = now;
        NextCheckAt = now + CheckInterval;
        UpdatedAt = now;
    }

    /// <summary>The node did not take the check after all (busy, away): asked again at <paramref name="retryAt"/>.</summary>
    public void NotAsked(DateTimeOffset retryAt, DateTimeOffset now)
    {
        CheckRequestedAt = null;
        NextCheckAt = retryAt;
        UpdatedAt = now;
    }

    /// <summary>Asked again at the next pass (after an update, or when a user asks).</summary>
    public void CheckSoon(DateTimeOffset now)
    {
        NextCheckAt = now;
        CheckRequestedAt = null;
        UpdatedAt = now;
    }

    /// <summary>Whether the node was asked and has not answered yet: an answer nobody asked for is not taken.</summary>
    public bool AwaitsAnswer => CheckRequestedAt is not null;

    /// <summary>
    /// What the node found. True when it is an update nobody was notified of yet, and the last notification is old enough
    /// (and notes that it now is notified).
    /// </summary>
    public bool Checked(string? installed, string? latest, bool? available, string? errorCode, string? error, DateTimeOffset now)
    {
        CheckedAt = now;
        CheckRequestedAt = null;
        UpdatedAt = now;
        if (errorCode is not null)
        {
            // What an earlier check found still stands.
            CheckErrorCode = Cut(errorCode, MaxErrorCodeLength);
            CheckError = error is null ? null : Cut(error, MaxMessageLength);
            return false;
        }

        (CheckErrorCode, CheckError) = (null, null);
        Installed = installed is null ? null : Cut(installed, MaxBuildLength);
        Latest = latest is null ? null : Cut(latest, MaxBuildLength);
        Available = available;
        if (Available != true || Latest == NotifiedLatest || (NotifiedAt is { } told && now - told < NotifiedAtMostEvery))
        {
            return false;
        }

        NotifiedLatest = Latest;
        NotifiedAt = now;
        return true;
    }

    /// <summary>Not asked before <paramref name="at"/> (the node is away, or the server busy).</summary>
    public void Postpone(DateTimeOffset at)
    {
        NextCheckAt = at;
    }

    /// <summary>The latest build is not installed by itself (a user cancelled it before it began); a newer one will be.</summary>
    public void Skip(DateTimeOffset now)
    {
        AttemptedLatest = Latest;
        UpdatedAt = now;
    }

    /// <param name="byItself">An automatic update (counted in the hourly limit); false: a user asked for it.</param>
    public void AutoUpdateStarted(ServerSequenceId sequence, DateTimeOffset now, bool byItself = true)
    {
        SequenceId = sequence;
        AttemptedLatest = Latest;
        if (byItself)
        {
            AutoUpdatedAt = now;
        }

        UpdatedAt = now;
    }

    /// <summary>The automatic update ended (or a user started one): what is installed now is checked.</summary>
    public void AutoUpdateEnded(DateTimeOffset now)
    {
        SequenceId = null;
        CheckSoon(now);
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}
