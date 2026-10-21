using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api.Realtime;

/// <summary>What the panel pushes to browsers over <see cref="PanelHub"/> (docs/architecture/07, "Real time").</summary>
public interface IPanelClient
{
    /// <summary>These servers changed: fetch them (and the list) again. Sent to watchers of the server list.</summary>
    Task ServersChanged(IReadOnlyCollection<Guid> servers);

    /// <summary>These nodes changed, or connected or disconnected. Sent to watchers of the node list.</summary>
    Task NodesChanged(IReadOnlyCollection<Guid> nodes);

    Task OperationProgress(OperationProgressView progress);

    Task ServerMetrics(ServerMetricsView metrics);

    Task NodeMetrics(NodeMetricsUpdate update);

    Task ConsoleOutput(ConsoleOutputView output);

    /// <summary>The appearance settings changed: fetch them again. Sent to every browser; it carries nothing.</summary>
    Task AppearanceChanged();
}

public sealed record OperationProgressView(Guid ServerId, Guid OperationId, string Stage, decimal? Percent);

/// <param name="CpuPercent">Share of the node's whole CPU (0–100).</param>
/// <param name="Query">What the game last answered to its query; null without one or when it does not answer.</param>
/// <param name="CommittedBytes">Memory committed by the server's processes (what its memory limit counts); null from older agents.</param>
public sealed record ServerMetricsView(Guid ServerId, DateTimeOffset At, double CpuPercent, long MemoryBytes, int ProcessCount, double UptimeSeconds,
    ServerQueryView? Query = null, long? CommittedBytes = null);

/// <summary>
/// A running game's name, map and players, as it answered its query. Every text is the game's or its players': the
/// agent cleaned it, the panel cleans it again (a node is not trusted with what browsers show) and pages show it as
/// plain text.
/// </summary>
public sealed record ServerQueryView(DateTimeOffset At, string? Name, string? Map, int Players, int MaxPlayers, int Bots, string? Version,
    IReadOnlyList<QueryPlayerView> PlayerList)
{
    private const int MaxCount = 1_000_000;

    public static ServerQueryView? From(ServerQueryInfo? info) => info is null ? null : new ServerQueryView(info.At, QueryText.Clean(info.Name),
        QueryText.Clean(info.Map), Math.Clamp(info.Players, 0, MaxCount), Math.Clamp(info.MaxPlayers, 0, MaxCount), Math.Clamp(info.Bots, 0, MaxCount),
        QueryText.Clean(info.Version),
        [.. (info.PlayerList ?? []).OfType<QueryPlayer>().Take(QueryText.MaxPlayers).Select(p => QueryText.Clean(p.Name) is { } name
            ? new QueryPlayerView(name, p.Score, p.Connected is { } connected && connected >= TimeSpan.Zero ? Math.Floor(connected.TotalSeconds) : null)
            : null).OfType<QueryPlayerView>()]);
}

/// <param name="ConnectedSeconds">How long the player has been on the server, when the game says.</param>
public sealed record QueryPlayerView(string Name, long? Score, double? ConnectedSeconds);

/// <summary>A node's latest usage, as the node API returns it.</summary>
public sealed record NodeMetricsUpdate(Guid NodeId, NodeMetricsView Metrics);

/// <summary>A server a browser now watches: its latest usage sample, when it is running.</summary>
public sealed record ServerWatch(ServerMetricsView? Metrics);

/// <summary>Console entries of one server, in sequence order (a browser drops the ones it already has).</summary>
public sealed record ConsoleOutputView(Guid ServerId, IReadOnlyList<ConsoleEntry> Entries);

/// <summary>What a browser gets when it starts watching a console: the recent output the panel holds.</summary>
/// <param name="NodeConnected">False when the node is offline: no output can come until it reconnects.</param>
public sealed record ConsoleSnapshot(IReadOnlyList<ConsoleEntry> Entries, bool NodeConnected);
