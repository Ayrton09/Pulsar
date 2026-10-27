using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// The ids each start wrote into a json file's lists (Pulsar 1.0.14; <see cref="ConfigListDefinition"/>), kept in the
/// server's <c>.pulsar</c> folder: the next start takes out only those the panel no longer lists, never what users added.
/// The server's account can change the folder, so a record that cannot be read or written never stops a start: it only
/// leaves the panel's former ids in place.
/// </summary>
public static class ConfigListRecords
{
    public const string Directory = ".pulsar/lists";

    /// <summary>The largest record read: the server's account can write the folder, so a bigger one counts as none.</summary>
    public const int MaximumSize = 256 * 1024;

    /// <summary>A file's record, named after its path whatever its case or slashes.</summary>
    public static string PathOf(string configPath)
    {
        ArgumentNullException.ThrowIfNull(configPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(configPath.Replace('\\', '/').ToUpperInvariant()));
        return $"{Directory}/{Convert.ToHexStringLower(hash)[..16]}.json";
    }

    /// <summary>The ids the last start wrote, by array; null when there is no record, or none that can be read.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? Read(IServerFiles files, string serverRoot, string configPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            using var stream = files.OpenRead(serverRoot, PathOf(configPath));
            if (stream is null || stream.Length > MaximumSize)
            {
                return null;
            }

            return JsonSerializer.Deserialize<Dictionary<string, List<string?>?>>(stream)?
                .Where(list => list.Value is not null)
                .ToDictionary(list => list.Key, IReadOnlyList<string> (list) => [.. list.Value!.OfType<string>()], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or GameProcessException)
        {
            // A folder or a link in its place, a file held open, or text that is no record.
            return null;
        }
    }

    /// <summary>Records the ids <paramref name="file"/>'s lists hold now; false when the record cannot be written.</summary>
    public static bool Write(IServerFiles files, string serverRoot, RenderedConfigFile file)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(file);
        var lists = file.Lists.ToDictionary(list => list.ArrayPath, list => list.Ids, StringComparer.Ordinal);
        try
        {
            using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(lists));
            files.Write(serverRoot, PathOf(file.Path), content);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GameProcessException)
        {
            return false;
        }
    }
}
