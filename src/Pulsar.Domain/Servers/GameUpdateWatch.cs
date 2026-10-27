using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>What the panel does when a server's game has an update (or its template a newer version).</summary>
public enum GameUpdatePolicy
{
    /// <summary>Never checked.</summary>
    Off,

    /// <summary>Checked and shown (and notified); updated only when someone asks.</summary>
    Notify,

    /// <summary>Checked and installed: the players are warned, the server stops, updates and starts again.</summary>
    Install,
}

/// <summary>A branch of the server's Steam app, as its node's last check found it (Pulsar 1.0.14).</summary>
/// <param name="PasswordRequired">Steam installs it only with the branch's password, which Pulsar does not ask for.</param>
public sealed record GameBranch(string Name, string? Build, string? Description, bool PasswordRequired, DateTimeOffset? Updated)
{
    public const string Public = "public";
    public const int MaxBranches = 64;
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 128;

    /// <summary>A name SteamCMD takes after <c>-beta</c> as one argument: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, not first.</summary>
    public static bool IsName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength } && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}

/// <summary>
/// How a server follows its game's updates (Pulsar 1.0.9): its node asks the publisher (Steam through SteamCMD, Mojang)
/// every <see cref="CheckInterval"/>, and the panel shows, notifies or installs what is new. Since 1.0.10 it follows the
/// newer versions of its game's template too (<see cref="TemplatePolicy"/>; the panel knows them without asking the
/// node): shown and notified, or applied by themselves the same way, with the same warnings. One per server, made the
/// first time it is needed; a server without one is checked and notified.
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
        TemplatePolicy = GameUpdatePolicy.Notify;
        NextCheckAt = now;
        UpdatedAt = now;
    }

    public ServerId ServerId { get; private set; }

    public GameUpdatePolicy Policy { get; private set; }

    /// <summary>What a newer version of the server's game template does (1.0.10): nothing, notified, or applied by itself.</summary>
    public GameUpdatePolicy TemplatePolicy { get; private set; }

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

    /// <summary>The branches of the server's Steam app, as the last check that listed them found (Pulsar 1.0.14); empty until then.</summary>
    public IReadOnlyList<GameBranch> Branches { get; private set; } = [];

    /// <summary>A change of Steam branch runs (1.0.14): one that does not end well puts back <see cref="BranchBefore"/>.</summary>
    public bool BranchChanging { get; private set; }

    /// <summary>The branch the server followed before the change that runs (null: its template's own).</summary>
    public string? BranchBefore { get; private set; }

    /// <summary>When the node took the check it has not answered yet.</summary>
    public DateTimeOffset? CheckRequestedAt { get; private set; }

    public DateTimeOffset NextCheckAt { get; private set; }

    /// <summary>The latest build a notification was sent for (each new one is notified once).</summary>
    public string? NotifiedLatest { get; private set; }

    public DateTimeOffset? NotifiedAt { get; private set; }

    /// <summary>The latest build an automatic update was started for: one that failed is not tried again until a newer one.</summary>
    public string? AttemptedLatest { get; private set; }

    /// <summary>The template version a notification was sent for (each newer one is notified once).</summary>
    public string? NotifiedTemplate { get; private set; }

    /// <summary>The template version an automatic move was started for: one that failed is not tried again until a newer one.</summary>
    public string? AttemptedTemplate { get; private set; }

    /// <summary>The automatic update running now: of the game, or to a newer template (its sequence's origin says which).</summary>
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

    /// <summary>A move to <paramref name="newer"/>, the newest version of the server's template, should start (when the server allows it).</summary>
    public bool TemplateUpgradeDue(string newer) =>
        TemplatePolicy == GameUpdatePolicy.Install && SequenceId is null && !string.Equals(newer, AttemptedTemplate, StringComparison.Ordinal);

    /// <summary>A move to <paramref name="newer"/> is due and may start at <paramref name="now"/> (one automatic update an hour, of either kind).</summary>
    public bool TemplateUpgradeMayStart(string newer, DateTimeOffset now) =>
        TemplateUpgradeDue(newer) && (AutoUpdateNotBefore is not { } notBefore || now >= notBefore);

    public static GameUpdateWatch Create(ServerId serverId, DateTimeOffset now) => new(serverId, now);

    public Result Change(GameUpdatePolicy policy, bool whenEmpty, int warningMinutes, string? warningMessage, DateTimeOffset now) =>
        Change(policy, TemplatePolicy, whenEmpty, warningMinutes, warningMessage, now);

    public Result Change(GameUpdatePolicy policy, GameUpdatePolicy templatePolicy, bool whenEmpty, int warningMinutes, string? warningMessage,
        DateTimeOffset now)
    {
        var message = string.IsNullOrWhiteSpace(warningMessage) ? null : warningMessage.Trim();
        if (!Enum.IsDefined(policy) || !Enum.IsDefined(templatePolicy) || warningMinutes is < 0 or > MaxWarningMinutes
            || message is { Length: > MaxWarningMessageLength } || (message is not null && !DisplayNames.IsDisplayable(message)))
        {
            return ServerErrors.GameUpdatesInvalid;
        }

        if (policy != GameUpdatePolicy.Off && Policy == GameUpdatePolicy.Off)
        {
            NextCheckAt = now; // turned on: checked now
        }

        Policy = policy;
        TemplatePolicy = templatePolicy;
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

    /// <summary>
    /// An install or an update changed the server's files: whether an update is available is unknown until the next check,
    /// which comes now (an automatic update would otherwise start again for what was just installed).
    /// </summary>
    public void FilesChanged(DateTimeOffset now)
    {
        Available = null;
        CheckSoon(now);
    }

    /// <summary>Whether the node was asked and has not answered yet: an answer nobody asked for is not taken.</summary>
    public bool AwaitsAnswer => CheckRequestedAt is not null;

    /// <summary>
    /// What the node found. True when it is an update nobody was notified of yet, and the last notification is old enough
    /// (and notes that it now is notified).
    /// </summary>
    public bool Checked(string? installed, string? latest, bool? available, string? errorCode, string? error, DateTimeOffset now,
        IReadOnlyList<GameBranch>? branches = null)
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
        if (branches is not null)
        {
            // What a node says is kept within bounds: its server's account controls the SteamCMD that printed it.
            Branches = [.. branches.Where(b => GameBranch.IsName(b.Name)).DistinctBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .Take(GameBranch.MaxBranches)
                .Select(b => b with
                {
                    Build = b.Build is null ? null : Cut(b.Build, MaxBuildLength),
                    Description = b.Description is null ? null : Cut(b.Description, GameBranch.MaxDescriptionLength),
                })];
        }

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

    /// <summary>
    /// Whether <paramref name="newer"/>, a newer version of the server's template, should be notified now (and notes that
    /// it is): each version once.
    /// </summary>
    public bool TemplateNoticed(string newer, DateTimeOffset now)
    {
        if (TemplatePolicy == GameUpdatePolicy.Off || string.Equals(newer, NotifiedTemplate, StringComparison.Ordinal))
        {
            return false;
        }

        NotifiedTemplate = Cut(newer, MaxBuildLength);
        UpdatedAt = now;
        return true;
    }

    /// <summary>The move to <paramref name="version"/> is not made by itself (a user cancelled it before it began); a newer one will be.</summary>
    public void SkipTemplate(string version, DateTimeOffset now)
    {
        AttemptedTemplate = Cut(version, MaxBuildLength);
        UpdatedAt = now;
    }

    /// <param name="byItself">An automatic move (counted in the hourly limit); false: a user asked for it.</param>
    public void TemplateUpgradeStarted(ServerSequenceId sequence, string version, DateTimeOffset now, bool byItself = true)
    {
        SequenceId = sequence;
        AttemptedTemplate = Cut(version, MaxBuildLength);
        if (byItself)
        {
            AutoUpdatedAt = now;
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// A change of the game's Steam branch started (Pulsar 1.0.14), from <paramref name="before"/>: followed as an update a
    /// user asked for, and undone when it does not end well (<see cref="BranchChangeEnded"/>).
    /// </summary>
    public void BranchChangeStarted(ServerSequenceId sequence, string? before, DateTimeOffset now)
    {
        SequenceId = sequence;
        BranchChanging = true;
        BranchBefore = before;
        UpdatedAt = now;
    }

    /// <summary>
    /// The change of branch ended. When it did not succeed (its backup failed, it was cancelled) the server goes back to the
    /// branch it followed, which this returns with true: otherwise the next check would find the new branch's build and an
    /// automatic update would install it with no backup.
    /// </summary>
    public (bool Undo, string? Branch) BranchChangeEnded(bool succeeded, DateTimeOffset now)
    {
        var undo = BranchChanging && !succeeded;
        var before = BranchBefore;
        (BranchChanging, BranchBefore) = (false, null);
        AutoUpdateEnded(now);
        return (undo, before);
    }

    /// <summary>The automatic update ended (or a user started one): what is installed now is checked.</summary>
    public void AutoUpdateEnded(DateTimeOffset now)
    {
        SequenceId = null;
        CheckSoon(now);
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}
