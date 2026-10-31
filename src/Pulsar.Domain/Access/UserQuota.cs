using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>
/// What a user may create for themselves (Pulsar 1.0.24, docs/architecture/02 "Quotas"): servers on the nodes listed,
/// each with its own CPU and memory caps, as long as the servers they own stay within these totals. Without one, a
/// user creates servers only where a role gives them <c>server.create</c>.
/// </summary>
public sealed class UserQuota
{
    public const int MaxServersLimit = 1000;
    public const int MaxNodes = 100;

    /// <summary>The CPU total: up to a hundred servers each using all of their node.</summary>
    public const int MaxCpuPercentLimit = 100 * 100;

    private UserQuota()
    {
        // For EF Core materialization.
        NodeIds = [];
    }

    private UserQuota(UserId user, DateTimeOffset now)
    {
        UserId = user;
        NodeIds = [];
        CreatedAt = now;
        UpdatedAt = now;
    }

    public UserId UserId { get; private set; }

    /// <summary>How many servers they may own (0: none more, those they have stay).</summary>
    public int MaxServers { get; private set; }

    /// <summary>The memory caps of their servers, added up, in MiB.</summary>
    public int MaxMemoryMb { get; private set; }

    /// <summary>The CPU caps of their servers (each a share of its node, 1–100 %), added up.</summary>
    public int MaxCpuPercent { get; private set; }

    /// <summary>The nodes they may create their servers on.</summary>
    public IReadOnlyList<NodeId> NodeIds { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<UserQuota> Create(UserId user, int maxServers, int maxMemoryMb, int maxCpuPercent, IEnumerable<NodeId> nodes,
        DateTimeOffset now)
    {
        var quota = new UserQuota(user, now);
        var set = quota.Set(maxServers, maxMemoryMb, maxCpuPercent, nodes, now);
        return set.IsSuccess ? quota : set.Error!;
    }

    public Result Set(int maxServers, int maxMemoryMb, int maxCpuPercent, IEnumerable<NodeId> nodes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var distinct = nodes.Distinct().ToList();
        if (maxServers is < 0 or > MaxServersLimit || maxMemoryMb is < 0 or > Servers.Server.MaxMemoryLimitMb * 10
            || maxCpuPercent is < 0 or > MaxCpuPercentLimit || distinct.Count > MaxNodes)
        {
            return AccessErrors.QuotaInvalid;
        }

        MaxServers = maxServers;
        MaxMemoryMb = maxMemoryMb;
        MaxCpuPercent = maxCpuPercent;
        NodeIds = distinct;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Whether one more server with these caps fits, next to what the user owns already.</summary>
    public Result Fits(NodeId node, int servers, int memoryMb, int cpuPercent, int newMemoryMb, int newCpuPercent)
    {
        if (!NodeIds.Contains(node))
        {
            return AccessErrors.QuotaNodeNotAllowed;
        }

        if (servers + 1 > MaxServers)
        {
            return AccessErrors.QuotaServers;
        }

        if ((long)memoryMb + newMemoryMb > MaxMemoryMb)
        {
            return AccessErrors.QuotaMemory;
        }

        return (long)cpuPercent + newCpuPercent > MaxCpuPercent ? AccessErrors.QuotaCpu : Result.Success();
    }
}
