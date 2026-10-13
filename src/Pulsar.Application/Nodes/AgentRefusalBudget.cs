using System.Net;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Nodes;

/// <summary>
/// Keeps refused agent requests from flooding the audit log (docs/architecture/08, "Audit"): an agent turned away (its
/// node disabled, its key revoked) keeps trying about once a minute. The first refusal of a node for one reason from
/// one address gets an entry; the same ones within <see cref="Window"/> are counted, and the next entry says how many
/// were left out. Another address gets its own entry, so the node's own agent being refused never hides a copied token
/// or key tried from elsewhere; past <see cref="MaxAddresses"/> addresses for one node and reason, the others share one
/// entry. Each agent address is also held to the agent endpoints' rate limit, and refusals that name no existing node
/// are only logged, so this holds a few entries per node and reason at most.
/// </summary>
public sealed class AgentRefusalBudget(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Addresses with an entry of their own, per node and reason.</summary>
    public const int MaxAddresses = 8;

    /// <summary>The entry the addresses past <see cref="MaxAddresses"/> share.</summary>
    private const string OtherAddresses = "*";

    private readonly Lock _lock = new();
    private readonly Dictionary<(NodeId Node, string Reason), Dictionary<string, (DateTimeOffset RecordedAt, int LeftOut)>> _recorded = [];

    /// <summary>Whether this refusal gets an entry, and how many of the same were left out before it.</summary>
    /// <param name="address">The client's address; null when unknown.</param>
    public bool TryRecord(NodeId node, string reason, IPAddress? address, out int leftOut)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (!_recorded.TryGetValue((node, reason), out var byAddress))
            {
                _recorded[(node, reason)] = byAddress = new Dictionary<string, (DateTimeOffset, int)>(StringComparer.Ordinal);
            }

            var key = address?.ToString() ?? string.Empty;
            if (!byAddress.ContainsKey(key) && byAddress.Count(e => e.Key != OtherAddresses) >= MaxAddresses)
            {
                key = Prune(byAddress, now) ? key : OtherAddresses;
            }

            if (byAddress.TryGetValue(key, out var last) && now - last.RecordedAt < Window)
            {
                byAddress[key] = (last.RecordedAt, last.LeftOut + 1);
                leftOut = 0;
                return false;
            }

            leftOut = last.LeftOut;
            byAddress[key] = (now, 0);
            return true;
        }
    }

    /// <summary>How many entries one node and reason hold (for tests).</summary>
    public int Tracked(NodeId node, string reason)
    {
        lock (_lock)
        {
            return _recorded.TryGetValue((node, reason), out var byAddress) ? byAddress.Count : 0;
        }
    }

    /// <summary>
    /// Drops the addresses whose window ended, their uncounted refusals carried to the shared entry. True when that made
    /// room for another address.
    /// </summary>
    private static bool Prune(Dictionary<string, (DateTimeOffset RecordedAt, int LeftOut)> byAddress, DateTimeOffset now)
    {
        var carried = 0;
        foreach (var (address, entry) in byAddress.Where(e => e.Key != OtherAddresses && now - e.Value.RecordedAt >= Window).ToList())
        {
            carried += entry.LeftOut;
            byAddress.Remove(address);
        }

        if (carried > 0)
        {
            var others = byAddress.TryGetValue(OtherAddresses, out var shared) ? shared : (RecordedAt: now - Window, LeftOut: 0);
            byAddress[OtherAddresses] = (others.RecordedAt, others.LeftOut + carried);
        }

        return byAddress.Count(e => e.Key != OtherAddresses) < MaxAddresses;
    }
}
