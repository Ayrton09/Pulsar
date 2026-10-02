using System.Text.Json.Serialization;

namespace Pulsar.Manifests;

/// <summary>
/// A game manifest (docs/architecture/04): what a game needs installed, how it starts, when it is ready and how it
/// stops. Declarative and closed: only the typed steps below exist, never shell or scripts. Parsed and validated by
/// <see cref="ManifestParser"/>, identically on the panel and on the agent. Its JSON Schema, for editors and community
/// CI, is <c>schemas/manifest.v1.schema.json</c>.
/// </summary>
public sealed record GameManifest(
    int SchemaVersion,
    string Key,
    string Version,
    string Name,
    IReadOnlyList<string> Platforms,
    IReadOnlyDictionary<string, RuntimeDefinition> Runtime,
    ReadinessDefinition Readiness,
    StopDefinition Stop,
    string? Description = null,
    IReadOnlyList<string>? Authors = null,
    RequirementsDefinition? Requirements = null,
    IReadOnlyList<VariableDefinition>? Variables = null,
    IReadOnlyList<PortDefinition>? Ports = null,
    InstallDefinition? Install = null,
    InstallDefinition? Update = null,
    IReadOnlyList<ConfigFileDefinition>? ConfigFiles = null,
    BackupDefinition? Backup = null)
{
    /// <summary>Optional editor hint (<c>"$schema"</c>); ignored.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public IReadOnlyList<VariableDefinition> VariableList => Variables ?? [];

    public IReadOnlyList<PortDefinition> PortList => Ports ?? [];

    public IReadOnlyList<ConfigFileDefinition> ConfigFileList => ConfigFiles ?? [];
}

/// <summary>What a node should have for the game; shown when creating a server, not enforced.</summary>
public sealed record RequirementsDefinition(int? MinDiskGb = null, int? MinMemoryMb = null);

#pragma warning disable CA1720 // The names are the manifest's JSON values ("string", "integer").
public enum VariableType
{
    String,
    Integer,
    Boolean,
    Enum,
}
#pragma warning restore CA1720

/// <summary>A value users can set per server (docs/architecture/04, "Variables").</summary>
public sealed record VariableDefinition(
    string Key,
    string Label,
    VariableType Type,
    System.Text.Json.JsonElement? Default = null,
    bool Required = false,
    bool Secret = false,
    bool UserEditable = true,
    string? Pattern = null,
    int? MaxLength = null,
    long? Min = null,
    long? Max = null,
    IReadOnlyList<EnumOption>? Options = null,
    string? Description = null);

/// <summary>One choice of an <see cref="VariableType.Enum"/> variable; <see cref="Map"/> gives its sub-values.</summary>
public sealed record EnumOption(string Value, string Label, IReadOnlyDictionary<string, string>? Map = null);

public enum PortProtocol
{
    Udp,
    Tcp,
}

/// <summary>
/// A port the game listens on. Each server gets one of its node's allocations (an <c>ip:port</c> pair reserved for
/// both protocols) per port; arguments reach it through <c>{{port.&lt;name&gt;}}</c> and <c>{{port.&lt;name&gt;.ip}}</c>.
/// </summary>
/// <param name="Default">The port tried first when the server is created.</param>
/// <param name="Required">An optional port (CSTV, say) can be left without an allocation.</param>
public sealed record PortDefinition(
    string Name,
    string Label,
    IReadOnlyList<PortProtocol> Protocols,
    int Default,
    bool Required = true,
    string? Description = null);

public sealed record InstallDefinition(IReadOnlyList<InstallStep> Steps);

/// <summary>Install and update steps: a closed set (docs/architecture/04, "Install/update steps").</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(SteamCmdStep), "steamcmd")]
[JsonDerivedType(typeof(EnsureDirectoryStep), "ensureDirectory")]
[JsonDerivedType(typeof(WriteFileIfMissingStep), "writeFileIfMissing")]
[JsonDerivedType(typeof(TestGameServerStep), "testGameServer")]
public abstract record InstallStep;

/// <summary>
/// Installs or updates a Steam app into the server directory with SteamCMD (anonymous login only). Run by the agent
/// as the server's own account.
/// </summary>
/// <param name="Beta">A beta branch (<c>-beta</c>).</param>
/// <param name="BetaPasswordVar">Secret string variable holding the branch password (<c>-betapassword</c>).</param>
/// <param name="Validate">Verify every file against Steam on every run (slow); an update operation can ask for it too.</param>
public sealed record SteamCmdStep(
    int AppId,
    string Login = SteamCmdStep.AnonymousLogin,
    string? Beta = null,
    string? BetaPasswordVar = null,
    bool Validate = false) : InstallStep
{
    public const string AnonymousLogin = "anonymous";
}

/// <summary>Creates a directory (relative to the server directory) if it does not exist.</summary>
public sealed record EnsureDirectoryStep(string Path) : InstallStep;

/// <summary>Writes <see cref="Content"/> (literal text, no placeholders) to a file that does not exist yet.</summary>
public sealed record WriteFileIfMissingStep(string Path, string Content) : InstallStep;

/// <summary>
/// Copies Pulsar's test game server into <see cref="Path"/>. Only for the <c>pulsar-test</c> manifest, and only agents
/// started with a test game server directory accept it (development and CI).
/// </summary>
public sealed record TestGameServerStep(string Path) : InstallStep;

