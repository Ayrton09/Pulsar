using System.Text.Json;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Servers;

/// <summary>What the agent keeps about a server between runs (<c>state\servers\{id}.json</c>).</summary>
/// <param name="DesiredRunning">Whether it should be running (the panel's intent, or the game's own stop).</param>
/// <param name="ResumeOnStart">It was running when the agent stopped in an orderly way: started again next time.</param>
public sealed record ServerRecord(Guid ServerId, ServerRuntimeSpec Spec, bool DesiredRunning, bool ResumeOnStart);

/// <summary>
/// Server records and undelivered operation results, as JSON files in the agent's state directory (SYSTEM and
/// Administrators only). Every write replaces the file atomically.
/// </summary>
public sealed class ServerStore(AgentPaths paths)
{
    private static readonly JsonSerializerOptions s_options = new(ProtocolJson.Options) { WriteIndented = true };

    public IReadOnlyList<ServerRecord> LoadAll()
    {
        if (!Directory.Exists(paths.ServersStateDirectory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(paths.ServersStateDirectory, "*.json").Select(Read<ServerRecord>).OfType<ServerRecord>()];
    }

    public ServerRecord? Load(Guid serverId)
    {
        var file = Path.Combine(paths.ServersStateDirectory, $"{serverId:N}.json");
        return File.Exists(file) ? Read<ServerRecord>(file) : null;
    }

    public void Save(ServerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Write(paths.ServersStateDirectory, record.ServerId, record);
    }

    public void Delete(Guid serverId) => File.Delete(Path.Combine(paths.ServersStateDirectory, $"{serverId:N}.json"));

    public IReadOnlyList<OperationCompleted> PendingResults()
    {
        if (!Directory.Exists(paths.OutboxDirectory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(paths.OutboxDirectory, "*.json").Select(Read<OperationCompleted>).OfType<OperationCompleted>()];
    }

    public void SaveResult(OperationCompleted result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Write(paths.OutboxDirectory, result.OperationId, result);
    }

    public void DeleteResult(Guid operationId) => File.Delete(Path.Combine(paths.OutboxDirectory, $"{operationId:N}.json"));

    private static T? Read<T>(string file)
    {
        using var stream = File.OpenRead(file);
        return JsonSerializer.Deserialize<T>(stream, s_options);
    }

    private static void Write<T>(string directory, Guid id, T value)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"{id:N}.json");
        var temporary = file + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, s_options));
        File.Move(temporary, file, overwrite: true);
    }
}
