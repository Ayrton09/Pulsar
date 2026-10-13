using Pulsar.Domain.Common;

namespace Pulsar.Application.Nodes;

/// <summary>
/// Keeps refused agent requests from flooding the audit log (docs/architecture/08, "Audit"): an agent turned away (its
/// node disabled, its key revoked) keeps trying about once a minute. The first refusal of a node for one reason gets an
/// entry; the same ones within <see cref="Window"/> are counted, and the next entry says how many were left out. Each
/// agent address is also held to the agent endpoints' rate limit, and refusals that name no existing node are only
/// logged, so this holds one entry per node and reason at most.
/// </summary>
public sealed class AgentRefusalBudget(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Lock _lock = new();
    private readonly Dictionary<(NodeId Node, string Reason), (DateTimeOffset RecordedAt, int LeftOut)> _recorded = [];

    /// <summary>Whether this refusal gets an entry, and how many of the same were left out before it.</summary>
    public bool TryRecord(NodeId node, string reason, out int leftOut)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (_recorded.TryGetValue((node, reason), out var last) && now - last.RecordedAt < Window)
            {
                _recorded[(node, reason)] = (last.RecordedAt, last.LeftOut + 1);
                leftOut = 0;
                return false;
            }

            leftOut = last.LeftOut;
            _recorded[(node, reason)] = (now, 0);
            return true;
        }
    }
}
