using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Metrics;
using Pulsar.Domain.Common;
using Pulsar.Domain.Metrics;

namespace Pulsar.Infrastructure.Persistence;

/// <summary>
/// History in PostgreSQL with set-based SQL: a minute's buckets in one statement, the roll-up and the charts grouped by
/// the database. Buckets are aligned on UTC (<c>date_bin</c> from a UTC origin, never the session's time zone).
/// </summary>
internal sealed class MetricHistoryStore(PulsarDbContext db) : IMetricHistoryStore
{
    public async Task AddAsync(IReadOnlyCollection<ServerMetricBucket> servers, IReadOnlyCollection<NodeMetricBucket> nodes, CancellationToken cancellationToken)
    {
        // Both statements or neither: a failed write is retried whole, and a half kept would be added twice.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (servers.Count > 0)
        {
            var ids = servers.Select(b => b.ServerId.Value).ToArray();
            var ats = servers.Select(b => b.At.ToUniversalTime()).ToArray();
            var samples = servers.Select(b => b.Samples).ToArray();
            var cpuSums = servers.Select(b => b.CpuSum).ToArray();
            var cpuMaxes = servers.Select(b => b.CpuMax).ToArray();
            var memorySums = servers.Select(b => b.MemorySum).ToArray();
            var memoryMaxes = servers.Select(b => b.MemoryMax).ToArray();
            var playerSamples = servers.Select(b => b.PlayerSamples).ToArray();
            var playersSums = servers.Select(b => b.PlayersSum).ToArray();
            var playersMaxes = servers.Select(b => b.PlayersMax).ToArray();
            // Servers deleted since their samples came are left out (their history went with them).
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO server_metrics (server_id, resolution, at, samples, cpu_sum, cpu_max, memory_sum, memory_max, player_samples, players_sum, players_max)
                SELECT u.server_id, 1, u.at, u.samples, u.cpu_sum, u.cpu_max, u.memory_sum, u.memory_max, u.player_samples, u.players_sum, u.players_max
                FROM unnest({ids}, {ats}, {samples}, {cpuSums}, {cpuMaxes}, {memorySums}, {memoryMaxes}, {playerSamples}, {playersSums}, {playersMaxes})
                    AS u(server_id, at, samples, cpu_sum, cpu_max, memory_sum, memory_max, player_samples, players_sum, players_max)
                WHERE EXISTS (SELECT 1 FROM servers s WHERE s.id = u.server_id)
                ON CONFLICT (server_id, resolution, at) DO UPDATE SET
                    samples = server_metrics.samples + excluded.samples,
                    cpu_sum = server_metrics.cpu_sum + excluded.cpu_sum,
                    cpu_max = greatest(server_metrics.cpu_max, excluded.cpu_max),
                    memory_sum = server_metrics.memory_sum + excluded.memory_sum,
                    memory_max = greatest(server_metrics.memory_max, excluded.memory_max),
                    player_samples = server_metrics.player_samples + excluded.player_samples,
                    players_sum = server_metrics.players_sum + excluded.players_sum,
                    players_max = greatest(server_metrics.players_max, excluded.players_max)
                """, cancellationToken);
        }

        if (nodes.Count > 0)
        {
            var ids = nodes.Select(b => b.NodeId.Value).ToArray();
            var ats = nodes.Select(b => b.At.ToUniversalTime()).ToArray();
            var samples = nodes.Select(b => b.Samples).ToArray();
            var cpuSums = nodes.Select(b => b.CpuSum).ToArray();
            var cpuMaxes = nodes.Select(b => b.CpuMax).ToArray();
            var usedSums = nodes.Select(b => b.MemoryUsedSum).ToArray();
            var usedMaxes = nodes.Select(b => b.MemoryUsedMax).ToArray();
            var totals = nodes.Select(b => b.MemoryTotal).ToArray();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO node_metrics (node_id, resolution, at, samples, cpu_sum, cpu_max, memory_used_sum, memory_used_max, memory_total)
                SELECT u.node_id, 1, u.at, u.samples, u.cpu_sum, u.cpu_max, u.memory_used_sum, u.memory_used_max, u.memory_total
                FROM unnest({ids}, {ats}, {samples}, {cpuSums}, {cpuMaxes}, {usedSums}, {usedMaxes}, {totals})
                    AS u(node_id, at, samples, cpu_sum, cpu_max, memory_used_sum, memory_used_max, memory_total)
                WHERE EXISTS (SELECT 1 FROM nodes n WHERE n.id = u.node_id)
                ON CONFLICT (node_id, resolution, at) DO UPDATE SET
                    samples = node_metrics.samples + excluded.samples,
                    cpu_sum = node_metrics.cpu_sum + excluded.cpu_sum,
                    cpu_max = greatest(node_metrics.cpu_max, excluded.cpu_max),
                    memory_used_sum = node_metrics.memory_used_sum + excluded.memory_used_sum,
                    memory_used_max = greatest(node_metrics.memory_used_max, excluded.memory_used_max),
                    memory_total = excluded.memory_total
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RollUpAsync(DateTimeOffset start, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var from = start.ToUniversalTime();
        var to = until.ToUniversalTime();
        // The hour's buckets are replaced, not added to: the roll-up runs again while an hour is still filling.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO server_metrics (server_id, resolution, at, samples, cpu_sum, cpu_max, memory_sum, memory_max, player_samples, players_sum, players_max)
            SELECT server_id, 60, date_bin('1 hour', at, TIMESTAMPTZ '2000-01-01 00:00:00+00'), sum(samples)::int, sum(cpu_sum), max(cpu_max),
                sum(memory_sum)::bigint, max(memory_max), sum(player_samples)::int, sum(players_sum)::bigint, max(players_max)
            FROM server_metrics
            WHERE resolution = 1 AND at >= {from} AND at < {to}
            GROUP BY server_id, date_bin('1 hour', at, TIMESTAMPTZ '2000-01-01 00:00:00+00')
            ON CONFLICT (server_id, resolution, at) DO UPDATE SET
                samples = excluded.samples, cpu_sum = excluded.cpu_sum, cpu_max = excluded.cpu_max, memory_sum = excluded.memory_sum,
                memory_max = excluded.memory_max, player_samples = excluded.player_samples, players_sum = excluded.players_sum,
                players_max = excluded.players_max
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO node_metrics (node_id, resolution, at, samples, cpu_sum, cpu_max, memory_used_sum, memory_used_max, memory_total)
            SELECT node_id, 60, date_bin('1 hour', at, TIMESTAMPTZ '2000-01-01 00:00:00+00'), sum(samples)::int, sum(cpu_sum), max(cpu_max),
                sum(memory_used_sum)::bigint, max(memory_used_max), (array_agg(memory_total ORDER BY at DESC))[1]
            FROM node_metrics
            WHERE resolution = 1 AND at >= {from} AND at < {to}
            GROUP BY node_id, date_bin('1 hour', at, TIMESTAMPTZ '2000-01-01 00:00:00+00')
            ON CONFLICT (node_id, resolution, at) DO UPDATE SET
                samples = excluded.samples, cpu_sum = excluded.cpu_sum, cpu_max = excluded.cpu_max, memory_used_sum = excluded.memory_used_sum,
                memory_used_max = excluded.memory_used_max, memory_total = excluded.memory_total
            """, cancellationToken);
    }

