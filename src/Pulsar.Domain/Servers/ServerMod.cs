using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>Where a mod comes from.</summary>
public enum ModSource
{
    Modrinth,
    CurseForge,

    /// <summary>uMod's plugins for Oxide and Carbon (Pulsar 1.0.14).</summary>
    Umod,

    /// <summary>Thunderstore's packages for BepInEx (Pulsar 1.0.14).</summary>
    Thunderstore,
}

public enum ServerModState
{
    /// <summary>An operation is putting it on the server.</summary>
    Installing,

    Installed,

    /// <summary>An operation is deleting it (or replacing it with another version).</summary>
    Removing,
}

/// <summary>
/// A file from a mod catalog on a server (Pulsar 1.0.9; docs/architecture/02, "Mods"): a mod or a plugin, the version
/// of its project the panel chose, and where it went. Recorded when the operation that changes it starts, and confirmed
/// or rolled back with its outcome.
/// </summary>
public sealed class ServerMod
{
    public const int MaxIdLength = 64;
    public const int MaxNameLength = 128;
    public const int MaxVersionLength = 64;
    public const int MaxPathLength = 200;
    public const int MaxRequires = 32;

    private ServerMod()
    {
        // For EF Core materialization.
        ProjectId = null!;
        VersionId = null!;
        Name = null!;
        VersionNumber = null!;
        Path = null!;
        Hash = null!;
        Requires = null!;
    }

    private ServerMod(ServerId serverId, ModSource source, string projectId, string versionId, string name, string versionNumber, string path,
        string hash, long size, bool dependency, IReadOnlyList<string> requires, OperationId operation, DateTimeOffset now)
    {
        Id = ServerModId.New();
        ServerId = serverId;
        Source = source;
        ProjectId = projectId;
        VersionId = versionId;
        Name = name;
        VersionNumber = versionNumber;
        Path = path;
        Hash = hash;
        Size = size;
        Dependency = dependency;
        Requires = requires;
        State = ServerModState.Installing;
        OperationId = operation;
        InstalledAt = now;
        UpdatedAt = now;
    }

    public ServerModId Id { get; private set; }

    public ServerId ServerId { get; private set; }

    public ModSource Source { get; private set; }

    /// <summary>The catalog's project (one mod or plugin, any version).</summary>
    public string ProjectId { get; private set; }

    /// <summary>The catalog's version of it that is installed.</summary>
    public string VersionId { get; private set; }

    public string Name { get; private set; }

    /// <summary>The version as its authors name it ("0.18.1+mc1.21.8").</summary>
    public string VersionNumber { get; private set; }

    /// <summary>The file in the server's folder (<c>mods/lithium-fabric-0.18.1.jar</c>).</summary>
    public string Path { get; private set; }

    /// <summary>The catalog's checksum of the file, lowercase hex: SHA-512 (Modrinth's, how its updates are looked up) or SHA-1 (CurseForge's).</summary>
    public string Hash { get; private set; }

    public long Size { get; private set; }

    /// <summary>Installed because another mod needs it, not asked for itself.</summary>
    public bool Dependency { get; private set; }

    /// <summary>The projects this version needs (installed with it).</summary>
    public IReadOnlyList<string> Requires { get; private set; }

    public ServerModState State { get; private set; }

    /// <summary>The operation changing it, while one does.</summary>
    public OperationId? OperationId { get; private set; }

    public DateTimeOffset InstalledAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A file an operation is about to put on the server.</summary>
    public static ServerMod Installing(ServerId serverId, ModSource source, string projectId, string versionId, string name, string versionNumber,
        string path, string hash, long size, bool dependency, IReadOnlyList<string> requires, OperationId operation, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(requires);
        return new ServerMod(serverId, source, Cut(projectId, MaxIdLength), Cut(versionId, MaxIdLength), Cut(name, MaxNameLength),
            Cut(versionNumber, MaxVersionLength), path, hash, size, dependency, [.. requires.Take(MaxRequires).Select(r => Cut(r, MaxIdLength))], operation,
            now);
    }

    /// <summary>The same mod, installed, on a copy of its server (a clone's, 1.0.17) that has its file.</summary>
    public ServerMod CopyFor(ServerId serverId, DateTimeOffset now)
    {
        var copy = new ServerMod(serverId, Source, ProjectId, VersionId, Name, VersionNumber, Path, Hash, Size, Dependency, [.. Requires], default, now);
        copy.Installed(now);
        return copy;
    }

    /// <summary>An operation is deleting it, or putting another version in its place.</summary>
    public void Removing(OperationId operation, DateTimeOffset now)
    {
        State = ServerModState.Removing;
        OperationId = operation;
        UpdatedAt = now;
    }

    /// <summary>The operation that put it on the server succeeded.</summary>
    public void Installed(DateTimeOffset now)
    {
        State = ServerModState.Installed;
        OperationId = null;
        UpdatedAt = now;
    }

    /// <summary>The operation that would have deleted it did not: it is still there.</summary>
    public void Kept(DateTimeOffset now) => Installed(now);

    /// <summary>Asked for by a user now, though it came as another's dependency.</summary>
    public void Chosen(DateTimeOffset now)
    {
        Dependency = false;
        UpdatedAt = now;
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}
