using Pulsar.Application.Updates;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Agents;

/// <summary>
/// Takes a node's <see cref="UpdateReport"/>s off its hub invocation. <see cref="PanelUpdates.ReportAsync"/> waits for the node's
/// update turn, which a request for that node holds while it waits for the agent's answer to its command; the answer is a
/// message of the same connection, which SignalR does not read until the hub method running for that connection returns. A
/// report awaited inside its own hub method would so wait for an answer that cannot arrive (until the command times out),
/// and stall the node's other hub traffic meanwhile. Reports of one node run one after another, in the order they arrived;
/// failures are logged, as nobody awaits them.
/// </summary>
internal sealed partial class UpdateReportQueue(IServiceScopeFactory scopes, ILogger<UpdateReportQueue> logger)
{
    private readonly Dictionary<NodeId, Line> _lines = [];
    private readonly Lock _lock = new();

    /// <summary>Tests: what handles a report (the scoped <see cref="PanelUpdates.ReportAsync"/> by default).</summary>
    internal Func<NodeId, UpdateReport, Task>? Handler { get; init; }

    public void Enqueue(NodeId nodeId, UpdateReport report)
    {
        lock (_lock)
        {
            if (!_lines.TryGetValue(nodeId, out var line))
            {
                line = new Line();
                _lines.Add(nodeId, line);
            }

            line.Waiting++;
            var previous = line.Tail;
            line.Tail = Task.Run(() => RunAsync(previous, nodeId, report));
        }
    }

    private async Task RunAsync(Task previous, NodeId nodeId, UpdateReport report)
    {
        // Never faults: every report's failure is caught below.
        await previous;
        try
        {
            if (Handler is { } handler)
            {
                await handler(nodeId, report);
            }
            else
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PanelUpdates>().ReportAsync(nodeId, report, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LogReportFailed(ex, nodeId, report.Status);
        }
        finally
        {
            lock (_lock)
            {
                if (--_lines[nodeId].Waiting == 0)
                {
                    _lines.Remove(nodeId);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Node {NodeId}'s update report ({Status}) could not be kept")]
    private partial void LogReportFailed(Exception exception, NodeId nodeId, string? status);

    private sealed class Line
    {
        public Task Tail { get; set; } = Task.CompletedTask;

        public int Waiting { get; set; }
    }
}
