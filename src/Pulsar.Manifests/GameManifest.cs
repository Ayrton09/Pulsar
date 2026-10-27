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
    BackupDefinition? Backup = null,
    QueryDefinition? Query = null,
    IReadOnlyList<AddonDefinition>? Addons = null,
    CommandsDefinition? Commands = null,
    IReadOnlyList<ModSourceDefinition>? Mods = null)
{
    /// <summary>Optional editor hint (<c>"$schema"</c>); ignored.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public IReadOnlyList<VariableDefinition> VariableList => Variables ?? [];

    public IReadOnlyList<PortDefinition> PortList => Ports ?? [];

    public IReadOnlyList<ConfigFileDefinition> ConfigFileList => ConfigFiles ?? [];

    public IReadOnlyList<AddonDefinition> AddonList => Addons ?? [];

    public IReadOnlyList<ModSourceDefinition> ModList => Mods ?? [];
}

/// <summary>
/// A catalog of mods the panel installs files from into the server (Pulsar 1.0.9; docs/architecture/04, "Mods"):
/// Modrinth for Minecraft. What a server can take depends on its type: the type variable's options are keys of
/// <see cref="Types"/> (an option without one takes nothing from the catalog), or (Pulsar 1.0.14) the addon it has
/// among <see cref="Types"/>' keys (Oxide or Carbon for uMod's plugins).
/// </summary>
/// <param name="Source">The catalog: <c>modrinth</c>, <c>curseforge</c> or <c>umod</c>.</param>
/// <param name="VersionVar">
/// The variable holding the game's version, the catalog's files are filtered by (when the install did not report it);
/// null for a catalog whose files are not made per game version (uMod).
/// </param>
/// <param name="TypeVar">The enum variable holding the server's type; null when <paramref name="AddonTypes"/>.</param>
/// <param name="Modpacks">The catalog's modpacks install on the game too (null: they do not).</param>
/// <param name="AddonTypes">The server's type is the addon it has among <paramref name="Types"/>' keys (Pulsar 1.0.14).</param>
/// <param name="Community">The catalog's name for the game: uMod's game (<c>rust</c>), Thunderstore's community (<c>valheim</c>).</param>
/// <param name="WhileRunning">
/// The game picks the files up while it runs (Oxide and Carbon load and unload plugins as they come and go): they are
/// installed and removed without stopping it (Pulsar 1.0.14).
/// </param>
/// <param name="Category">Thunderstore's category the searches keep to (<c>server-side</c>); any when null.</param>
public sealed record ModSourceDefinition(string Source, IReadOnlyDictionary<string, ModTargetDefinition> Types, string? VersionVar = null,
    string? TypeVar = null, ModpacksDefinition? Modpacks = null, bool AddonTypes = false, string? Community = null, bool WhileRunning = false,
    string? Category = null)
{
    public const string Modrinth = "modrinth";

    /// <summary>CurseForge (Pulsar 1.0.9): its API needs the panel's administrator's own key.</summary>
    public const string CurseForge = "curseforge";

    /// <summary>uMod (Pulsar 1.0.14): Oxide's and Carbon's plugins, one C# file each, from umod.org.</summary>
    public const string Umod = "umod";

    /// <summary>Thunderstore (Pulsar 1.0.14): packages for BepInEx, a zip each, from thunderstore.io.</summary>
    public const string Thunderstore = "thunderstore";

    /// <summary>Whether the catalog's files are made per game version (Modrinth's and CurseForge's are).</summary>
    public bool Versioned => Source is Modrinth or CurseForge;
}

/// <summary>
/// Modpacks of the catalog (Pulsar 1.0.9; docs/architecture/04, "Modpacks"): a pack runs on the type whose mods it is
/// made of (a type of project type <c>mod</c> listing its loader), at the game version it names. Installing one runs the
/// game's install steps with those values in <see cref="ModSourceDefinition.TypeVar"/> and
/// <see cref="ModSourceDefinition.VersionVar"/>, and the loader version its index names in <see cref="LoaderVar"/>.
/// </summary>
/// <param name="LoaderVar">The string variable holding the loader's version.</param>
public sealed record ModpacksDefinition(string LoaderVar);

