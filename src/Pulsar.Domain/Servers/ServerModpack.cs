using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

public enum ServerModpackState
{
    /// <summary>An operation is installing it.</summary>
    Installing,
    Installed,
}

/// <summary>
/// A modpack from a mod catalog on a server (Pulsar 1.0.9; docs/architecture/02, "Mods"): the version of its project the
/// panel chose, and what it runs on. Recorded when the operation that installs it starts; it replaces the server's
/// previous pack if that operation succeeds, and is dropped if it does not.
/// </summary>
public sealed class ServerModpack
{
    public const int MaxIdLength = 64;
    public const int MaxNameLength = 128;
    public const int MaxVersionLength = 64;
    public const int MaxTypeLength = 64;

    private ServerModpack()
    {
        // For EF Core materialization.
        ProjectId = null!;
        VersionId = null!;
        Name = null!;
        VersionNumber = null!;
        ServerType = null!;
        Loader = null!;
        GameVersion = null!;
    }

    private ServerModpack(ServerId serverId, ModSource source, string projectId, string versionId, string name, string versionNumber, string serverType,
        string loader, string gameVersion, OperationId operation, DateTimeOffset now)
    {
        Id = ServerModpackId.New();
        ServerId = serverId;
        Source = source;
        ProjectId = projectId;
        VersionId = versionId;
        Name = name;
        VersionNumber = versionNumber;
        ServerType = serverType;
        Loader = loader;
        GameVersion = gameVersion;
        State = ServerModpackState.Installing;
        OperationId = operation;
        InstalledAt = now;
        UpdatedAt = now;
    }

    public ServerModpackId Id { get; private set; }

    public ServerId ServerId { get; private set; }

    public ModSource Source { get; private set; }

    /// <summary>The catalog's project (the pack, any version).</summary>
    public string ProjectId { get; private set; }

    /// <summary>The catalog's version of it.</summary>
    public string VersionId { get; private set; }

    public string Name { get; private set; }

    public string VersionNumber { get; private set; }

    /// <summary>The option of the manifest's server type variable it runs on (<c>fabric</c>).</summary>
    public string ServerType { get; private set; }

    /// <summary>The catalog's loader it is made for (<c>fabric</c>).</summary>
    public string Loader { get; private set; }

    /// <summary>The game version it is made for.</summary>
    public string GameVersion { get; private set; }

    /// <summary>The loader's version its index names, once the node reported it.</summary>
    public string? LoaderVersion { get; private set; }

    public ServerModpackState State { get; private set; }

    /// <summary>The operation installing it, while one does.</summary>
    public OperationId? OperationId { get; private set; }

    public DateTimeOffset InstalledAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A pack an operation is about to install.</summary>
    public static ServerModpack Installing(ServerId serverId, ModSource source, string projectId, string versionId, string name, string versionNumber,
        string serverType, string loader, string gameVersion, OperationId operation, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);
        ArgumentException.ThrowIfNullOrEmpty(serverType);
        ArgumentException.ThrowIfNullOrEmpty(loader);
        ArgumentException.ThrowIfNullOrEmpty(gameVersion);
        return new ServerModpack(serverId, source, Cut(projectId, MaxIdLength), Cut(versionId, MaxIdLength), Cut(name, MaxNameLength),
            Cut(versionNumber, MaxVersionLength), Cut(serverType, MaxTypeLength), Cut(loader, MaxTypeLength), Cut(gameVersion, MaxVersionLength), operation,
            now);
    }

    /// <summary>The same pack, installed, on a copy of its server (a clone's, 1.0.17) that has its files.</summary>
    public ServerModpack CopyFor(ServerId serverId, DateTimeOffset now)
    {
        var copy = new ServerModpack(serverId, Source, ProjectId, VersionId, Name, VersionNumber, ServerType, Loader, GameVersion, default, now);
        copy.Installed(LoaderVersion, now);
        return copy;
    }

    /// <summary>The operation that installed it succeeded, with the loader version the pack names (null: not reported).</summary>
    public void Installed(string? loaderVersion, DateTimeOffset now)
    {
        LoaderVersion = loaderVersion is null ? null : Cut(loaderVersion, MaxVersionLength);
        State = ServerModpackState.Installed;
        OperationId = null;
        InstalledAt = now;
        UpdatedAt = now;
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}