/// <summary>How the game runs on one platform (<c>windows-x64</c>).</summary>
public sealed record RuntimeDefinition(
    string Executable,
    ConsoleDefinition Console,
    IReadOnlyList<ArgumentTemplate>? Arguments = null,
    string? WorkingDirectory = null,
    bool AllowExtraArguments = false,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public IReadOnlyList<ArgumentTemplate> ArgumentList => Arguments ?? [];
}

/// <summary>One argv element (text with placeholders) or a conditional group of them.</summary>
[JsonConverter(typeof(ArgumentTemplateConverter))]
public abstract record ArgumentTemplate;

public sealed record ArgumentText(string Text) : ArgumentTemplate;

/// <summary>
/// <c>{ "if": "var.x", "then": [...] }</c>: included when the variable is true or a non-empty string, or, for
/// <c>port.&lt;name&gt;</c>, when that optional port has an allocation.
/// </summary>
public sealed record ArgumentCondition(string If, IReadOnlyList<ArgumentTemplate> Then) : ArgumentTemplate;

public enum ManifestConsoleMode
{
    Pty,
    Pipes,

    [JsonStringEnumMemberName("logfile+rcon")]
    LogfileRcon,
}

public enum ManifestConsoleEncoding
{
    [JsonStringEnumMemberName("utf-8")]
    Utf8,
    Oem,
    Ansi,
}

/// <param name="LogFile">For <c>logfile+rcon</c>: the log the game writes its console to (relative path).</param>
/// <param name="Rcon">For <c>logfile+rcon</c>: how the agent sends commands.</param>
public sealed record ConsoleDefinition(
    ManifestConsoleMode Mode,
    ManifestConsoleEncoding Encoding = ManifestConsoleEncoding.Utf8,
    string? InputNewline = null,
    IReadOnlyList<string>? SensitiveCommandPrefixes = null,
    string? LogFile = null,
    RconDefinition? Rcon = null);

public enum RconProtocol
{
    Source,
}

/// <summary>
/// Remote console the agent keeps (docs/architecture/04, "Console"). The agent generates a random password per server;
/// the manifest passes it to the game with <c>{{rcon.password}}</c>.
/// </summary>
/// <param name="Port">Name of the (TCP) port the game serves RCON on.</param>
public sealed record RconDefinition(RconProtocol Protocol, string Port);

/// <summary>When a starting server counts as running (docs/architecture/04, "Readiness and stop").</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LogPatternReadiness), "logPattern")]
[JsonDerivedType(typeof(ProcessAliveReadiness), "processAlive")]
[JsonDerivedType(typeof(RconReadiness), "rcon")]
public abstract record ReadinessDefinition;

/// <summary>A line of output matches <see cref="Pattern"/> (non-backtracking regex).</summary>
public sealed record LogPatternReadiness(string Pattern, int TimeoutSeconds) : ReadinessDefinition;

/// <summary>The process is still alive <see cref="GraceSeconds"/> after starting.</summary>
public sealed record ProcessAliveReadiness(int GraceSeconds) : ReadinessDefinition;

/// <summary>The first RCON command that succeeds.</summary>
public sealed record RconReadiness(int TimeoutSeconds) : ReadinessDefinition;

public sealed record StopDefinition(IReadOnlyList<StopStep> Steps, int TimeoutSeconds);

/// <summary>Graceful stop steps, run in order; after <see cref="StopDefinition.TimeoutSeconds"/> the agent kills the tree.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConsoleCommandStop), "consoleCommand")]
[JsonDerivedType(typeof(RconCommandStop), "rconCommand")]
[JsonDerivedType(typeof(CtrlCStop), "ctrlC")]
[JsonDerivedType(typeof(WaitStop), "wait")]
public abstract record StopStep;

public sealed record ConsoleCommandStop(string Command) : StopStep;

public sealed record RconCommandStop(string Command) : StopStep;

public sealed record CtrlCStop : StopStep;

public sealed record WaitStop(int Seconds) : StopStep;

public enum ConfigFileFormat
{
    /// <summary><c>cvar "value"</c> per line (Source engine <c>.cfg</c>).</summary>
    [JsonStringEnumMemberName("source-cfg")]
    SourceCfg,

    /// <summary><c>key=value</c> per line.</summary>
    Properties,
}

public enum ConfigFileMode
{
    /// <summary>Only the declared keys change; the rest of the file (users' own lines) stays.</summary>
    Patch,

    /// <summary>The file is written entirely by Pulsar; manual edits are lost at the next start.</summary>
    Managed,
}

/// <summary>
/// A configuration file the agent writes before every start (docs/architecture/04, "Configuration files"). Values are
/// templates; each format's encoder refuses what would break out of a line or a value.
/// </summary>
public sealed record ConfigFileDefinition(string Path, ConfigFileFormat Format, ConfigFileMode Mode, IReadOnlyDictionary<string, string> Set);

/// <summary>What a backup contains: globs relative to the server directory (Phase 7).</summary>
public sealed record BackupDefinition(IReadOnlyList<string>? Include = null, IReadOnlyList<string>? Exclude = null);
