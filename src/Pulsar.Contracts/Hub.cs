namespace Pulsar.Contracts;

/// <summary>Methods the agent calls on the panel's agent hub (<see cref="AgentProtocol.HubPath"/>).</summary>
public interface IAgentHub
{
    /// <summary>First message after connecting; the panel answers with its time and keys (or closes the connection).</summary>
    Task<HelloAccepted> Hello(AgentHello hello);

    /// <summary>Node-level metrics, every 15 s.</summary>
    Task NodeMetrics(NodeMetrics metrics);

    /// <summary>A server changed state. Not retried: the next Hello carries every server's state anyway.</summary>
    Task ServerStateChanged(ServerStateChanged change);

    Task OperationProgress(OperationProgress progress);

    /// <summary>The end of an operation; the agent keeps it and sends it again until this call succeeds.</summary>
    Task OperationCompleted(OperationCompleted completed);

    /// <summary>Console output of a server the panel subscribed to (<see cref="SubscribeConsoleCommand"/>).</summary>
    Task ConsoleOutput(ServerConsoleOutput output);

    /// <summary>Usage of the running servers, every 5 s.</summary>
    Task Metrics(MetricsBatch batch);

    /// <summary>The end of a transfer the agent received (<see cref="ReceiveFileCommand"/>).</summary>
    Task TransferCompleted(TransferResult result);

    /// <summary>
    /// How an update of Pulsar on the node goes (<see cref="UpdatePulsarCommand"/>): each step, then its end, which the
    /// installer wrote and the agent sends again after each Hello until a newer one replaces it.
    /// </summary>
    Task UpdateReport(UpdateReport report);
}

/// <summary>Methods the panel calls on a connected agent (SignalR client results).</summary>
public interface IAgentClient
{
    Task<CommandAck> Execute(SignedMessage command);
}

/// <param name="PinnedPanelKeys">
/// Key ids of the panel keys the agent trusts. The panel signs this connection's commands with one of them and, when they
/// differ from its own, sends <see cref="UpdatePanelKeysCommand"/>. Null from agents that predate panel key rotation.
/// </param>
/// <param name="Servers">The actual state of every server the agent knows (docs/architecture/05, "Reconciliation").</param>
/// <param name="OperationsInFlight">Operations the agent is still running; any other one the panel thinks is running was interrupted.</param>
/// <param name="CachedSteamLogins">
/// The Steam accounts whose logins this agent has cached (at most <see cref="MaxCachedSteamLogins"/>): the panel sends a
/// signed <see cref="ForgetSteamLoginCommand"/> for each one it no longer has.
/// </param>
public sealed record AgentHello(
    string AgentVersion,
    int ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    NodeInventory Inventory,
    IReadOnlyList<string>? PinnedPanelKeys = null,
    IReadOnlyList<ServerSnapshot>? Servers = null,
    IReadOnlyList<Guid>? OperationsInFlight = null,
    IReadOnlyList<Guid>? CachedSteamLogins = null)
{
    /// <summary>The most cached Steam logins a Hello names; the panel reads no more.</summary>
    public const int MaxCachedSteamLogins = 256;
}

/// <summary><see cref="ServerTime"/> lets the agent detect clock skew (command expiry is a secondary defense).</summary>
/// <param name="ConsoleSubscriptions">
/// Consoles browsers are watching: the agent sends their output from the given sequence on, as after a
/// <see cref="SubscribeConsoleCommand"/>. Subscriptions end with the connection.
/// </param>
/// <param name="SteamAccountIds">
/// Not sent any more, and ignored by agents: this answer is not signed, and a list from whatever answered in the panel's
/// place would have made the agent delete its cached logins. The panel now sends signed
/// <see cref="ForgetSteamLoginCommand"/>s for the logins <see cref="AgentHello.CachedSteamLogins"/> names.
/// </param>
public sealed record HelloAccepted(DateTimeOffset ServerTime, IReadOnlyList<PanelKey> PanelKeys,
    IReadOnlyList<ConsoleSubscription>? ConsoleSubscriptions = null, IReadOnlyList<Guid>? SteamAccountIds = null);

/// <summary>A drive the agent uses (<see cref="DiskUses"/>), with its free space: the panel warns when one is nearly full.</summary>
public sealed record DiskUsage(string Root, long TotalBytes, long FreeBytes, IReadOnlyList<string> Uses);

public static class DiskUses
{
    public const string Servers = "servers";
    public const string Backups = "backups";
}

/// <param name="Disks">The drives of the servers and backups folders; null from agents older than Phase 10.</param>
public sealed record NodeMetrics(DateTimeOffset At, double CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, TimeSpan Uptime,
    IReadOnlyList<DiskUsage>? Disks = null);

/// <summary>An update of Pulsar on a node, as far as it went (<see cref="IAgentHub.UpdateReport"/>).</summary>
/// <param name="Status">One of <see cref="UpdateStatuses"/>.</param>
/// <param name="Parts">What the installer updated: <c>agent</c>, <c>panel</c>.</param>
/// <param name="Error">Why it failed or was put back, for the administrator (plain text).</param>
public sealed record UpdateReport(string Status, string? From, string? To, IReadOnlyList<string>? Parts, string? Error, DateTimeOffset At)
{
    public const int MaxErrorLength = 2000;
}

public static class UpdateStatuses
{
    public const string Downloading = "downloading";
    public const string Installing = "installing";
    public const string Succeeded = "succeeded";
    public const string RolledBack = "rolled_back";
    public const string Failed = "failed";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>([Downloading, Installing, Succeeded, RolledBack, Failed], StringComparer.Ordinal);

    /// <summary>Whether the update ended (the installer's result, or a failure before it ran).</summary>
    public static bool IsFinal(string status) => status is Succeeded or RolledBack or Failed;
}