/// <summary>What a server of one type takes from the catalog, and where it goes.</summary>
/// <param name="ProjectType">The catalog's kind of project: <c>mod</c> or <c>plugin</c>.</param>
/// <param name="Loaders">
/// The catalog's loaders a file must be for, any of them (<c>fabric</c>; <c>paper</c>, <c>spigot</c>, <c>bukkit</c>);
/// none for a catalog that names none (uMod).
/// </param>
/// <param name="Folder">The server's folder the files go into (<c>mods</c>, <c>plugins</c>; <c>oxide/plugins</c> from Pulsar 1.0.14).</param>
/// <param name="Extensions">What the files may end with (Pulsar 1.0.14; <c>.cs</c> for uMod's plugins); <c>.jar</c> when not given.</param>
/// <param name="Layout">
/// How a package's zip goes into <paramref name="Folder"/> (Pulsar 1.0.14): <c>bepinex</c>, BepInEx's folder, a package's
/// plugins and patchers each in a folder named after it, its configuration only where there is none.
/// </param>
public sealed record ModTargetDefinition(string ProjectType, string Folder, IReadOnlyList<string>? Loaders = null, IReadOnlyList<string>? Extensions = null,
    string? Layout = null)
{
    /// <summary>A package unpacked into BepInEx's folder (Thunderstore's packages).</summary>
    public const string BepInExLayout = "bepinex";

    public IReadOnlyList<string> LoaderList => Loaders ?? [];

    public IReadOnlyList<string> ExtensionList => Extensions ?? [".jar"];
}

/// <summary>
/// Console commands the panel sends for the game's players (Pulsar 1.0.9), through the server's console (its input or
/// RCON, as the console section says).
/// </summary>
/// <param name="Broadcast">
/// Tells everyone playing a message, with <c>{{message}}</c> where the message goes (<c>say {{message}}</c>): the warnings
/// before an automatic update or a scheduled restart. The panel keeps letters, digits, spaces and <c>. , ! ? : ' ( ) -</c>
/// of a message and drops anything else, so it can never end the command or start another.
/// </param>
/// <param name="MessageSpaces">What stands for a space in the message, for a game whose command takes a single word (Palworld's).</param>
/// <param name="Players">What the panel does to one player at a time (Pulsar 1.0.11).</param>
public sealed record CommandsDefinition(string? Broadcast = null, string? MessageSpaces = null, PlayerCommandsDefinition? Players = null);

/// <summary>
/// The commands for one player (Pulsar 1.0.11; docs/architecture/04, "Players"), each with <c>{{player}}</c> where the
/// player goes (<c>kick {{player}}</c>). The panel never cleans a player's name: one with anything but letters, digits and
/// <c>_ - .</c> (and spaces, when <see cref="Spaces"/>) is refused, so it can never end the command or start another, and a
/// command never reaches a player it was not meant for.
/// </summary>
/// <param name="Kick">Puts the player out of the game.</param>
/// <param name="Ban">Keeps the player out for good.</param>
/// <param name="Unban">Lets a banned player in again.</param>
/// <param name="WhitelistAdd">Adds the player to the list of who may join (Minecraft's whitelist, Bedrock's allowlist).</param>
/// <param name="WhitelistRemove">Takes the player off that list.</param>
/// <param name="AdminAdd">Makes the player the game's operator or admin, who runs its commands in game.</param>
/// <param name="AdminRemove">Takes that away.</param>
/// <param name="ById">The commands take an id (a Steam ID), not the name the game's query lists.</param>
/// <param name="Spaces">A name may have spaces: the commands' arguments do not end at one (they are quoted, or end at another character).</param>
/// <param name="Lists">Where the game keeps who is banned, whitelisted or admin, for the panel to show.</param>
public sealed record PlayerCommandsDefinition(
    string? Kick = null,
    string? Ban = null,
    string? Unban = null,
    string? WhitelistAdd = null,
    string? WhitelistRemove = null,
    string? AdminAdd = null,
    string? AdminRemove = null,
    bool ById = false,
    bool Spaces = false,
    PlayerListsDefinition? Lists = null);

