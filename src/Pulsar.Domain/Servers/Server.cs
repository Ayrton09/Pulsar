using System.Security.Cryptography;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

public enum InstallState
{
    NotInstalled,
    Installing,
    Installed,
    Failed,
}

/// <summary>Last state the agent observed (it owns this dimension; docs/architecture/05).</summary>
public enum ServerRuntimeState
{
    Offline,
    Starting,
    Running,
    Stopping,
    Crashed,
}

/// <summary>What the panel (a user, later the scheduler) wants.</summary>
public enum DesiredState
{
    Stopped,
    Running,
}

/// <summary>Why the server last changed state, as the agent reported it (mirrors the protocol's reasons).</summary>
public enum ServerStateReason
{
    Requested,
    Ready,
    ReadinessTimedOut,
    ExitedByItself,
    Crashed,
    CrashLoop,
    StopTimedOut,
    Killed,
    AutoRestart,
    StartFailed,
    AgentStopping,
    AgentStarted,

    /// <summary>Crashed after its memory reached the server's limit.</summary>
    MemoryLimit,

    /// <summary>Taken from an agent's Hello (reconciliation), not from a change it reported.</summary>
    Reconciled,
}

/// <summary>
/// A game server (docs/architecture/02 and 05). Three independent dimensions: the install state and the desired state
/// belong to the panel, the runtime state is what its agent observed. At most one operation runs on it at a time.
/// </summary>
public sealed class Server
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 512;
    public const int ShortIdLength = 8;
    public const int MaxExtraArguments = 32;
    public const int MaxExtraArgumentLength = 512;
    public const int MaxBackupLimit = 1000;

    /// <summary>The backups a new server keeps (a backup every 15 minutes would otherwise fill its node's drive).</summary>
    public const int DefaultBackupLimit = 7;
    public const int MinMemoryLimitMb = 128;
    public const int MaxMemoryLimitMb = 1024 * 1024; // 1 TiB
    public const int DefaultDatabaseLimit = 2;
    public const int MaxDatabaseLimit = 50;

    private const string ShortIdAlphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private readonly List<ServerVariable> _variables = [];

    private Server()
    {
        // For EF Core materialization.
        ShortId = null!;
        Name = null!;
    }

    private Server(ServerId id, string shortId, NodeId nodeId, TemplateId templateId, string name, string? description, DateTimeOffset now)
    {
        Id = id;
        ShortId = shortId;
        NodeId = nodeId;
        TemplateId = templateId;
        Name = name;
        Description = description;
        InstallState = InstallState.NotInstalled;
        RuntimeState = ServerRuntimeState.Offline;
        DesiredState = DesiredState.Stopped;
        RestartOnCrash = true;
        BackupLimit = DefaultBackupLimit;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public ServerId Id { get; private set; }

    /// <summary>Random lowercase base32: names the server's folder and Windows account on its node.</summary>
    public string ShortId { get; private set; }

    public NodeId NodeId { get; private set; }

    public TemplateId TemplateId { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public InstallState InstallState { get; private set; }

    /// <summary>
    /// The game's version its last install or update reported (Pulsar 1.0.9: Minecraft's, which "latest" stands for);
    /// null when it did not say.
    /// </summary>
    public string? GameVersion { get; private set; }

    public ServerRuntimeState RuntimeState { get; private set; }

    public DesiredState DesiredState { get; private set; }

    public ServerStateReason? LastStateReason { get; private set; }

    /// <summary>The agent's sequence of the last state taken; older events are ignored.</summary>
    public long StateSequence { get; private set; }

    public DateTimeOffset? StateObservedAt { get; private set; }

    public OperationId? ActiveOperationId { get; private set; }

    /// <summary>What the active operation is: a backup leaves the server free to start and stop.</summary>
    public OperationType? ActiveOperationType { get; private set; }

    /// <summary>How many backups to keep (locked ones aside); null keeps them all.</summary>
    public int? BackupLimit { get; private set; }

    /// <summary>Where new backups are copied besides the node (Phase 10); null: on the node only.</summary>
    public BackupStorageId? BackupStorageId { get; private set; }

    /// <summary>Cap on the server's share of its node's CPU (1–100 %); null: none.</summary>
    public int? CpuLimitPercent { get; private set; }

    /// <summary>Cap on the memory the server's processes commit, in MiB; null: none.</summary>
    public int? MemoryLimitMb { get; private set; }

    /// <summary>The Steam account SteamCMD signs in with for this server (Phase 13); null: anonymous.</summary>
    public SteamAccountId? SteamAccountId { get; private set; }

    /// <summary>How many MySQL/MariaDB databases the server may have (Phase 13); 0: none.</summary>
    public int DatabaseLimit { get; private set; } = DefaultDatabaseLimit;

    public bool AutoStart { get; private set; }

    public bool RestartOnCrash { get; private set; }

    /// <summary>Arguments users added after the manifest's (only when the manifest allows them), one argv entry each.</summary>
    public IReadOnlyList<string> ExtraArguments { get; private set; } = [];

    /// <summary>Values of the template's variables (missing ones use the manifest default).</summary>
    public IReadOnlyCollection<ServerVariable> Variables => _variables;

    public int? LastExitCode { get; private set; }

    public DateTimeOffset? LastStartedAt { get; private set; }

    public DateTimeOffset? LastStoppedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public const int MaxGameVersionLength = 64;

    /// <summary>An install or update reported the game's version (one of letters, digits and <c>. - _ +</c>; anything else is dropped).</summary>
    public void GameInstalled(string? version, DateTimeOffset now)
    {
        GameVersion = version is { Length: > 0 and <= MaxGameVersionLength } && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')
            ? version
            : null;
        UpdatedAt = now;
    }

    public static string NewShortId() => RandomNumberGenerator.GetString(ShortIdAlphabet, ShortIdLength);

    /// <summary>Whether <paramref name="value"/> has the shape of a short id (it may name no server).</summary>
    public static bool IsShortId(string? value) =>
        value is { Length: ShortIdLength } && value.All(c => ShortIdAlphabet.Contains(c, StringComparison.Ordinal));

    public static Result<Server> Create(NodeId nodeId, TemplateId templateId, string name, string? description, string shortId, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || !DisplayNames.IsDisplayable(trimmed))
        {
            return ServerErrors.NameInvalid;
        }

        var cleanDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (cleanDescription?.Length > MaxDescriptionLength)
        {
            return ServerErrors.DescriptionTooLong;
        }

        if (!IsShortId(shortId))
        {
            throw new ArgumentException("Invalid short id.", nameof(shortId));
        }

        return new Server(ServerId.New(), shortId, nodeId, templateId, trimmed, cleanDescription, now);
    }

    /// <summary>Takes the server for <paramref name="operation"/>; only one operation runs on a server at a time.</summary>
    public Result BeginOperation(Operation operation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (ActiveOperationId is not null)
        {
            return ServerErrors.Busy;
        }

        if (operation.Type is OperationType.ServerUpdate or OperationType.BackupCreate or OperationType.BackupRestore
                or OperationType.AddonInstall or OperationType.AddonRemove
            && InstallState != InstallState.Installed)
        {
            return ServerErrors.NotInstalled;
        }

        // Provisioning rewrites the server's folder, account and spec: like the others, never under a running game.
        if (operation.Type is OperationType.ServerProvision or OperationType.ServerInstall or OperationType.ServerUpdate or OperationType.BackupRestore
                or OperationType.AddonInstall or OperationType.AddonRemove
            && RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        ActiveOperationId = operation.Id;
        ActiveOperationType = operation.Type;
        if (operation.Type is OperationType.ServerProvision or OperationType.ServerInstall)
        {
            InstallState = InstallState.Installing;
        }

        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Releases the server from <paramref name="operation"/> and applies its outcome.</summary>
    public void OperationCompleted(Operation operation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (ActiveOperationId != operation.Id)
        {
            return;
        }

        ActiveOperationId = null;
        ActiveOperationType = null;
        var succeeded = operation.Status == OperationStatus.Succeeded;
        switch (operation.Type)
        {
            case OperationType.ServerInstall when operation.Status == OperationStatus.Cancelled:
                // Stopped on purpose: nothing failed, the game is just not (completely) installed.
                InstallState = InstallState.NotInstalled;
                break;
            case OperationType.ServerProvision when !succeeded:
            case OperationType.ServerInstall when !succeeded:
                InstallState = InstallState.Failed;
                break;
            case OperationType.ServerInstall:
                InstallState = InstallState.Installed;
                break;
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// An install that will not run after all (the one that follows provisioning could not begin, or was lost with a
    /// restart of the panel): the server says it failed rather than installing for ever. False when it is not
    /// installing, or an install or provisioning is still running.
    /// </summary>
    public bool InstallAbandoned(DateTimeOffset now)
    {
        if (InstallState != InstallState.Installing || ActiveOperationType is OperationType.ServerProvision or OperationType.ServerInstall)
        {
            return false;
        }

        InstallState = InstallState.Failed;
        UpdatedAt = now;
        return true;
    }

    public Result RequestStart(DateTimeOffset now)
    {
        if (InstallState != InstallState.Installed)
        {
            return ServerErrors.NotInstalled;
        }

        // A backup reads the files while the game runs (doc 05); every other operation keeps the server stopped.
        if (ActiveOperationId is not null && ActiveOperationType != OperationType.BackupCreate)
        {
            return ServerErrors.Busy;
        }

        DesiredState = DesiredState.Running;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result RequestRestart(DateTimeOffset now) => RequestStart(now);

    /// <summary>
    /// A start its node already took: the game is wanted running whatever the server looks like now. Unlike
    /// <see cref="RequestStart"/> it checks nothing, for when the check has been made and the row changed since.
    /// </summary>
    public void WantsRunning(DateTimeOffset now)
    {
        DesiredState = DesiredState.Running;
        UpdatedAt = now;
    }

    /// <summary>
    /// Moves the server to a newer version of its game's template (docs/architecture/02, "Template upgrades"), while
    /// it is stopped and nothing else runs on it. Values of variables the new version no longer has are dropped; the
    /// caller checks the rest against the new manifest and installs the server again with it.
    /// </summary>
    public Result ChangeTemplate(GameTemplate current, GameTemplate next, IReadOnlyCollection<string> variables, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(variables);
        if (current.Id != TemplateId || next.Key != current.Key || GameTemplate.CompareVersions(next.Version, current.Version) <= 0)
        {
            return ServerErrors.TemplateNotNewer;
        }

        if (ActiveOperationId is not null)
        {
            return ServerErrors.Busy;
        }

        if (RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        _variables.RemoveAll(v => !variables.Contains(v.Key));
        TemplateId = next.Id;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Sets a plain variable value (null removes it, so the manifest default applies).</summary>
    public void SetVariable(string key, string? value, UserId? by, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var existing = _variables.FirstOrDefault(v => v.Key == key);
        if (value is null)
        {
            if (existing is not null)
            {
                _variables.Remove(existing);
            }
        }
        else if (existing is null)
        {
            _variables.Add(ServerVariable.Plain(Id, key, value, by, now));
        }
        else
        {
            existing.SetPlain(value, by, now);
        }

        UpdatedAt = now;
    }

    /// <summary>Sets a secret variable's encrypted value (null removes it).</summary>
    public void SetSecretVariable(string key, byte[]? protectedValue, UserId? by, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var existing = _variables.FirstOrDefault(v => v.Key == key);
        if (protectedValue is null)
        {
            if (existing is not null)
            {
                _variables.Remove(existing);
            }
        }
        else if (existing is null)
        {
            _variables.Add(ServerVariable.Secret(Id, key, protectedValue, by, now));
        }
        else
        {
            existing.SetSecret(protectedValue, by, now);
        }

        UpdatedAt = now;
    }

    /// <summary>The start options users can change (docs/architecture/05, "Startup").</summary>
    public Result SetStartup(IReadOnlyList<string> extraArguments, bool autoStart, bool restartOnCrash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(extraArguments);
        if (extraArguments.Count > MaxExtraArguments
            || extraArguments.Any(a => a is null || a.Length is 0 or > MaxExtraArgumentLength || a.Any(char.IsControl)))
        {
            return ServerErrors.ExtraArgumentsInvalid;
        }

        ExtraArguments = [.. extraArguments];
        AutoStart = autoStart;
        RestartOnCrash = restartOnCrash;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>How many backups retention keeps (locked ones are kept besides); null keeps them all.</summary>
    public Result SetBackupLimit(int? limit, DateTimeOffset now)
    {
        if (limit is < 1 or > MaxBackupLimit)
        {
            return ServerErrors.BackupLimitInvalid;
        }

        BackupLimit = limit;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Caps on the server's CPU and memory (docs/architecture/06, "Resource limits"); null removes a cap.</summary>
    public Result SetLimits(int? cpuPercent, int? memoryMb, DateTimeOffset now)
    {
        if (cpuPercent is < 1 or > 100 || memoryMb is < MinMemoryLimitMb or > MaxMemoryLimitMb)
        {
            return ServerErrors.LimitsInvalid;
        }

        CpuLimitPercent = cpuPercent;
        MemoryLimitMb = memoryMb;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>The Steam account SteamCMD signs in with from the next install or update; null: anonymous.</summary>
    public void SetSteamAccount(SteamAccountId? account, DateTimeOffset now)
    {
        SteamAccountId = account;
        UpdatedAt = now;
    }

    /// <summary>How many databases the server may have; lowering it keeps the ones it has.</summary>
    public Result SetDatabaseLimit(int limit, DateTimeOffset now)
    {
        if (limit is < 0 or > MaxDatabaseLimit)
        {
            return ServerErrors.DatabaseLimitInvalid;
        }

        DatabaseLimit = limit;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Where new backups are copied (null: nowhere); backups already made keep their copies where they are.</summary>
    public void SetBackupStorage(BackupStorageId? storage, DateTimeOffset now)
    {
        BackupStorageId = storage;
        UpdatedAt = now;
    }

    /// <summary>Stop and kill are always allowed: they only ever make things safer.</summary>
    public void RequestStop(DateTimeOffset now)
    {
        DesiredState = DesiredState.Stopped;
        UpdatedAt = now;
    }

    /// <summary>The game asked to stop, or it cannot be kept running: the desired state follows the agent's report.</summary>
    public static bool StopsIntent(ServerStateReason reason) =>
        reason is ServerStateReason.ExitedByItself or ServerStateReason.CrashLoop or ServerStateReason.StartFailed;

    /// <summary>
    /// What the agent reports. Changes older than the last one taken are ignored; a <paramref name="snapshot"/> (the
    /// agent's Hello) is always taken and resets the sequence, since a restarted agent counts from zero again.
    /// </summary>
    public bool Observe(ServerRuntimeState state, ServerStateReason reason, int? exitCode, long sequence, DateTimeOffset at, bool snapshot)
    {
        if (!snapshot && sequence <= StateSequence)
        {
            return false;
        }

        if (state == ServerRuntimeState.Starting && RuntimeState != ServerRuntimeState.Starting)
        {
            LastStartedAt = at;
        }

        if (state is ServerRuntimeState.Offline or ServerRuntimeState.Crashed && RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            LastStoppedAt = at;
        }

        if (state is ServerRuntimeState.Offline or ServerRuntimeState.Crashed)
        {
            LastExitCode = exitCode ?? LastExitCode;
        }

        if (StopsIntent(reason))
        {
            DesiredState = DesiredState.Stopped;
        }

        RuntimeState = state;
        LastStateReason = reason;
        StateSequence = sequence;
        StateObservedAt = at;
        UpdatedAt = at;
        return true;
    }
}
