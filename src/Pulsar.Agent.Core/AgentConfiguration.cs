using System.Text.Json;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core;

/// <summary>
/// The agent's state directory (docs/architecture/03): <c>%ProgramData%\Pulsar\Agent</c> by default, readable only by
/// SYSTEM and Administrators.
/// </summary>
public sealed class AgentPaths(string root)
{
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pulsar", "Agent");

    public string Root { get; } = Path.GetFullPath(root);

    public string ConfigDirectory => Path.Combine(Root, "config");

    public string ConfigFile => Path.Combine(ConfigDirectory, "agent.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>One file per server: its runtime spec and whether it should be running.</summary>
    public string ServersStateDirectory => Path.Combine(Root, "state", "servers");

    /// <summary>Operation results the panel has not received yet.</summary>
    public string OutboxDirectory => Path.Combine(Root, "state", "outbox");

    /// <summary>The logins SteamCMD cached for each Steam account (Phase 13), encrypted.</summary>
    public string SteamLoginsDirectory => Path.Combine(Root, "state", "steam-logins");

    /// <summary>The verified SteamCMD bootstrapper each server gets a copy of.</summary>
    public string SteamCmdDirectory => Path.Combine(Root, "steamcmd");

    /// <summary>Rotated console logs, one folder per server (outside the server directory, which its account controls).</summary>
    public string ConsoleLogsDirectory => Path.Combine(Root, "console-logs");

    /// <summary>Backups, one folder per server, unless enrollment chose another place (<see cref="AgentConfig.BackupsRoot"/>).</summary>
    public string BackupsDirectory => Path.Combine(Root, "backups");

    /// <summary>The folders the agent itself makes in <see cref="Root"/>: what securing it resets (nothing else in it).</summary>
    public IReadOnlyList<string> OwnFolders =>
        [ConfigDirectory, LogsDirectory, Path.Combine(Root, "state"), SteamCmdDirectory, ConsoleLogsDirectory, BackupsDirectory];
}

/// <summary>Everything the agent needs to reach and trust its panel, written at enrollment.</summary>
/// <param name="KeyCreatedAt">When the current node key was registered (null: at enrollment). Drives automatic rotation.</param>
/// <param name="PendingRotation">A new key the panel registered but the agent has not authenticated with yet.</param>
/// <param name="BackupsRoot">Where backups go, chosen at enrollment (null: inside the state directory).</param>
public sealed record AgentConfig(
    Uri Panel,
    Guid NodeId,
    Guid CredentialId,
    NodeKeyReference Key,
    IReadOnlyList<PanelKey> PanelKeys,
    string? ServersRoot,
    DateTimeOffset EnrolledAt,
    bool Development,
    DateTimeOffset? KeyCreatedAt = null,
    PendingRotation? PendingRotation = null,
    string? BackupsRoot = null)
{
    public DateTimeOffset CurrentKeyCreatedAt => KeyCreatedAt ?? EnrolledAt;

    /// <summary>
    /// Where server directories go: the one chosen at enrollment, otherwise <c>%SystemDrive%\PulsarServers</c> (inside the
    /// data directory for a development agent).
    /// </summary>
    public string ServersRootOrDefault(AgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ServersRoot ?? (Development
            ? Path.Combine(paths.Root, "servers")
            : Path.Combine((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + Path.DirectorySeparatorChar, "PulsarServers"));
    }

    /// <summary>Where backups go: out of the servers' reach (only SYSTEM and Administrators can open it).</summary>
    public string BackupsRootOrDefault(AgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return BackupsRoot ?? paths.BackupsDirectory;
    }
}

/// <summary>
/// The middle of a node key rotation: written before the agent first authenticates with the new key, so a crash at any
/// point leaves either the old credential or the new one usable (docs/architecture/03, "Rotation").
/// </summary>
public sealed record PendingRotation(NodeKeyReference Key, Guid CredentialId, DateTimeOffset CreatedAt);

/// <summary>Restricts the agent's folders to the accounts that run or administer the agent.</summary>
public interface IAgentDirectorySecurity
{
    /// <summary>
    /// The state directory: the folder itself, and the agent's own folders in it with everything they hold, are reset;
    /// anything else in it (a development agent's servers folder) keeps its own rules.
    /// </summary>
    /// <param name="directory">Created if missing.</param>
    /// <param name="ownFolders">The agent's folders in it (<see cref="AgentPaths.OwnFolders"/>).</param>
    /// <param name="development">Also grants the current user (development installs run without elevation).</param>
    void SecureState(string directory, IReadOnlyCollection<string> ownFolders, bool development);

    /// <summary>
    /// A folder only the agent uses, chosen at enrollment (a backups root). One that already holds something is refused
    /// unless it is already restricted to the agent's accounts: someone else's files there are never rewritten.
    /// </summary>
    /// <param name="directory">Created if missing.</param>
    /// <param name="development">Also grants the current user (development installs run without elevation).</param>
    void SecureFolder(string directory, bool development);
}

/// <summary>Reads and atomically replaces <c>config\agent.json</c>.</summary>
public sealed class AgentConfigStore(AgentPaths paths)
{
    private static readonly JsonSerializerOptions s_options = new(ProtocolJson.Options) { WriteIndented = true };

    public AgentPaths Paths { get; } = paths;

    /// <summary>The saved configuration, or null when the agent has not been enrolled.</summary>
    public AgentConfig? Load()
    {
        if (!File.Exists(Paths.ConfigFile))
        {
            return null;
        }

        using var stream = File.OpenRead(Paths.ConfigFile);
        return JsonSerializer.Deserialize<AgentConfig>(stream, s_options)
            ?? throw new InvalidDataException($"{Paths.ConfigFile} is empty.");
    }

    public void Save(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(Paths.ConfigDirectory);
        // Write next to the target and swap, so a crash never leaves a half-written configuration.
        var temporary = Paths.ConfigFile + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(config, s_options));
        File.Move(temporary, Paths.ConfigFile, overwrite: true);
    }
}