/// <summary>The game's files that list who is banned, on its whitelist, or its admins.</summary>
public sealed record PlayerListsDefinition(PlayerListFile? Banned = null, PlayerListFile? Whitelist = null, PlayerListFile? Admins = null);

/// <summary>A JSON file in the server's folder holding an array of objects, each naming a player in <see cref="Field"/> (Minecraft's <c>ops.json</c>).</summary>
/// <param name="File">The file, relative to the server's folder.</param>
/// <param name="Field">The objects' property holding the player.</param>
public sealed record PlayerListFile(string File, string Field);

/// <summary>What the panel can do to a player (<see cref="PlayerCommandsDefinition"/>).</summary>
public enum PlayerAction
{
    Kick,
    Ban,
    Unban,
    WhitelistAdd,
    WhitelistRemove,
    AdminAdd,
    AdminRemove,
}

/// <summary>A list of players the game keeps (<see cref="PlayerListsDefinition"/>).</summary>
public enum PlayerListKind
{
    Banned,
    Whitelist,
    Admins,
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

    /// <summary>A decimal number between <c>min</c> and <c>max</c> ("1.5"): the multipliers games take.</summary>
    Number,
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
    decimal? Min = null,
    decimal? Max = null,
    IReadOnlyList<EnumOption>? Options = null,
    string? Description = null,
    WorkshopList? Workshop = null,
    CurseForgeList? CurseForge = null,
    ReforgerWorkshopList? ReforgerWorkshop = null)
{
    /// <summary>Between the ids of a variable that lists Workshop items or catalog projects; null for any other variable.</summary>
    public string? ListSeparator => Workshop?.Separator ?? CurseForge?.Separator ?? ReforgerWorkshop?.Separator;
}

/// <summary>
/// A string variable that lists Steam Workshop items by id (Project Zomboid's <c>WorkshopItems</c>): the panel offers
/// an editor that looks the items up on Steam. The game downloads them itself.
/// </summary>
/// <param name="AppId">The Steam app whose Workshop the items belong to (the game's, not its dedicated server's).</param>
/// <param name="Separator">Between ids: <c>;</c> or <c>,</c>.</param>
public sealed record WorkshopList(long AppId, string Separator = ";");

/// <summary>
/// A string variable that lists CurseForge projects by id (Pulsar 1.0.14; ARK: Survival Ascended's mods): the panel
/// offers an editor that searches the game's mods on CurseForge and names them, with the administrator's key. The game
/// downloads them itself.
/// </summary>
/// <param name="GameId">The game on CurseForge (ARK: Survival Ascended is 83374).</param>
/// <param name="Separator">Between ids: <c>,</c> or <c>;</c>.</param>
public sealed record CurseForgeList(int GameId, string Separator = ",");

/// <summary>
/// A string variable that lists mods of Arma Reforger's Workshop by id (Pulsar 1.0.14; 16 hexadecimal digits,
/// <c>5965550F24A0C152</c>): the panel offers an editor that searches the Workshop and names them. The game downloads
/// them itself.
/// </summary>
/// <param name="Separator">Between ids: <c>,</c> or <c>;</c>.</param>
public sealed record ReforgerWorkshopList(string Separator = ",");

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
/// <see cref="Protocols"/> are the ones opened to the outside when Pulsar manages the node's firewall.
/// </summary>
/// <param name="Default">The port tried first when the server is created.</param>
/// <param name="Required">An optional port (CSTV, say) can be left without an allocation.</param>
/// <param name="Follows">A port the game opens at a fixed distance from another (Valheim: the game port + 1): the panel
/// gives it the allocation of the same address at that port, never one of its own choosing.</param>
/// <param name="Internal">Only the agent uses it, on the node (7 Days to Die's telnet console): never opened in the firewall.</param>
public sealed record PortDefinition(
    string Name,
    string Label,
    IReadOnlyList<PortProtocol> Protocols,
    int Default,
    bool Required = true,
    string? Description = null,
    PortFollow? Follows = null,
    bool Internal = false);

