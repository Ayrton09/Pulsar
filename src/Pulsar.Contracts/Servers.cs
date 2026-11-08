namespace Pulsar.Contracts;

/// <summary>What a server process is doing, as observed by its agent (docs/architecture/05).</summary>
public enum RuntimeState
{
    Offline,
    Starting,
    Running,
    Stopping,
    Crashed,
}

/// <summary>Why a server changed state; the panel derives its desired state and alerts from it.</summary>
public enum StateChangeReason
{
    /// <summary>A start, stop, restart or kill from the panel.</summary>
    Requested,

    /// <summary>The readiness check passed.</summary>
    Ready,

    /// <summary>Still alive but the readiness check never passed: running, with an alert.</summary>
    ReadinessTimedOut,

    /// <summary>Exit code 0 without a stop request (someone typed <c>quit</c>): a stop the game asked for.</summary>
    ExitedByItself,

    /// <summary>Unexpected exit with a non-zero code.</summary>
    Crashed,

    /// <summary>Too many crashes in a short time: no more automatic restarts until someone starts it.</summary>
    CrashLoop,

    /// <summary>A graceful stop did not finish in time; the process tree was killed.</summary>
    StopTimedOut,

    /// <summary>Killed on request.</summary>
    Killed,

    /// <summary>Restarted by the crash policy.</summary>
    AutoRestart,

    /// <summary>The process could not be started at all (missing executable, account, OS error).</summary>
    StartFailed,

    /// <summary>The agent is stopping (service stop, update, reboot) and stops its servers.</summary>
    AgentStopping,

    /// <summary>Started by the agent itself after it started (auto-start, or running before an orderly agent stop).</summary>
    AgentStarted,

    /// <summary>
    /// Ended (with a non-zero code) after its memory reached the server's limit (<see cref="ServerLimits.MemoryBytes"/>):
    /// a crash whose cause is known. The crash policy applies as for <see cref="Crashed"/>.
    /// </summary>
    MemoryLimit,
}

/// <summary>
/// How a server runs, sent by the panel at provisioning (docs/architecture/03): the exact manifest text and the values
/// to render it with. The agent renders and validates the command line itself; it never receives one.
/// </summary>
/// <param name="Variables">Values of the manifest's variables, secret ones included (the agent keeps them encrypted).</param>
/// <param name="Ports">The node allocation given to each manifest port.</param>
/// <param name="Limits">Caps on the server's resources; null (or older panels) for none.</param>
/// <param name="Addons">Keys of the manifest's addons installed on the server (their lines are kept at every start).</param>
/// <param name="SteamBranch">
/// The Steam branch the game follows (Pulsar 1.0.14; <c>public</c>, <c>previous_build</c>), passed to SteamCMD as
/// <c>-beta</c> for the game's app whenever it installs, updates or checks it; null keeps the template's own. SteamCMD
/// stays on a branch it installed until told another, so going back is <c>public</c>, never null.
/// </param>
/// <param name="RconPassword">
/// The RCON password chosen for the server (Pulsar 1.0.29, <see cref="RconPasswords"/>), used by the game and by the
/// agent's own RCON; null for the one the agent makes (a new one at each provisioning). Only a stopped server's changes.
/// </param>
/// <param name="Database">
/// The panel's database given to the server for its game (Pulsar 1.0.37), whose manifest's <c>database</c> environment
/// the game then starts with; null for the game's own files. Older agents ignore it: the panel gives it only to agents
/// that announce <see cref="AgentCapabilities.GameDatabase"/>.
/// </param>
public sealed record ServerRuntimeSpec(
    string ShortId,
    string Manifest,
    IReadOnlyDictionary<string, string?> Variables,
    IReadOnlyList<string> ExtraArguments,
    bool AutoStart,
    bool RestartOnCrash,
    IReadOnlyDictionary<string, ServerPort>? Ports = null,
    ServerLimits? Limits = null,
    IReadOnlyList<string>? Addons = null,
    string? SteamBranch = null,
    string? RconPassword = null,
    GameDatabaseSpec? Database = null)
{
    // Variables, the RCON password and the database's are secrets: never print them.
    public override string ToString() => $"ServerRuntimeSpec {{ ShortId = {ShortId} }}";
}

/// <summary>Where a server's game reaches its database (Pulsar 1.0.37): what the manifest's <c>{{database.*}}</c> stand for.</summary>
/// <param name="Engine"><c>postgres</c> or <c>mysql</c>, as the manifest's <c>database.engine</c>.</param>
/// <param name="Host">The host's address for game servers.</param>
/// <param name="Name">The database, whose user (role) has the same name.</param>
public sealed record GameDatabaseSpec(string Engine, string Host, int Port, string Name, string Password)
{
    // The password is a secret: never print it.
    public override string ToString() => $"GameDatabaseSpec {{ {Engine} {Name}@{Host}:{Port} }}";
}

