using System.Text.Json.Serialization;

namespace Pulsar.Manifests;

/// <summary>
/// A game manifest (docs/architecture/04): what a game needs installed, how it starts, when it is ready and how it
/// stops. Declarative and closed: only the typed steps below exist, never shell or scripts. Parsed and validated by
/// <see cref="ManifestParser"/>, identically on the panel and on the agent.
/// </summary>
/// <remarks>
/// Phase 3 subset: identity, variables, install steps, runtime, readiness and stop. Ports, configuration files and
/// backups arrive with the parts of Pulsar that use them; until then a manifest containing them is refused.
/// </remarks>
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
    IReadOnlyList<VariableDefinition>? Variables = null,
    InstallDefinition? Install = null)
{
    /// <summary>Optional editor hint (<c>"$schema"</c>); ignored.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public IReadOnlyList<VariableDefinition> VariableList => Variables ?? [];
}

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

public sealed record InstallDefinition(IReadOnlyList<InstallStep> Steps);

/// <summary>Install and update steps: a closed set (docs/architecture/04, "Install/update steps").</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(EnsureDirectoryStep), "ensureDirectory")]
[JsonDerivedType(typeof(TestGameServerStep), "testGameServer")]
public abstract record InstallStep;

/// <summary>Creates a directory (relative to the server directory) if it does not exist.</summary>
public sealed record EnsureDirectoryStep(string Path) : InstallStep;

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

/// <summary><c>{ "if": "var.x", "then": [...] }</c>: included when the variable is true or a non-empty string.</summary>
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

public sealed record ConsoleDefinition(
    ManifestConsoleMode Mode,
    ManifestConsoleEncoding Encoding = ManifestConsoleEncoding.Utf8,
    string? InputNewline = null,
    IReadOnlyList<string>? SensitiveCommandPrefixes = null);

/// <summary>When a starting server counts as running (docs/architecture/04, "Readiness and stop").</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LogPatternReadiness), "logPattern")]
[JsonDerivedType(typeof(ProcessAliveReadiness), "processAlive")]
public abstract record ReadinessDefinition;

/// <summary>A line of output matches <see cref="Pattern"/> (non-backtracking regex).</summary>
public sealed record LogPatternReadiness(string Pattern, int TimeoutSeconds) : ReadinessDefinition;

/// <summary>The process is still alive <see cref="GraceSeconds"/> after starting.</summary>
public sealed record ProcessAliveReadiness(int GraceSeconds) : ReadinessDefinition;

public sealed record StopDefinition(IReadOnlyList<StopStep> Steps, int TimeoutSeconds);

/// <summary>Graceful stop steps, run in order; after <see cref="StopDefinition.TimeoutSeconds"/> the agent kills the tree.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConsoleCommandStop), "consoleCommand")]
[JsonDerivedType(typeof(CtrlCStop), "ctrlC")]
[JsonDerivedType(typeof(WaitStop), "wait")]
public abstract record StopStep;

public sealed record ConsoleCommandStop(string Command) : StopStep;

public sealed record CtrlCStop : StopStep;

public sealed record WaitStop(int Seconds) : StopStep;