/// <summary>This port is always <see cref="Port"/>'s port number plus <see cref="Offset"/>, on the same address.</summary>
public sealed record PortFollow(string Port, int Offset);

public sealed record InstallDefinition(IReadOnlyList<InstallStep> Steps);

/// <summary>Install and update steps: a closed set (docs/architecture/04, "Install/update steps").</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(SteamCmdStep), "steamcmd")]
[JsonDerivedType(typeof(EnsureDirectoryStep), "ensureDirectory")]
[JsonDerivedType(typeof(WriteFileIfMissingStep), "writeFileIfMissing")]
[JsonDerivedType(typeof(TestGameServerStep), "testGameServer")]
[JsonDerivedType(typeof(MinecraftServerStep), "minecraftServer")]
[JsonDerivedType(typeof(MinecraftBedrockStep), "minecraftBedrock")]
[JsonDerivedType(typeof(CopyFileIfMissingStep), "copyFileIfMissing")]
[JsonDerivedType(typeof(GitHubReleaseStep), "githubRelease")]
[JsonDerivedType(typeof(DotnetRuntimeStep), "dotnetRuntime")]
public abstract record InstallStep;

/// <summary>
/// Installs or updates a Steam app into the server directory with SteamCMD. Run by the agent as the server's own
/// account.
/// </summary>
/// <param name="Login">
/// <c>anonymous</c>, or <c>account</c> for a dedicated server only an account that owns it may download: such a server
/// cannot be installed without a Steam account (Phase 13). A server with an account logs in with it either way.
/// </param>
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
    public const string AccountLogin = "account";

    public bool NeedsAccount => Login == AccountLogin;
}

/// <summary>Creates a directory (relative to the server directory) if it does not exist.</summary>
public sealed record EnsureDirectoryStep(string Path) : InstallStep;

/// <summary>Writes <see cref="Content"/> (literal text, no placeholders) to a file that does not exist yet.</summary>
public sealed record WriteFileIfMissingStep(string Path, string Content) : InstallStep;

/// <summary>
/// Copies a file of the server directory to <see cref="To"/> when that does not exist yet: a game's shipped settings
/// file as the server's own, which the game's updates then leave alone (7 Days to Die overwrites its
/// <c>serverconfig.xml</c>).
/// </summary>
public sealed record CopyFileIfMissingStep(string From, string To) : InstallStep;

/// <summary>
/// Copies Pulsar's test game server into <see cref="Path"/>. Only for the <c>pulsar-test</c> manifest, and only agents
/// started with a test game server directory accept it (development and CI).
/// </summary>
public sealed record TestGameServerStep(string Path) : InstallStep;

/// <summary>
/// Installs Minecraft: Java Edition's dedicated server as <c>server.jar</c>, from Mojang, and the Java runtime its
/// version asks for in <c>java/</c>, from Adoptium (Eclipse Temurin). The agent checks both against the checksums their
/// publishers list. Pulsar 1.0.9: a server type other than Mojang's own (<see cref="Types"/>): Paper or Purpur as
/// <c>server.jar</c>, Fabric's loader beside Mojang's jar (<c>fabric-server-launch.jar</c>), or Forge's or NeoForge's
/// installer run as the server's account, which leaves the Java arguments that start it in <see cref="ArgumentsFile"/>.
/// </summary>
/// <param name="VersionVar">A variable holding the version ("1.21.4", or "latest" for the latest release); latest without one.</param>
/// <param name="TypeVar">An enum variable whose options are <see cref="Types"/>; Mojang's server without one.</param>
/// <param name="LoaderVar">A variable holding the version of Fabric's loader, Forge or NeoForge; empty for the recommended one.</param>
public sealed record MinecraftServerStep(string? VersionVar = null, string? TypeVar = null, string? LoaderVar = null) : InstallStep
{
    public const string Latest = "latest";