    public async Task PruneAsync(MetricResolution resolution, DateTimeOffset before, CancellationToken cancellationToken)
    {
        var cutoff = before.ToUniversalTime();
        await db.ServerMetrics.Where(b => b.Resolution == resolution && b.At < cutoff).ExecuteDeleteAsync(cancellationToken);
        await db.NodeMetrics.Where(b => b.Resolution == resolution && b.At < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ServerMetricPoint>> ReadServerAsync(ServerId server, MetricResolution source, TimeSpan width, DateTimeOffset start,
        DateTimeOffset until, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT date_bin(@step, at, TIMESTAMPTZ '2000-01-01 00:00:00+00') AS point,
                sum(cpu_sum) / sum(samples), max(cpu_max),
                (sum(memory_sum) / sum(samples))::bigint, max(memory_max),
                CASE WHEN sum(player_samples) > 0 THEN sum(players_sum)::double precision / sum(player_samples) END, max(players_max)
            FROM server_metrics
            WHERE server_id = @id AND resolution = @resolution AND at >= @from AND at < @to
            GROUP BY point
            ORDER BY point
            """;
        return await ReadAsync(Sql, server.Value, source, width, start, until, reader => new ServerMetricPoint(
            reader.GetFieldValue<DateTimeOffset>(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt64(3), reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetInt32(6)), cancellationToken);
    }

    public async Task<IReadOnlyList<NodeMetricPoint>> ReadNodeAsync(NodeId node, MetricResolution source, TimeSpan width, DateTimeOffset start,
        DateTimeOffset until, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT date_bin(@step, at, TIMESTAMPTZ '2000-01-01 00:00:00+00') AS point,
                sum(cpu_sum) / sum(samples), max(cpu_max),
                (sum(memory_used_sum) / sum(samples))::bigint, max(memory_used_max),
                (array_agg(memory_total ORDER BY at DESC))[1]
            FROM node_metrics
            WHERE node_id = @id AND resolution = @resolution AND at >= @from AND at < @to
            GROUP BY point
            ORDER BY point
            """;
        return await ReadAsync(Sql, node.Value, source, width, start, until, reader => new NodeMetricPoint(
            reader.GetFieldValue<DateTimeOffset>(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt64(3), reader.GetInt64(4),
            reader.GetInt64(5)), cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, Guid id, MetricResolution source, TimeSpan step, DateTimeOffset from,
        DateTimeOffset to, Func<DbDataReader, T> read, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            Add(command, "step", step);
            Add(command, "id", id);
            Add(command, "resolution", (short)source);
            Add(command, "from", from.ToUniversalTime());
            Add(command, "to", to.ToUniversalTime());
            var points = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                points.Add(read(reader));
            }

            return points;
        }
        finally
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