/// <summary>
/// Caps on a server's process tree, enforced by its Job (docs/architecture/06, "Resource limits"); null fields are not
/// capped. Applied to a running server at once.
/// </summary>
/// <param name="CpuPercent">Share of the whole machine, 1–100 (the unit of <see cref="ServerMetrics.CpuPercent"/>).</param>
/// <param name="MemoryBytes">Memory committed by all the server's processes together.</param>
public sealed record ServerLimits(int? CpuPercent, long? MemoryBytes)
{
    public const int MinCpuPercent = 1;
    public const int MaxCpuPercent = 100;
    public const long MinMemoryBytes = 128L * 1024 * 1024;

    /// <summary>Whether the values are within range (the agent checks again what the panel checked).</summary>
    public bool IsValid => (CpuPercent is null or (>= MinCpuPercent and <= MaxCpuPercent)) && (MemoryBytes is null or >= MinMemoryBytes);
}

/// <summary>An <c>ip:port</c> pair of the node (both protocols); <c>0.0.0.0</c> means every interface.</summary>
public sealed record ServerPort(string IpAddress, int Port);

/// <summary>A server's state at the time of an agent's Hello: the panel takes it as the truth (reconciliation).</summary>
public sealed record ServerSnapshot(Guid ServerId, RuntimeState State, long StateSequence, int? ExitCode, DateTimeOffset? StartedAt);

/// <param name="StateSequence">Increases with every change of the server while the agent runs; older events are dropped.</param>
/// <param name="Detail">Error code or short explanation (a failed start, a readiness timeout).</param>
public sealed record ServerStateChanged(
    Guid ServerId,
    RuntimeState State,
    RuntimeState Previous,
    DateTimeOffset At,
    long StateSequence,
    int? ExitCode,
    StateChangeReason Reason,
    string? Detail);

public enum OperationOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Progress of long work started by a command carrying an operation id.</summary>
public sealed record OperationProgress(Guid OperationId, string Stage, double? Percent, string? Message);

/// <summary>The end of an operation. Kept by the agent and sent again until the panel has received it.</summary>
/// <param name="Result">What a successful operation produced (a <see cref="BackupResult"/>); null for most operations and older agents.</param>
public sealed record OperationCompleted(Guid OperationId, Guid? ServerId, OperationOutcome Outcome, string? ErrorCode, string? Message,
    CommandResult? Result = null);

/// <summary>
/// What an update check found (<see cref="CheckGameUpdateCommand"/>). Builds are the game's own names for what is
/// installed: a Steam build id, a Minecraft version. Not sent again: the panel asks again later.
/// </summary>
/// <param name="Installed">What the server has; null when it cannot be told.</param>
/// <param name="Latest">What its publisher offers for the version or branch the server follows; null when unknown.</param>
/// <param name="Available">An update would bring something the server does not have; null when the check failed.</param>
/// <param name="ErrorCode">Why the check failed (<c>steamcmd.*</c>, <c>minecraft.*</c>); null when it did not.</param>
/// <param name="Branches">
/// The Steam branches of the game's app (Pulsar 1.0.14), as Steam lists them in the same answer; null for games not on
/// Steam, older agents, and checks that failed.
/// </param>
public sealed record GameUpdateCheck(Guid ServerId, string? Installed, string? Latest, bool? Available, string? ErrorCode, string? Message,
    DateTimeOffset At, IReadOnlyList<SteamBranch>? Branches = null)
{
    public const int MaxBuildLength = 64;
    public const int MaxMessageLength = 300;
}

/// <summary>A branch of a Steam app (Pulsar 1.0.14): its name, its current build and what its publisher says of it.</summary>
/// <param name="PasswordRequired">Steam installs it only with the branch's password.</param>
/// <param name="Updated">When its build last changed.</param>
public sealed record SteamBranch(string Name, string? Build, string? Description, bool PasswordRequired, DateTimeOffset? Updated)
{
    public const string Public = "public";

    /// <summary>Branches kept of one app: 7 Days to Die lists every version it ever shipped, about twenty.</summary>
    public const int MaxBranches = 64;

    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 128;

    /// <summary>A branch name SteamCMD takes after <c>-beta</c> as one argument: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, not first.</summary>
    public static bool IsName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength } && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}

/// <summary>
/// An RCON password chosen for a server (Pulsar 1.0.29): 12 to 64 characters, letters, digits and
/// <c>_.!@#$%^&amp;*=?~</c>. Nothing a game's command line or console reads as more than one word: no spaces, quotes,
/// <c>;</c>, <c>+</c> or <c>-</c> (a new command or switch), nor <c>{}()':</c> (Source's tokenizer ends a word there).
/// At least 12, since CS2's RCON has no ban for wrong passwords (docs/architecture/08, T18).
/// </summary>
public static partial class RconPasswords
{
    public const int MinLength = 12;
    public const int MaxLength = 64;

    public static bool IsValid(string? password) => password is not null && Pattern().IsMatch(password);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_.!@#$%^&*=?~]{12,64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