    public const string Vanilla = "vanilla";
    public const string Paper = "paper";
    public const string Purpur = "purpur";
    public const string Fabric = "fabric";
    public const string Forge = "forge";
    public const string NeoForge = "neoforge";

    /// <summary>The server types the step installs.</summary>
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { Vanilla, Paper, Purpur, Fabric, Forge, NeoForge };

    /// <summary>The Java arguments that start a Forge or NeoForge server (its <c>win_args.txt</c>), for <c>java @file</c>.</summary>
    public const string ArgumentsFile = ".pulsar/minecraft-args.txt";
}

/// <summary>
/// Installs Minecraft: Bedrock Edition's dedicated server from Mojang (1.0.8): the Windows build of the chosen version,
/// unpacked into the server directory. Mojang publishes no checksum for it, so the agent takes it only from Mojang's
/// hosts and accepts its <c>bedrock_server.exe</c> only with a valid Authenticode signature of Microsoft's. An update
/// leaves the server's own settings files (<see cref="KeptFiles"/>) and its worlds as they are.
/// </summary>
/// <param name="VersionVar">A variable holding the version ("1.21.50.07", or "latest" for the latest release); latest without one.</param>
public sealed record MinecraftBedrockStep(string? VersionVar = null) : InstallStep
{
    public const string Latest = "latest";

    /// <summary>Files of the download that are the server's settings: written only when missing.</summary>
    public static readonly IReadOnlyList<string> KeptFiles = ["server.properties", "allowlist.json", "permissions.json", "packetlimitconfig.json"];
}

/// <summary>
/// Installs a game server published as a zip or .tar.gz asset of a GitHub release (Pulsar 1.0.12: TShock, Terraria's
/// server), as an addon is installed: found through GitHub's API, checked against the SHA-256 GitHub publishes for it,
/// unpacked into <see cref="Target"/> with the archive rules of addons. An update takes the latest release (or the one
/// with <see cref="Tag"/>) and removes the files of the previous one that the new one no longer has; nothing is
/// downloaded when the same asset is installed. What was installed is kept in <see cref="RecordDirectory"/>.
/// </summary>
/// <param name="Repo"><c>owner/name</c>.</param>
/// <param name="Asset">A regular expression the asset's file name must match (one asset of the release).</param>
/// <param name="Prerelease">Take the newest release even when it is marked as a pre-release.</param>
/// <param name="Target">The folder of the server it is unpacked into ("" for the server's own folder).</param>
public sealed record GitHubReleaseStep(string Repo, string Asset, string? Tag = null, bool Prerelease = false, string Target = "") : InstallStep
{
    /// <summary>Where the agent records what each such step installed (not an addon: the addons' records are elsewhere).</summary>
    public const string RecordDirectory = ".pulsar/releases";

    public GitHubReleaseSource Source => new(Repo, Asset, Tag, Prerelease);

    /// <summary>The name of the step's record: its repository's, lowercase, <c>/</c> as <c>_</c>.</summary>
    public string RecordKey => Repo.Replace('/', '_').ToLowerInvariant();
}

/// <summary>
/// Installs Microsoft's .NET runtime (<c>Microsoft.NETCore.App</c>) of <see cref="Channel"/> in the server's
/// <see cref="Folder"/> folder (Pulsar 1.0.12): its latest patch, from Microsoft's release metadata, the download checked
/// against the SHA-512 Microsoft lists. The game's .NET programs run on it whatever the node has installed: the agent
/// points <c>DOTNET_ROOT</c> there (the manifest's own environment cannot name <c>DOTNET_</c> variables). An update brings
/// it to the channel's latest patch; nothing is downloaded when that one is installed.
/// </summary>
/// <param name="Channel">The runtime's major version, as Microsoft names its channels: <c>9.0</c>.</param>
public sealed record DotnetRuntimeStep(string Channel) : InstallStep
{
    /// <summary>The server's folder the runtime goes in.</summary>
    public const string Folder = "dotnet";
}

