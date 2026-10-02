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
}

public sealed record OperationProgressView(Guid ServerId, Guid OperationId, string Stage, decimal? Percent);

/// <param name="CpuPercent">Share of the node's whole CPU (0–100).</param>
public sealed record ServerMetricsView(Guid ServerId, DateTimeOffset At, double CpuPercent, long MemoryBytes, int ProcessCount, double UptimeSeconds);

/// <summary>A node's latest usage, as the node API returns it.</summary>
public sealed record NodeMetricsUpdate(Guid NodeId, NodeMetricsView Metrics);

/// <summary>A server a browser now watches: its latest usage sample, when it is running.</summary>
public sealed record ServerWatch(ServerMetricsView? Metrics);

/// <summary>Console entries of one server, in sequence order (a browser drops the ones it already has).</summary>
public sealed record ConsoleOutputView(Guid ServerId, IReadOnlyList<ConsoleEntry> Entries);

/// <summary>What a browser gets when it starts watching a console: the recent output the panel holds.</summary>
/// <param name="NodeConnected">False when the node is offline: no output can come until it reconnects.</param>
public sealed record ConsoleSnapshot(IReadOnlyList<ConsoleEntry> Entries, bool NodeConnected);