/// <summary>How the game runs on one platform (<c>windows-x64</c>).</summary>
/// <param name="UserProfile">
/// A folder of the server the game takes for its user's (Pulsar 1.0.12): the agent sets <c>USERPROFILE</c>,
/// <c>APPDATA</c> and <c>LOCALAPPDATA</c> to it, so what the game keeps in its account's AppData (SCP: Secret
/// Laboratory's LabAPI plugins, say) is in the server's folder, reached by the file manager and backed up.
/// </param>
public sealed record RuntimeDefinition(
    string Executable,
    ConsoleDefinition Console,
    IReadOnlyList<ArgumentTemplate>? Arguments = null,
    string? WorkingDirectory = null,
    bool AllowExtraArguments = false,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? UserProfile = null)
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
/// <param name="Else">What goes instead when the condition does not hold (Rust: turn a feature off when its port is not assigned).</param>
public sealed record ArgumentCondition(string If, IReadOnlyList<ArgumentTemplate> Then, IReadOnlyList<ArgumentTemplate>? Else = null) : ArgumentTemplate;

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

/// <param name="LogFile">
/// For <c>logfile+rcon</c>, or <c>pipes</c> for a game that writes only there (Pulsar 1.0.12): the log the game writes its
/// console to (relative path). Its file name may hold one <c>*</c> (1.0.12): the newest file it matches that appeared
/// with the start (Space Engineers names its log by the time it started).
/// </param>
/// <param name="Rcon">For <c>logfile+rcon</c>: how the agent sends commands.</param>
/// <param name="PrivatePaths">
/// With GoldSrc's RCON (Pulsar 1.0.15): other files or folders the game writes its RCON commands to, password included
/// (HLDS's <c>logs</c> folder once logging is on). Like the console's log, they and what is in them are Pulsar's to keep.
/// </param>
public sealed record ConsoleDefinition(
    ManifestConsoleMode Mode,
    ManifestConsoleEncoding Encoding = ManifestConsoleEncoding.Utf8,
    string? InputNewline = null,
    IReadOnlyList<string>? SensitiveCommandPrefixes = null,
    string? LogFile = null,
    RconDefinition? Rcon = null,
    IReadOnlyList<string>? PrivatePaths = null);

public enum RconProtocol
{
    /// <summary>Source RCON (srcds, CS2, Rust, ARK, Palworld).</summary>
    Source,

    /// <summary>A line console over telnet with an optional password prompt (7 Days to Die).</summary>
    Telnet,

    /// <summary>
    /// GoldSrc's RCON over UDP (hlds: Counter-Strike 1.6, Half-Life; Pulsar 1.0.15). The game writes each command it receives
    /// to its console, password included: its console's log file is Pulsar's to read, never served as a file.
    /// </summary>
    [JsonStringEnumMemberName("goldsrc")]
    GoldSrc,
}

/// <summary>
/// Remote console the agent keeps (docs/architecture/04, "Console"). The agent generates a random password per server;
/// the manifest passes it to the game with <c>{{rcon.password}}</c>.
/// </summary>
/// <param name="Port">
/// Name of the port the game serves RCON on (over TCP). Declare only the protocols players need on it (UDP for Source
/// games): the firewall rules Pulsar manages open just those, and RCON stays reachable from the node only.
/// </param>
/// <param name="LogsCommands">
/// The game writes the commands it receives over RCON to its log file (CS2 does, srcds does not), or (Pulsar 1.0.12) to
/// its output (Soulmask): the console then shows the log instead of adding the agent's copy of each command, and adds
/// an answer only when the log does not show it (CS2 logs the answer to <c>status</c> but not to cvar queries).
/// </param>
public sealed record RconDefinition(RconProtocol Protocol, string Port, bool LogsCommands = false);

public enum QueryProtocol
{
    /// <summary>Valve's server queries (A2S_INFO, A2S_PLAYER) over UDP: Source games and most Steam games.</summary>
    A2s,

    /// <summary>Minecraft's Server List Ping over TCP.</summary>
    Minecraft,
}

/// <summary>
/// How the agent asks a running server for its name, map and players (docs/architecture/04, "Query"), on a required
/// port of the manifest that has the protocol's transport (UDP for A2S, TCP for Minecraft).
/// </summary>
public sealed record QueryDefinition(QueryProtocol Protocol, string Port);

/// <summary>When a starting server counts as running (docs/architecture/04, "Readiness and stop").</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LogPatternReadiness), "logPattern")]
[JsonDerivedType(typeof(ProcessAliveReadiness), "processAlive")]
[JsonDerivedType(typeof(RconReadiness), "rcon")]
[JsonDerivedType(typeof(QueryReadiness), "query")]
public abstract record ReadinessDefinition;

/// <summary>A line of output matches <see cref="Pattern"/> (non-backtracking regex).</summary>
public sealed record LogPatternReadiness(string Pattern, int TimeoutSeconds) : ReadinessDefinition;

/// <summary>The process is still alive <see cref="GraceSeconds"/> after starting.</summary>
public sealed record ProcessAliveReadiness(int GraceSeconds) : ReadinessDefinition;

/// <summary>The first RCON command that succeeds.</summary>
public sealed record RconReadiness(int TimeoutSeconds) : ReadinessDefinition;

/// <summary>The first answer to the manifest's query.</summary>
public sealed record QueryReadiness(int TimeoutSeconds) : ReadinessDefinition;

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

    /// <summary>
    /// Keys of one <c>[section]</c> of an Unreal Engine ini, as plain <c>Key=Value</c> lines or as the members of one
    /// <c>Struct=(Key=Value,...)</c> line (Palworld's <c>OptionSettings</c>).
    /// </summary>
    [JsonStringEnumMemberName("unreal-ini")]
    UnrealIni,

    /// <summary><c>key=value</c> per line, written as is (no escapes): Project Zomboid's <c>.ini</c>.</summary>
    [JsonStringEnumMemberName("key-value")]
    KeyValue,

    /// <summary>
    /// <c>&lt;property name="Key" value="Value"/&gt;</c> elements of an XML file (7 Days to Die's
    /// <c>serverconfig.xml</c>); values are escaped. Patch mode only; <c>section</c> names the root element.
    /// </summary>
    [JsonStringEnumMemberName("xml-properties")]
    XmlProperties,

    /// <summary>
    /// <c>key: value</c> per line, <c>#</c> starting a comment (SCP: Secret Laboratory's <c>config_gameplay.txt</c>);
    /// values are written as they are, so those a YAML reader would take for a list, a map or a quoted text are refused
    /// (1.0.5).
    /// </summary>
    Colon,

    /// <summary>
    /// Members of a JSON object (Enshrouded's <c>enshrouded_server.json</c>), each key a JSON Pointer
    /// (<c>/gameSettings/enemyDamageFactor</c>); the game's other members stay. A value that is a lone variable or port
    /// placeholder takes its type: integer, number and port values are JSON numbers, boolean ones <c>true</c> or
    /// <c>false</c>, as are enums whose options all are and such literals; anything else is a string. Patch mode only (1.0.7).
    /// </summary>
    Json,

    /// <summary><c>Key value</c> per line, <c>//</c> starting a comment (Unturned's <c>Commands.dat</c>); values as typed (1.0.7).</summary>
    Space,

    /// <summary>
    /// A file that holds one value and nothing else (Don't Starve Together's <c>cluster_token.txt</c>): <c>set</c> has the
    /// one key <c>content</c>, written as the whole file; an empty value empties it, unless kept. Patch mode only (1.0.12).
    /// </summary>
    Text,

    /// <summary>
    /// The text of elements of an XML file (Space Engineers' <c>SpaceEngineers-Dedicated.cfg</c>), each key the element's
    /// path from the root (<c>/MyConfigDedicated/SessionSettings/MaxPlayers</c>); missing elements are added, an empty value
    /// removes its element, the rest of the file stays. Patch mode only (1.0.12).
    /// </summary>
    Xml,
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
/// <param name="Section">
/// The <c>[section]</c> the keys go into (<see cref="ConfigFileFormat.UnrealIni"/>), or the root element of an
/// <see cref="ConfigFileFormat.XmlProperties"/> file (to create it when it is missing).
/// </param>
/// <param name="Struct">The struct line the keys are members of, when they are not plain lines (<see cref="ConfigFileFormat.UnrealIni"/> only).</param>
/// <param name="KeepIfEmpty">
/// Keys of <see cref="Set"/> whose line stays as the file has it when their value is empty: an option left unset in
/// the panel changes nothing (an empty value of another key removes its line).
/// </param>
/// <param name="WriteIfEmpty">
/// Keys of <see cref="Set"/> of a <see cref="ConfigFileFormat.Json"/> file whose empty value is written as an empty
/// string instead of removing the member: Enshrouded makes up a password for a group that has none (1.0.7).
/// </param>
/// <param name="Lists">
/// Arrays of a <see cref="ConfigFileFormat.Json"/> file that hold one object per id of a list variable (Pulsar 1.0.14;
/// Arma Reforger's <c>/game/mods</c>), by the array's pointer. The panel's ids are merged into the array, not written
/// over it: see <see cref="ConfigListDefinition"/>.
/// </param>
public sealed record ConfigFileDefinition(string Path, ConfigFileFormat Format, ConfigFileMode Mode, IReadOnlyDictionary<string, string> Set,
    string? Section = null, string? Struct = null, IReadOnlyList<string>? KeepIfEmpty = null, IReadOnlyList<string>? WriteIfEmpty = null,
    IReadOnlyDictionary<string, ConfigListDefinition>? Lists = null);

/// <summary>
/// An array of objects that list the ids of <see cref="Variable"/>, each in its member <see cref="Key"/> (Arma
/// Reforger's <c>{ "modId": "5965550F24A0C152" }</c>). Before every start an id the variable lists keeps its object (with
/// whatever else it holds, a version a user pinned) or gets a new one, an id the panel wrote last time and the variable no
/// longer lists loses its object, and the objects users added by hand stay: the agent keeps what it wrote in the server's
/// <c>.pulsar</c> folder.
/// </summary>
/// <param name="Variable">A string variable that lists Workshop items or catalog projects (its separator splits the ids).</param>
/// <param name="Key">The member of each object that holds its id (<c>modId</c>).</param>
public sealed record ConfigListDefinition(string Variable, string Key);

/// <summary>
/// What a backup contains: globs relative to the server directory (Phase 7); and how a running game is asked to write
/// its world to disk first (<see cref="Save"/>, Phase 15).
/// </summary>
public sealed record BackupDefinition(IReadOnlyList<string>? Include = null, IReadOnlyList<string>? Exclude = null, GameSaveDefinition? Save = null);

/// <summary>
/// Console commands that make a running game save before a backup captures its files (docs/architecture/04, "Backups").
/// The <see cref="Commands"/> go through the server's console (RCON when the console uses it); the backup then waits for
/// a console line matching <see cref="Confirmation"/>, at most <see cref="TimeoutSeconds"/>, or, without one, for
/// <see cref="WaitSeconds"/>. Once the files are captured, <see cref="Resume"/> undoes what the commands paused
/// (Minecraft's <c>save-on</c>).
/// </summary>
public sealed record GameSaveDefinition(IReadOnlyList<string> Commands, string? Confirmation = null, int WaitSeconds = 0, int TimeoutSeconds = 60,
    IReadOnlyList<string>? Resume = null);
