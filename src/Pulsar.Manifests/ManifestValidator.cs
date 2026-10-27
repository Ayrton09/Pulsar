using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pulsar.Manifests;

/// <summary>Semantic rules beyond the JSON shape (docs/architecture/04, "Validation").</summary>
public static partial class ManifestValidator
{
    public const string WindowsX64 = "windows-x64";

    /// <summary>Manifest key of Pulsar's own test game (<c>games/_test</c>); the only one allowed the test server step.</summary>
    public const string TestGameKey = "pulsar-test";

    // As the file manager's names (ServerFilePath.MaxSegmentLength).
    private const int MaxSegmentLength = 255;

    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly string[] s_platforms = [WindowsX64];
    private static readonly string[] s_newlines = ["\n", "\r\n", "\r"];
    private static readonly HashSet<string> s_noPorts = [];

    // Set by the agent itself (docs/architecture/04, "Environment").
    private static readonly HashSet<string> s_reservedEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "ComSpec", "SystemRoot", "windir", "PSModulePath", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
    };

    // The .NET runtime's own settings (DOTNET_STARTUP_HOOKS, COR_PROFILER_PATH…) can load any DLL into the game's process.
    private static readonly string[] s_codeLoadingEnvironment = ["DOTNET_", "COR_", "CORECLR_"];

    /// <summary>Where a template is used: what it may reference depends on it.</summary>
    private enum TemplateUse
    {
        Argument,
        Environment,
        ConfigValue,
    }

    public static IReadOnlyList<ManifestError> Validate(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<ManifestError>();
        void Error(string path, string message) => errors.Add(new ManifestError(path, message));

        if (manifest.SchemaVersion != 1)
        {
            Error("schemaVersion", "Only schema version 1 is supported.");
        }

        if (!KeyPattern().IsMatch(manifest.Key))
        {
            Error("key", "Use 2–64 lowercase letters, digits and dashes, starting with a letter or digit.");
        }

        if (!VersionPattern().IsMatch(manifest.Version))
        {
            Error("version", "Use a semantic version (1.2.3).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128)
        {
            Error("name", "Required, at most 128 characters.");
        }

        if (manifest.Description?.Length > 1024)
        {
            Error("description", "At most 1024 characters.");
        }

        if (manifest.Platforms.Count == 0 || manifest.Platforms.Any(p => !s_platforms.Contains(p)) || manifest.Platforms.Distinct().Count() != manifest.Platforms.Count)
        {
            Error("platforms", $"Supported platforms: {string.Join(", ", s_platforms)}.");
        }

        foreach (var platform in manifest.Runtime.Keys.Where(p => !manifest.Platforms.Contains(p)))
        {
            Error($"runtime.{platform}", "Runtime for a platform the manifest does not declare.");
        }

        if (manifest.Requirements is { } requirements)
        {
            if (requirements.MinDiskGb is < 0 or > 10_000)
            {
                Error("requirements.minDiskGb", "Between 0 and 10000.");
            }

            if (requirements.MinMemoryMb is < 0 or > 1_048_576)
            {
                Error("requirements.minMemoryMb", "Between 0 and 1048576.");
            }
        }

        var variables = ValidateVariables(manifest.VariableList, Error);
        var ports = ValidatePorts(manifest.PortList, Error);
        var context = new Context(variables, ports, Error);

        if (manifest.Install is { } install)
        {
            ValidateSteps("install", manifest, install, context);
        }

        if (manifest.Update is { } update)
        {
            ValidateSteps("update", manifest, update, context);
        }

        RuntimeDefinition? runtime = null;
        foreach (var platform in manifest.Platforms)
        {
            if (!manifest.Runtime.TryGetValue(platform, out var platformRuntime))
            {
                Error("runtime", $"Missing the runtime for {platform}.");
                continue;
            }

            runtime = platformRuntime;
            ValidateRuntime($"runtime.{platform}", platformRuntime, context);
        }

        context.HasRcon = runtime?.Console.Rcon is not null;
        ValidateConfigFiles(manifest.ConfigFileList, context);
        if (context.HasRcon && !context.UsesRconPassword)
        {
            Error("runtime", "The game must receive the RCON password: use {{rcon.password}} in its arguments or a configuration file.");
        }

        if (manifest.Query is { } query)
        {
            ValidateQuery(query, ports, Error);
        }

        ValidateReadiness(manifest.Readiness, runtime?.Console, manifest.Query, Error);
        ValidateStop(manifest.Stop, runtime?.Console, Error);
        if (manifest.Backup is { } backup)
        {
            ValidateGlobs("backup.include", backup.Include ?? [], Error);
            ValidateGlobs("backup.exclude", backup.Exclude ?? [], Error);
            if (backup.Save is { } save)
            {
                ValidateSave(save, Error);
            }
        }

        ValidateAddons(manifest.AddonList, Error);
        if (manifest.Commands is { } commands)
        {
            ValidateCommands(commands, Error);
        }

        ValidateMods(manifest, context);
        return errors;
    }

    private static void ValidateMods(GameManifest manifest, Context context)
    {
        var sources = manifest.ModList;
        if (sources.Count > 4 || sources.Select(s => s.Source).Distinct(StringComparer.Ordinal).Count() != sources.Count)
        {
            context.Error("mods", "At most 4 catalogs, each once.");
        }

        // The Mods tab shows one type for the server: a game's catalogs are typed alike, and change while it runs alike.
        if (sources.Select(s => (s.AddonTypes, s.TypeVar, s.WhileRunning, s.Versioned)).Distinct().Count() > 1)
        {
            context.Error("mods", "A game's catalogs share how the server's type is told (typeVar or addonTypes), whileRunning, and whether they are made per game version.");
        }

        for (var i = 0; i < sources.Count; i++)
        {
            var path = $"mods[{i}]";
            var source = sources[i];
            if (source.Source is not (ModSourceDefinition.Modrinth or ModSourceDefinition.CurseForge or ModSourceDefinition.Umod
                    or ModSourceDefinition.Thunderstore))
            {
                context.Error($"{path}.source", $"Pulsar knows the catalogs '{ModSourceDefinition.Modrinth}', '{ModSourceDefinition.CurseForge}', "
                    + $"'{ModSourceDefinition.Umod}' and '{ModSourceDefinition.Thunderstore}'.");
            }

            // Thunderstore's packages are zips unpacked into BepInEx's folder: the catalog is typed by the BepInEx addon.
            var thunderstore = source.Source == ModSourceDefinition.Thunderstore;
            if (thunderstore && !source.AddonTypes)
            {
                context.Error($"{path}.addonTypes", "Thunderstore's packages run on the game's BepInEx addon: 'addonTypes' must be true.");
            }

            if (source.Category is { } category && (!thunderstore || !CommunityPattern().IsMatch(category)))
            {
                context.Error($"{path}.category", "Only for Thunderstore: a category's slug, lowercase letters, digits or '-' (server-side).");
            }

            // Modrinth's and CurseForge's files are made per game version and loader; uMod's are not.
            if (source.Versioned ? source.VersionVar is not { } versionVar || !IsVersionVariable(versionVar, context) : source.VersionVar is not null)
            {
                context.Error($"{path}.versionVar", source.Versioned
                    ? "Must name a string variable with a 'pattern', or an enum."
                    : "This catalog's files are not made per game version: leave it out.");
            }

            var communal = source.Source is ModSourceDefinition.Umod or ModSourceDefinition.Thunderstore;
            if (communal ? source.Community is null || !CommunityPattern().IsMatch(source.Community) : source.Community is not null)
            {
                context.Error($"{path}.community", communal
                    ? "The catalog's name for the game: 1 to 32 lowercase letters, digits or '-' (rust, valheim)."
                    : "Only for uMod and Thunderstore.");
            }

            if (source.WhileRunning && source.Source != ModSourceDefinition.Umod)
            {
                context.Error($"{path}.whileRunning", "Only uMod's plugins are loaded by a running game (Oxide, Carbon).");
            }

            // The server's type: an option of an enum variable, or the addon it has.
            HashSet<string>? options = null;
            if (source.AddonTypes)
            {
                options = manifest.AddonList.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
                if (source.TypeVar is not null || source.Versioned)
                {
                    context.Error($"{path}.addonTypes", "Not with a 'typeVar', nor for a catalog whose files are made per game version.");
                }
            }
            else
            {
                options = source.TypeVar is not null && context.Variables.TryGetValue(source.TypeVar, out var type) && type.Type == VariableType.Enum
                    && !type.Secret
                    ? (type.Options ?? []).Select(o => o.Value).ToHashSet(StringComparer.Ordinal)
                    : null;
                if (options is null)
                {
                    context.Error($"{path}.typeVar", "Must name an enum variable (or the types are the game's addons: 'addonTypes').");
                }
            }

            if (source.Types.Count is 0 or > 32)
            {
                context.Error($"{path}.types", "1 to 32 types.");
            }

            foreach (var (key, target) in source.Types)
            {
                var at = $"{path}.types.{key}";
                if (options is not null && !options.Contains(key))
                {
                    context.Error(at, source.AddonTypes ? "Not one of the game's addons." : $"Not an option of '{source.TypeVar}'.");
                }

                if (target.ProjectType is not ("mod" or "plugin"))
                {
                    context.Error($"{at}.projectType", "'mod' or 'plugin'.");
                }

                if (source.Versioned
                        ? target.LoaderList.Count is 0 or > 8 || target.LoaderList.Any(l => !CatalogLoaderPattern().IsMatch(l))
                        : target.LoaderList.Count > 0)
                {
                    context.Error($"{at}.loaders", source.Versioned
                        ? "1 to 8 loaders, each lowercase letters, digits, '-' or '_'."
                        : "This catalog names no loaders: leave them out.");
                }

                if (target.Extensions is { } extensions && (extensions.Count is 0 or > 4 || extensions.Any(e => !ExtensionPattern().IsMatch(e)) || thunderstore))
                {
                    context.Error($"{at}.extensions", thunderstore
                        ? "Thunderstore's packages are zips unpacked by their layout: leave it out."
                        : "1 to 4 endings, each '.' and 1 to 8 lowercase letters or digits (.jar, .cs).");
                }

                if (thunderstore ? target.Layout != ModTargetDefinition.BepInExLayout : target.Layout is not null)
                {
                    context.Error($"{at}.layout", thunderstore ? "Thunderstore's packages go into BepInEx's folder: 'bepinex'." : "Only for Thunderstore.");
                }

                // A folder of its own: never the server's root, never Pulsar's, nothing hidden.
                if (!IsValidRelativePath(target.Folder) || target.Folder.Length > 64 || target.Folder.Split('/') is { Length: > 3 } parts
                    || target.Folder.Split('/').Any(p => p.StartsWith('.')))
                {
                    context.Error($"{at}.folder", "A folder in the server's folder, up to three names deep (mods, plugins, oxide/plugins).");
                }
            }

            if (source.Modpacks is { } modpacks)
            {
                if (!context.Variables.TryGetValue(modpacks.LoaderVar, out var loader) || loader.Type != VariableType.String || loader.Secret
                    || loader.Pattern is null || modpacks.LoaderVar == source.VersionVar || modpacks.LoaderVar == source.TypeVar)
                {
                    context.Error($"{path}.modpacks.loaderVar", "Must name a string variable with a 'pattern' of its own.");
                }

                if (!source.Types.Values.Any(t => t.ProjectType == "mod") || !source.Versioned)
                {
                    context.Error($"{path}.modpacks", "Modpacks are made of mods, from Modrinth or CurseForge: a type must take them.");
                }
            }
        }
    }

    private static void ValidateCommands(CommandsDefinition commands, Action<string, string> error)
    {
        if (commands.Broadcast is { } broadcast && (!IsCommand(broadcast) || !GameCommands.HasOnlyMessage(broadcast)))
        {
            error("commands.broadcast", "A single line of up to 256 characters with {{message}} once, and no other placeholder.");
        }

        if (commands.MessageSpaces is { } spaces && spaces is not ("_" or "-" or "."))
        {
            error("commands.messageSpaces", "One of _ - .");
        }

        if (commands.Players is { } players)
        {
            ValidatePlayerCommands(players, error);
        }
    }

    private static void ValidatePlayerCommands(PlayerCommandsDefinition players, Action<string, string> error)
    {
        var declared = 0;
        foreach (var action in Enum.GetValues<PlayerAction>())
        {
            if (GameCommands.Template(players, action) is not { } template)
            {
                continue;
            }

            declared++;
            if (!IsCommand(template) || !GameCommands.HasOnlyPlayer(template))
            {
                error($"commands.players.{JsonNamingPolicy.CamelCase.ConvertName(action.ToString())}",
                    "A single line of up to 256 characters with {{player}} once, and no other placeholder.");
            }
        }

        var lists = players.Lists ?? new PlayerListsDefinition();
        var files = new (PlayerListFile? File, string Name)[] { (lists.Banned, "banned"), (lists.Whitelist, "whitelist"), (lists.Admins, "admins") };
        foreach (var (file, name) in files)
        {
            if (file is null)
            {
                continue;
            }

            ValidateRelativePath($"commands.players.lists.{name}.file", file.File, error);
            if (!ListFieldPattern().IsMatch(file.Field))
            {
                error($"commands.players.lists.{name}.field", "Use 1–32 letters, digits and underscores, starting with a letter.");
            }
        }

        if (declared == 0)
        {
            error("commands.players", "At least one command.");
        }
    }

    private static void ValidateAddons(IReadOnlyList<AddonDefinition> addons, Action<string, string> error)
    {
        if (addons.Count > 16)
        {
            error("addons", "At most 16 addons.");
        }

        var keys = addons.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        if (keys.Count != addons.Count)
        {
            error("addons", "Each addon key can be declared once.");
        }

        for (var i = 0; i < addons.Count; i++)
        {
            var addon = addons[i];
            var path = $"addons[{i}]";
            if (!AddonKeyPattern().IsMatch(addon.Key))
            {
                error($"{path}.key", "Lowercase letters, digits and '-', 2 to 32 characters.");
            }

            if (!IsLabel(addon.Name, 64) || !IsLabel(addon.License, 64) || (addon.Description is { } description && !IsLabel(description, 512)))
            {
                error(path, "A name and a licence of 1 to 64 characters, a description of at most 512, on one line.");
            }

            if (!Uri.TryCreate(addon.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || addon.Url.Length > 256)
            {
                error($"{path}.url", "The addon's home page, over https.");
            }

            if (addon.Target.Length > 0)
            {
                ValidateRelativePath($"{path}.target", addon.Target, error);
            }

            switch (addon.Source)
            {
                case GitHubReleaseSource github:
                    ValidateGitHubRelease($"{path}.source", github, error);
                    break;
                case ThunderstoreSource package:
                    if (!ThunderstorePackagePattern().IsMatch(package.Package))
                    {
                        error($"{path}.source.package", "The package's namespace and name: letters, digits and '_', joined by '-' (denikson-BepInExPack_Valheim).");
                    }

                    if (package.Folder.Length > 0 && (!IsValidRelativePath(package.Folder) || package.Folder.Contains('/', StringComparison.Ordinal)
                            || package.Folder.StartsWith('.')))
                    {
                        error($"{path}.source.folder", "One folder of the package's zip (BepInExPack_Valheim), or nothing for the whole zip.");
                    }

                    break;
                default:
                    error($"{path}.source", "Unknown source.");
                    break;
            }

            foreach (var other in addon.RequiresList.Concat(addon.ConflictsList))
            {
                if (other == addon.Key || !keys.Contains(other))
                {
                    error(path, $"'{other}' must be another addon of the manifest.");
                }
            }

            if (addon.RequiresList.Intersect(addon.ConflictsList).Any())
            {
                error(path, "An addon cannot both require and exclude another.");
            }

            if (addon.LineEditList.Count > 8)
            {
                error($"{path}.lineEdits", "At most 8 line edits.");
            }

            for (var j = 0; j < addon.LineEditList.Count; j++)
            {
                var edit = addon.LineEditList[j];
                ValidateRelativePath($"{path}.lineEdits[{j}].path", edit.Path, error);
                if (!IsLine(edit.After) || !IsLine(edit.Line))
                {
                    error($"{path}.lineEdits[{j}]", "'after' and 'line' are one line of 1 to 256 characters (tabs allowed).");
                }
            }
        }

        // An addon is installed once what it requires is: a cycle (A requires B, B requires A) could never be installed.
        var requires = addons.GroupBy(a => a.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().RequiresList, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < addons.Count; i++)
        {
            if (FindCycle(addons[i].Key, []) is { } cycle)
            {
                error($"addons[{i}]", $"Addons cannot require each other in a cycle: {string.Join(" -> ", cycle)}.");
                break;
            }
        }

        List<string>? FindCycle(string key, List<string> trail)
        {
            if (trail.Contains(key))
            {
                return [.. trail.SkipWhile(k => k != key), key];
            }

            if (done.Contains(key) || !requires.TryGetValue(key, out var needed))
            {
                return null;
            }

            trail.Add(key);
            foreach (var next in needed)
            {
                if (FindCycle(next, trail) is { } found)
                {
                    return found;
                }
            }

            trail.RemoveAt(trail.Count - 1);
            done.Add(key);
            return null;
        }

        static bool IsLabel(string text, int max) => text.Trim().Length > 0 && text.Length <= max && !text.Any(char.IsControl);

        static bool IsLine(string text) => text.Trim().Length > 0 && text.Length <= 256 && !text.Any(c => char.IsControl(c) && c != '\t');
    }

    /// <summary>A path relative to the server directory: forward slashes, no way out of it, nothing Windows treats specially.</summary>
    public static bool IsValidRelativePath(string? path) => path is { Length: > 0 and <= 260 } && RelativePathError(path) is null;

    private static void ValidateRelativePath(string path, string value, Action<string, string> error)
    {
        if (value.Length is 0 or > 260)
        {
            error(path, "Between 1 and 260 characters.");
        }
        else if ((RelativePathError(value) ?? PulsarFolderError(value)) is { } message)
        {
            error(path, message);
        }
    }

    /// <summary>A release of a GitHub repository whose asset the agent downloads and unpacks (an addon's, or an install step's).</summary>
    private static void ValidateGitHubRelease(string path, GitHubReleaseSource github, Action<string, string> error)
    {
        if (!GitHubRepoPattern().IsMatch(github.Repo))
        {
            error($"{path}.repo", "A GitHub repository, 'owner/name'.");
        }

        if (github.Asset.Length is 0 or > 200 || CompileError(github.Asset) is not null)
        {
            error($"{path}.asset", "A regular expression (non-backtracking, at most 200 characters) for the asset's file name.");
        }
        else if (ManifestFeatures.AddonFormat(github) is null)
        {
            // What the agent unpacks is known from the manifest: a .tar.gz goes only to agents that read one.
            error($"{path}.asset", @"Must end with the archive's extension, which the agent unpacks: '\.zip$', '\.tar\.gz$' or '\.tgz$'.");
        }

        // A tag goes into the API's URL as one segment: '.' and '..' would be read as a path.
        if (github.Tag is { } tag && (!BranchPattern().IsMatch(tag) || tag is "." or ".." || github.Prerelease))
        {
            error($"{path}.tag", "1–64 letters, digits, '_', '-' or '.' (not '.' or '..'); not with 'prerelease'.");
        }
    }

    /// <summary>
    /// Manifests never name Pulsar's own folder (<c>.pulsar</c>: SteamCMD, its login, addon records), by its name or
    /// by a first segment shaped like an 8.3 short name (<c>PULSAR~1</c>), as the file manager refuses it.
    /// </summary>
    private static string? PulsarFolderError(string path)
    {
        var first = path.Split('/')[0];
        return first.Equals(".pulsar", StringComparison.OrdinalIgnoreCase) || ShortName().IsMatch(first)
            ? "Pulsar's own folder (.pulsar) is off limits."
            : null;
    }

    /// <remarks>The file manager's segment rules (<c>ServerFilePath</c>); the agent also uses it for its own paths.</remarks>
    internal static string? RelativePathError(string path, bool allowWildcards = false)
    {
        if (path.Contains('\\', StringComparison.Ordinal))
        {
            return "Use forward slashes.";
        }

        if (path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal))
        {
            return "Must be relative to the server directory (no root, drive or stream).";
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return "Empty, '.' and '..' segments are not allowed.";
            }

            if (segment.Length > MaxSegmentLength)
            {
                return $"Names have at most {MaxSegmentLength} characters.";
            }

            if (segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' '))
            {
                return "Segments cannot start with a space or end with a dot or a space.";
            }

            if (segment.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '|' || (!allowWildcards && c is '?' or '*')))
            {
                return "Contains characters Windows does not allow in names.";
            }

            // Windows maps these names to devices whatever their extension, and trims spaces first ("nul.txt", "CON .cfg").
            var dot = segment.IndexOf('.', StringComparison.Ordinal);
            if (ReservedName().IsMatch((dot < 0 ? segment : segment[..dot]).TrimEnd(' ')))
            {
                return $"'{segment}' is a reserved device name on Windows.";
            }
        }

        return null;
    }

    private static Dictionary<string, VariableDefinition> ValidateVariables(IReadOnlyList<VariableDefinition> variables, Action<string, string> error)
    {
        var byKey = new Dictionary<string, VariableDefinition>(StringComparer.Ordinal);
        for (var i = 0; i < variables.Count; i++)
        {
            var variable = variables[i];
            var path = $"variables[{i}]";
            if (!VariableKeyPattern().IsMatch(variable.Key) || !byKey.TryAdd(variable.Key, variable))
            {
                error($"{path}.key", "Unique, a letter followed by up to 63 letters, digits or underscores.");
            }

            if (string.IsNullOrWhiteSpace(variable.Label) || variable.Label.Length > 64)
            {
                error($"{path}.label", "Required, at most 64 characters.");
            }

            if (variable.Description?.Length > 512)
            {
                error($"{path}.description", "At most 512 characters.");
            }

            if (variable.Secret && variable.Default is not null)
            {
                error($"{path}.default", "Secret variables cannot have a default value.");
            }

            if (variable.Secret && variable.Type != VariableType.String)
            {
                error($"{path}.secret", "Only string variables can be secret.");
            }

            if (variable.Workshop is { } workshop)
            {
                if (variable.Type != VariableType.String || variable.Secret)
                {
                    error($"{path}.workshop", "Only string variables that are not secret can list Workshop items.");
                }

                if (workshop.AppId is < 1 or > uint.MaxValue)
                {
                    error($"{path}.workshop.appId", "A Steam app id.");
                }

                if (workshop.Separator is not (";" or ","))
                {
                    error($"{path}.workshop.separator", "';' or ','.");
                }
            }

            if (variable.CurseForge is { } curseForge)
            {
                if (variable.Type != VariableType.String || variable.Secret || variable.Workshop is not null)
                {
                    error($"{path}.curseForge", "Only string variables that are not secret, and list no Workshop items, can list CurseForge projects.");
                }

                if (curseForge.GameId < 1)
                {
                    error($"{path}.curseForge.gameId", "A CurseForge game id.");
                }

                if (curseForge.Separator is not (";" or ","))
                {
                    error($"{path}.curseForge.separator", "',' or ';'.");
                }
            }

            if (variable.ReforgerWorkshop is { } reforger)
            {
                if (variable.Type != VariableType.String || variable.Secret || variable.Workshop is not null || variable.CurseForge is not null)
                {
                    error($"{path}.reforgerWorkshop", "Only string variables that are not secret, and list nothing else, can list Arma Reforger's Workshop mods.");
                }

                if (reforger.Separator is not (";" or ","))
                {
                    error($"{path}.reforgerWorkshop.separator", "',' or ';'.");
                }
            }

            switch (variable.Type)
            {
                case VariableType.String:
                    // As it runs (wrapped): "(?x)a # comment" compiles alone, not with the wrapper's end in the comment.
                    if (variable.Pattern is { } pattern && (CompileError(pattern) ?? CompileError(VariableValues.WholePattern(pattern))) is { } patternError)
                    {
                        error($"{path}.pattern", patternError);
                    }

                    if (variable.MaxLength is < 1 or > 4096)
                    {
                        error($"{path}.maxLength", "Between 1 and 4096.");
                    }

                    break;
                case VariableType.Integer:
                    if (variable.Min is null || variable.Max is null || variable.Min > variable.Max
                        || decimal.Truncate(variable.Min.Value) != variable.Min || decimal.Truncate(variable.Max.Value) != variable.Max
                        || variable.Min < long.MinValue || variable.Max > long.MaxValue)
                    {
                        error(path, "Integer variables need whole 'min' and 'max' (min ≤ max).");
                    }

                    break;
                case VariableType.Number:
                    if (variable.Min is null || variable.Max is null || variable.Min > variable.Max || variable.Min.Value.Scale > 6 || variable.Max.Value.Scale > 6)
                    {
                        error(path, "Number variables need 'min' and 'max' (min ≤ max, up to 6 decimals).");
                    }

                    break;
                case VariableType.Enum:
                    if (variable.Options is not { Count: > 0 } options || options.Select(o => o.Value).Distinct(StringComparer.Ordinal).Count() != options.Count)
                    {
                        error($"{path}.options", "Enum variables need options with unique values.");
                    }
                    else if (options.Any(o => !OptionValuePattern().IsMatch(o.Value) || o.Map?.Values.Any(v => v.Any(char.IsControl)) == true))
                    {
                        error($"{path}.options", "Option values are 1–64 letters, digits, '_', '-' or '.'; mapped values are single lines.");
                    }

                    break;
            }

            if (variable.Type != VariableType.String && (variable.Pattern is not null || variable.MaxLength is not null))
            {
                error(path, "'pattern' and 'maxLength' apply to string variables only.");
            }

            if (variable.Default is { ValueKind: System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array })
            {
                error($"{path}.default", "A string, a number or true or false.");
            }
            else if (variable.Default is { } value && VariableValues.Check(variable, VariableValues.FromJson(value)) is { } defaultError)
            {
                error($"{path}.default", defaultError);
            }
        }

        return byKey;
    }

    private static Dictionary<string, PortDefinition> ValidatePorts(IReadOnlyList<PortDefinition> ports, Action<string, string> error)
    {
        var byName = new Dictionary<string, PortDefinition>(StringComparer.Ordinal);
        for (var i = 0; i < ports.Count; i++)
        {
            var port = ports[i];
            var path = $"ports[{i}]";
            if (!PortNamePattern().IsMatch(port.Name) || !byName.TryAdd(port.Name, port))
            {
                error($"{path}.name", "Unique, a lowercase letter followed by up to 31 lowercase letters, digits or underscores.");
            }

            if (string.IsNullOrWhiteSpace(port.Label) || port.Label.Length > 64)
            {
                error($"{path}.label", "Required, at most 64 characters.");
            }

            if (port.Protocols.Count == 0 || port.Protocols.Distinct().Count() != port.Protocols.Count)
            {
                error($"{path}.protocols", "One or both of 'udp' and 'tcp', once each.");
            }

            if (port.Default is < 1 or > 65535)
            {
                error($"{path}.default", "Between 1 and 65535.");
            }
        }

        if (ports.Select(p => p.Default).Distinct().Count() != ports.Count)
        {
            error("ports", "Two ports have the same default.");
        }

        for (var i = 0; i < ports.Count; i++)
        {
            if (ports[i].Follows is not { } follow)
            {
                continue;
            }

            var path = $"ports[{i}].follows";
            if (!byName.TryGetValue(follow.Port, out var followed) || followed == ports[i])
            {
                error($"{path}.port", "The name of another port of the game.");
            }
            else if (followed.Follows is not null)
            {
                error($"{path}.port", "A port can follow only a port that does not follow another.");
            }
            else
            {
                if (followed.Required != ports[i].Required)
                {
                    error($"{path}", "A port and the port it follows are both required or both optional.");
                }

                if (ports[i].Default != followed.Default + follow.Offset)
                {
                    error($"ports[{i}].default", $"The followed port's default plus {follow.Offset}.");
                }
            }

            if (follow.Offset is < 1 or > 10)
            {
                error($"{path}.offset", "From 1 to 10.");
            }
        }

        return byName;
    }

    private static void ValidateSteps(string section, GameManifest manifest, InstallDefinition definition, Context context)
    {
        if (definition.Steps.Count is 0 or > 32)
        {
            context.Error($"{section}.steps", "Between 1 and 32 steps.");
        }

        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var path = $"{section}.steps[{i}]";
            switch (definition.Steps[i])
            {
                case SteamCmdStep steam:
                    if (steam.AppId <= 0)
                    {
                        context.Error($"{path}.appId", "A Steam app id.");
                    }

                    if (steam.Login is not (SteamCmdStep.AnonymousLogin or SteamCmdStep.AccountLogin))
                    {
                        context.Error($"{path}.login", "'anonymous', or 'account' for a server only an account that owns it may download.");
                    }

                    if (steam.Beta is { } beta && !BranchPattern().IsMatch(beta))
                    {
                        context.Error($"{path}.beta", "1–64 letters, digits, '_', '-' or '.'.");
                    }

                    if (steam.BetaPasswordVar is { } passwordVar
                        && (steam.Beta is null || !context.Variables.TryGetValue(passwordVar, out var password) || !password.Secret))
                    {
                        context.Error($"{path}.betaPasswordVar", "Must name a secret variable, together with 'beta'.");
                    }

                    break;
                case EnsureDirectoryStep step:
                    ValidateRelativePath($"{path}.path", step.Path, context.Error);
                    break;
                case CopyFileIfMissingStep step:
                    ValidateRelativePath($"{path}.from", step.From, context.Error);
                    ValidateRelativePath($"{path}.to", step.To, context.Error);
                    if (string.Equals(step.From, step.To, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Error($"{path}.to", "A different file than 'from'.");
                    }

                    break;
                case WriteFileIfMissingStep step:
                    ValidateRelativePath($"{path}.path", step.Path, context.Error);
                    if (step.Content.Length > 64 * 1024)
                    {
                        context.Error($"{path}.content", "At most 64 KiB.");
                    }

                    break;
                case MinecraftServerStep step:
                    if (step.VersionVar is { } javaVersionVar && !IsVersionVariable(javaVersionVar, context))
                    {
                        context.Error($"{path}.versionVar", "Must name a string variable with a 'pattern', or an enum.");
                    }

                    if (step.TypeVar is { } typeVar && !(context.Variables.TryGetValue(typeVar, out var type) && !type.Secret
                            && type.Type == VariableType.Enum && type.Options is { Count: > 0 } options
                            && options.All(o => MinecraftServerStep.Types.Contains(o.Value))))
                    {
                        context.Error($"{path}.typeVar", $"Must name an enum whose options are server types: {string.Join(", ", MinecraftServerStep.Types.Order(StringComparer.Ordinal))}.");
                    }

                    if (step.LoaderVar is { } loaderVar && !IsVersionVariable(loaderVar, context))
                    {
                        context.Error($"{path}.loaderVar", "Must name a string variable with a 'pattern', or an enum.");
                    }

                    break;
                case MinecraftBedrockStep { VersionVar: { } versionVar }
                    when !IsVersionVariable(versionVar, context):
                    context.Error($"{path}.versionVar", "Must name a string variable with a 'pattern', or an enum.");
                    break;
                case GitHubReleaseStep step:
                    ValidateGitHubRelease(path, step.Source, context.Error);
                    if (step.Target.Length > 0)
                    {
                        ValidateRelativePath($"{path}.target", step.Target, context.Error);
                    }

                    // The runtime's folder is the runtime step's own: its updates replace what is there.
                    if (definition.Steps.Any(s => s is DotnetRuntimeStep) && IsUnder(step.Target, DotnetRuntimeStep.Folder))
                    {
                        context.Error($"{path}.target", $"'{DotnetRuntimeStep.Folder}' holds the .NET runtime of the 'dotnetRuntime' step.");
                    }

                    if (definition.Steps.OfType<GitHubReleaseStep>().Count(s => s.RecordKey == step.RecordKey) > 1)
                    {
                        context.Error($"{path}.repo", "One 'githubRelease' step per repository.");
                    }

                    break;
                case DotnetRuntimeStep step:
                    if (!DotnetChannelPattern().IsMatch(step.Channel))
                    {
                        context.Error($"{path}.channel", "A .NET channel: its major version, '9.0'.");
                    }

                    if (definition.Steps.OfType<DotnetRuntimeStep>().Count() > 1)
                    {
                        context.Error(path, "One 'dotnetRuntime' step.");
                    }

                    break;
                case TestGameServerStep step:
                    ValidateRelativePath($"{path}.path", step.Path, context.Error);
                    if (manifest.Key != TestGameKey)
                    {
                        context.Error(path, $"Only the '{TestGameKey}' manifest can install the test game server.");
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The game's log: a relative path whose file name may hold one <c>*</c> (Pulsar 1.0.12), for a game that names its
    /// log by its start (Space Engineers' <c>SpaceEngineersDedicated_*.log</c>): the agent follows the newest file it matches.
    /// </summary>
    private static void ValidateLogFile(string path, string logFile, Action<string, string> error)
    {
        var separator = logFile.LastIndexOf('/');
        if (logFile.Count(c => c == '*') > 1 || (logFile.Contains('*', StringComparison.Ordinal) && logFile.LastIndexOf('*') < separator))
        {
            error(path, "At most one '*', in the file's name.");
            return;
        }

        ValidateRelativePath(path, logFile.Replace('*', 'x'), error);
    }

    /// <summary>Whether a log file's name is a pattern (one <c>*</c> for any run of characters).</summary>
    public static bool IsLogFilePattern(string logFile)
    {
        ArgumentNullException.ThrowIfNull(logFile);
        return logFile.Contains('*', StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it ("" is the server's folder, above it).</summary>
    private static bool IsUnder(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>A version a download step puts in an address: a non-secret enum, or a string the panel checks with a pattern.</summary>
    private static bool IsVersionVariable(string key, Context context) =>
        context.Variables.TryGetValue(key, out var version) && !version.Secret
        && (version.Type == VariableType.Enum || (version.Type == VariableType.String && version.Pattern is not null));

    private static void ValidateRuntime(string path, RuntimeDefinition runtime, Context context)
    {
        ValidateRelativePath($"{path}.executable", runtime.Executable, context.Error);
        if (IsValidRelativePath(runtime.Executable) && !runtime.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            context.Error($"{path}.executable", "Only .exe files can be started (no scripts, installers or interpreters).");
        }

        if (runtime.WorkingDirectory is { } workingDirectory)
        {
            ValidateRelativePath($"{path}.workingDirectory", workingDirectory, context.Error);
        }

        if (runtime.UserProfile is { } userProfile)
        {
            ValidateRelativePath($"{path}.userProfile", userProfile, context.Error);
        }

        var console = runtime.Console;
        context.HasRcon = console.Rcon is not null;
        ValidateArguments($"{path}.arguments", runtime.ArgumentList, context, guardedPorts: s_noPorts);
        if (runtime.Environment is { } environment && environment.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != environment.Count)
        {
            // Windows environment names ignore case.
            context.Error($"{path}.environment", "Names differ only by case.");
        }

        foreach (var (name, value) in runtime.Environment ?? new Dictionary<string, string>())
        {
            if (s_reservedEnvironment.Contains(name) || !EnvironmentNamePattern().IsMatch(name))
            {
                context.Error($"{path}.environment.{name}", "Reserved or invalid environment variable name.");
            }
            else if (s_codeLoadingEnvironment.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                context.Error($"{path}.environment.{name}", "Environment variables that make the .NET runtime load code or profilers are not allowed.");
            }

            ValidateTemplate($"{path}.environment.{name}", value, context, TemplateUse.Environment, guardedPorts: s_noPorts);
        }

        if (console.InputNewline is { } newline && !s_newlines.Contains(newline))
        {
            context.Error($"{path}.console.inputNewline", "Use \"\\n\", \"\\r\\n\" or \"\\r\".");
        }

        if (console.Mode == ManifestConsoleMode.Pty && console.Encoding != ManifestConsoleEncoding.Utf8)
        {
            context.Error($"{path}.console.encoding", "A pseudo console always produces UTF-8.");
        }

        if (console.SensitiveCommandPrefixes is { } prefixes && prefixes.Any(p => p.Length is 0 or > 64 || p.Any(char.IsControl)))
        {
            context.Error($"{path}.console.sensitiveCommandPrefixes", "Single words of up to 64 characters.");
        }

        if (console.Mode == ManifestConsoleMode.LogfileRcon)
        {
            if (console.LogFile is not { } logFile)
            {
                context.Error($"{path}.console.logFile", "Required in 'logfile+rcon' mode.");
            }
            else
            {
                ValidateLogFile($"{path}.console.logFile", logFile, context.Error);
            }

            if (console.Rcon is null)
            {
                context.Error($"{path}.console.rcon", "Required in 'logfile+rcon' mode.");
            }

            if (console.InputNewline is not null)
            {
                context.Error($"{path}.console.inputNewline", "Commands go through RCON in 'logfile+rcon' mode.");
            }
        }
        else if (console.LogFile is { } pipedLog)
        {
            // A game run with pipes that writes only to its log (Killing Floor 2): the log is its console (1.0.12).
            if (console.Mode != ManifestConsoleMode.Pipes)
            {
                context.Error($"{path}.console.logFile", "'logFile' belongs to the 'logfile+rcon' and 'pipes' modes.");
            }
            else
            {
                ValidateLogFile($"{path}.console.logFile", pipedLog, context.Error);
            }
        }
        else if (console.Rcon is not null && console.InputNewline is not null)
        {
            // pty or pipes with RCON: the output comes from the process, commands go through RCON (Palworld).
            context.Error($"{path}.console.inputNewline", "Commands go through RCON when the console has one.");
        }

        // RCON listens on TCP whatever the port declares: the declared protocols are what the firewall opens, and RCON
        // only needs to be reachable from the node itself.
        if (console.Rcon is { } rcon && (!context.Ports.TryGetValue(rcon.Port, out var port) || !port.Required))
        {
            context.Error($"{path}.console.rcon.port", "Must name a required port of the manifest.");
        }
    }

    private static void ValidateArguments(string path, IReadOnlyList<ArgumentTemplate> arguments, Context context, IReadOnlySet<string> guardedPorts)
    {
        if (arguments.Count > 256)
        {
            context.Error(path, "At most 256 arguments.");
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case ArgumentText text:
                    ValidateTemplate($"{path}[{i}]", text.Text, context, TemplateUse.Argument, guardedPorts);
                    break;
                case ArgumentCondition condition:
                    var guarded = guardedPorts;
                    switch (condition.If.Split('.'))
                    {
                        // A boolean holds when true; any other variable when it has a value (an option left unset does not),
                        // so its arguments may take an optional choice's sub-value.
                        case ["var", var key] when context.Variables.ContainsKey(key):
                            guarded = new HashSet<string>(guardedPorts, StringComparer.Ordinal) { $"var.{key}" };
                            break;
                        // 1.0.9: what the chosen option maps (Minecraft's server types: an arguments file, or a jar).
                        case ["var", var key, var sub] when context.Variables.TryGetValue(key, out var choice) && choice.Type == VariableType.Enum
                            && (choice.Required || choice.Default is not null) && choice.Options is { Count: > 0 } choices
                            && choices.All(o => o.Map?.ContainsKey(sub) == true):
                            break;
                        case ["port", var name] when context.Ports.ContainsKey(name):
                            guarded = new HashSet<string>(guardedPorts, StringComparer.Ordinal) { name };
                            break;
                        default:
                            context.Error($"{path}[{i}].if",
                                "Must be 'var.<key>' of a variable, 'var.<key>.<sub>' of an enum with a default (or required) whose options all map it, or 'port.<name>'.");
                            break;
                    }

                    ValidateArguments($"{path}[{i}].then", condition.Then, context, guarded);
                    if (condition.Else is { } otherwise)
                    {
                        // Not guarded: the else side runs exactly when the port is not assigned.
                        ValidateArguments($"{path}[{i}].else", otherwise, context, guardedPorts);
                    }

                    break;
            }
        }
    }

    private static void ValidateTemplate(string path, string template, Context context, TemplateUse use, IReadOnlySet<string> guardedPorts)
    {
        if (template.Length > 1024)
        {
            context.Error(path, "At most 1024 characters.");
        }

        var placeholders = Placeholders.Parse(template, out var malformed);
        foreach (var placeholder in placeholders)
        {
            switch (placeholder)
            {
                case ["server", "shortId"] or ["server", "dir"]:
                    break;
                case ["var", var key, .. var rest] when rest.Length <= 1:
                    if (!context.Variables.TryGetValue(key, out var variable))
                    {
                        context.Error(path, $"Unknown variable '{key}'.");
                    }
                    else if (rest is [var sub] && (variable.Type != VariableType.Enum || variable.Options is null || variable.Options.Any(o => o.Map?.ContainsKey(sub) != true)))
                    {
                        context.Error(path, $"'{key}.{sub}' is not a sub-value every option of the enum maps.");
                    }
                    else if (rest is [_] && !variable.Required && variable.Default is null && !guardedPorts.Contains($"var.{key}"))
                    {
                        // Unset, there would be no option to take the sub-value from (inside its own condition, there is one).
                        context.Error(path, $"Enum variable '{key}' is used with a sub-value, so it needs a default, to be required or a condition on it.");
                    }
                    else if (variable.Type == VariableType.String && variable.Pattern is null)
                    {
                        context.Error(path, $"String variable '{key}' is used in arguments, the environment or configuration files, so it needs a 'pattern'.");
                    }
                    else if (use == TemplateUse.Environment && variable.Secret)
                    {
                        context.Error(path, $"Secret variable '{key}' cannot go into the environment (child processes inherit it).");
                    }

                    break;
                case ["port", var name, .. var rest] when rest is [] or ["ip"]:
                    if (!context.Ports.TryGetValue(name, out var port))
                    {
                        context.Error(path, $"Unknown port '{name}'.");
                    }
                    else if (!port.Required && !guardedPorts.Contains(name))
                    {
                        context.Error(path, $"Port '{name}' is optional: use it inside {{ \"if\": \"port.{name}\" }} arguments only.");
                    }

                    break;
                case ["rcon", "password"]:
                    if (use == TemplateUse.Environment)
                    {
                        context.Error(path, "The RCON password cannot go into the environment.");
                    }
                    else if (!context.HasRcon)
                    {
                        context.Error(path, "{{rcon.password}} needs an RCON console ('logfile+rcon').");
                    }

                    context.UsesRconPassword = true;
                    break;
                default:
                    context.Error(path, $"Unknown placeholder '{{{{{string.Join('.', placeholder)}}}}}'.");
                    break;
            }
        }

        if (malformed)
        {
            context.Error(path, "Malformed placeholder: use {{var.<key>}}, {{var.<key>.<sub>}}, {{port.<name>}}, {{port.<name>.ip}}, {{rcon.password}}, {{server.shortId}} or {{server.dir}}.");
        }
    }

    private static void ValidateConfigFiles(IReadOnlyList<ConfigFileDefinition> files, Context context)
    {
        if (files.Count > 16)
        {
            context.Error("configFiles", "At most 16 files.");
        }

        // An ini file's sections can be declared one by one (Pulsar 1.0.12): they are patched in turn, each on what the one
        // before wrote. Any other file is declared once.
        foreach (var same in files.GroupBy(f => f.Path.ToUpperInvariant()).Where(g => g.Count() > 1))
        {
            if (same.Any(f => f.Format != ConfigFileFormat.UnrealIni || f.Mode != ConfigFileMode.Patch || f.Struct is not null)
                || same.Select(f => f.Section?.ToUpperInvariant()).Distinct().Count() != same.Count())
            {
                context.Error("configFiles", "Each file can be declared once, except an ini file's sections ('unreal-ini', 'patch', one section each).");
            }
        }

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var path = $"configFiles[{i}]";
            ValidateRelativePath($"{path}.path", file.Path, context.Error);
            if (file.Set.Count is 0 or > 128)
            {
                context.Error($"{path}.set", "Between 1 and 128 keys.");
            }

            if (file.Set.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != file.Set.Count)
            {
                context.Error($"{path}.set", "Keys differ only by case.");
            }

            if (file.KeepIfEmpty is { } kept && (kept.Count > 128 || kept.Any(k => !file.Set.ContainsKey(k)) || kept.Distinct(StringComparer.OrdinalIgnoreCase).Count() != kept.Count))
            {
                context.Error($"{path}.keepIfEmpty", "Keys of 'set', each once.");
            }

            if (file.WriteIfEmpty is { } written && (file.Format != ConfigFileFormat.Json || written.Count > 128 || written.Any(k => !file.Set.ContainsKey(k))
                || written.Distinct(StringComparer.OrdinalIgnoreCase).Count() != written.Count || written.Any(k => file.KeepIfEmpty?.Contains(k, StringComparer.OrdinalIgnoreCase) == true)))
            {
                context.Error($"{path}.writeIfEmpty", "Keys of a 'json' file's 'set', each once, and none kept when empty.");
            }

            if (file.Format == ConfigFileFormat.UnrealIni)
            {
                if (file.Section is null || !UnrealSectionPattern().IsMatch(file.Section))
                {
                    context.Error($"{path}.section", "The section the keys go into, without its brackets ('/Script/Pal.PalGameWorldSettings').");
                }

                if (file.Struct is not null && !UnrealKeyPattern().IsMatch(file.Struct))
                {
                    context.Error($"{path}.struct", "The name of the struct line ('OptionSettings').");
                }
            }
            else if (file.Format == ConfigFileFormat.XmlProperties)
            {
                if (file.Section is null || !XmlElementPattern().IsMatch(file.Section))
                {
                    context.Error($"{path}.section", "The file's root element ('ServerSettings').");
                }

                if (file.Struct is not null)
                {
                    context.Error($"{path}.struct", "'struct' belongs to the 'unreal-ini' format.");
                }

                if (file.Mode != ConfigFileMode.Patch)
                {
                    context.Error($"{path}.mode", "'xml-properties' files are patched (the game's other settings stay).");
                }
            }
            else if (file.Section is not null || file.Struct is not null)
            {
                context.Error(path, "'section' and 'struct' belong to the 'unreal-ini' and 'xml-properties' formats.");
            }

            if (file.Format == ConfigFileFormat.Xml)
            {
                if (file.Mode != ConfigFileMode.Patch)
                {
                    context.Error($"{path}.mode", "'xml' files are patched (the game's other settings stay).");
                }

                // One root: every path starts at the same element, and none is inside another key's element.
                if (file.Set.Keys.Select(k => k.Split('/').ElementAtOrDefault(1)).Distinct(StringComparer.Ordinal).Count() > 1
                    || file.Set.Keys.Any(k => file.Set.Keys.Any(o => o.StartsWith(k + "/", StringComparison.Ordinal))))
                {
                    context.Error($"{path}.set", "Paths from one root element, none inside another key's element.");
                }
            }

            if (file.Format == ConfigFileFormat.Text && (file.Mode != ConfigFileMode.Patch || file.Set.Count != 1 || !file.Set.ContainsKey(ConfigFileWriter.TextKey)))
            {
                context.Error($"{path}.set", "A 'text' file is patched and holds one value: its 'set' has the one key 'content'.");
            }

            if (file.Format == ConfigFileFormat.Json)
            {
                if (file.Mode != ConfigFileMode.Patch)
                {
                    context.Error($"{path}.mode", "'json' files are patched (the game's other settings stay).");
                }

                // "/a" set to a value leaves no object for "/a/b" to go into.
                if (file.Set.Keys.Any(k => file.Set.Keys.Any(o => o.StartsWith(k + "/", StringComparison.OrdinalIgnoreCase))))
                {
                    context.Error($"{path}.set", "A key cannot be inside another key's value ('/a' and '/a/b').");
                }
            }

            ValidateConfigLists(file, path, context);

            foreach (var (key, value) in file.Set)
            {
                var keyPattern = file.Format switch
                {
                    ConfigFileFormat.SourceCfg => SourceCfgKeyPattern(),
                    ConfigFileFormat.UnrealIni => UnrealKeyPattern(),
                    ConfigFileFormat.Json => JsonPointerPattern(),
                    ConfigFileFormat.Xml => XmlPathPattern(),
                    _ => PropertiesKeyPattern(),
                };
                if (!keyPattern.IsMatch(key))
                {
                    context.Error($"{path}.set.{key}", "Invalid key for this format.");
                }

                // Literal text must already be safe; values are checked again once rendered.
                if (ConfigFileWriter.ValueError(file.Format, Placeholders.Strip(value)) is { } error)
                {
                    context.Error($"{path}.set.{key}", error);
                }

                ValidateTemplate($"{path}.set.{key}", value, context, TemplateUse.ConfigValue, guardedPorts: s_noPorts);
            }
        }
    }

    /// <summary>A json file's arrays of ids (Pulsar 1.0.14): each of a list variable, none inside a key or another list.</summary>
    private static void ValidateConfigLists(ConfigFileDefinition file, string path, Context context)
    {
        if (file.Lists is not { Count: > 0 } lists)
        {
            return;
        }

        if (file.Format != ConfigFileFormat.Json || lists.Count > 8)
        {
            context.Error($"{path}.lists", "At most 8 arrays, in a 'json' file.");
            return;
        }

        string[] pointers = [.. file.Set.Keys, .. lists.Keys];
        foreach (var (pointer, list) in lists)
        {
            var at = $"{path}.lists.{pointer}";
            if (!JsonPointerPattern().IsMatch(pointer))
            {
                context.Error(at, "Invalid key for this format.");
            }

            // The array is the list's own: no key writes into it, over it or around it.
            if (pointers.Count(p => p.Equals(pointer, StringComparison.OrdinalIgnoreCase)) > 1
                || pointers.Any(p => p.StartsWith(pointer + "/", StringComparison.OrdinalIgnoreCase) || pointer.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
            {
                context.Error(at, "A list's array is its own: no other key or list is it, or inside it, or holds it.");
            }

            if (!context.Variables.TryGetValue(list.Variable, out var variable) || variable.ListSeparator is null)
            {
                context.Error($"{at}.variable", "A variable that lists Workshop items or catalog projects.");
            }

            if (!JsonMemberPattern().IsMatch(list.Key))
            {
                context.Error($"{at}.key", "The member that holds each id ('modId').");
            }
        }
    }

    private static void ValidateGlobs(string path, IReadOnlyList<string> globs, Action<string, string> error)
    {
        for (var i = 0; i < globs.Count; i++)
        {
            if (globs[i].Length is 0 or > 260 || (RelativePathError(globs[i], allowWildcards: true) ?? PulsarFolderError(globs[i])) is not null)
            {
                error($"{path}[{i}]", "A relative glob ('cfg/**', '**/*.log').");
            }
        }
    }

    /// <summary>The query goes to a port every server has, over the transport the game answers it on.</summary>
    private static void ValidateQuery(QueryDefinition query, Dictionary<string, PortDefinition> ports, Action<string, string> error)
    {
        var transport = query.Protocol == QueryProtocol.Minecraft ? PortProtocol.Tcp : PortProtocol.Udp;
        if (!ports.TryGetValue(query.Port, out var port) || !port.Required || !port.Protocols.Contains(transport))
        {
            error("query.port", $"Must name a required {transport.ToString().ToUpperInvariant()} port of the manifest.");
        }
    }

    private static void ValidateReadiness(ReadinessDefinition readiness, ConsoleDefinition? console, QueryDefinition? query, Action<string, string> error)
    {
        switch (readiness)
        {
            case QueryReadiness queried:
                if (query is null)
                {
                    error("readiness", "Query readiness needs a 'query' section.");
                }

                if (queried.TimeoutSeconds is < 1 or > 3600)
                {
                    error("readiness.timeoutSeconds", "Between 1 and 3600.");
                }

                break;
            case LogPatternReadiness log:
                if (CompileError(log.Pattern) is { } patternError)
                {
                    error("readiness.pattern", patternError);
                }

                if (log.TimeoutSeconds is < 1 or > 3600)
                {
                    error("readiness.timeoutSeconds", "Between 1 and 3600.");
                }

                break;
            case ProcessAliveReadiness alive when alive.GraceSeconds is < 0 or > 600:
                error("readiness.graceSeconds", "Between 0 and 600.");
                break;
            case RconReadiness rcon:
                if (console?.Rcon is null)
                {
                    error("readiness", "RCON readiness needs an RCON console.");
                }

                if (rcon.TimeoutSeconds is < 1 or > 3600)
                {
                    error("readiness.timeoutSeconds", "Between 1 and 3600.");
                }

                break;
        }
    }

    private static void ValidateStop(StopDefinition stop, ConsoleDefinition? console, Action<string, string> error)
    {
        if (stop.TimeoutSeconds is < 1 or > 3600)
        {
            error("stop.timeoutSeconds", "Between 1 and 3600.");
        }

        if (stop.Steps.Count > 16)
        {
            error("stop.steps", "At most 16 steps.");
        }

        var mode = console?.Mode;
        for (var i = 0; i < stop.Steps.Count; i++)
        {
            var path = $"stop.steps[{i}]";
            switch (stop.Steps[i])
            {
                case ConsoleCommandStop command:
                    if (!IsCommand(command.Command))
                    {
                        error($"{path}.command", "A single line of up to 256 characters.");
                    }

                    if (mode == ManifestConsoleMode.LogfileRcon)
                    {
                        error(path, "Console commands need console mode 'pty' or 'pipes'; use 'rconCommand'.");
                    }

                    break;
                case RconCommandStop command:
                    if (!IsCommand(command.Command))
                    {
                        error($"{path}.command", "A single line of up to 256 characters.");
                    }

                    if (console?.Rcon is null)
                    {
                        error(path, "RCON commands need an RCON console.");
                    }

                    break;
                case CtrlCStop when mode == ManifestConsoleMode.LogfileRcon:
                    error(path, "Ctrl+C needs console mode 'pty' or 'pipes' (a game that opens a console of its own).");
                    break;
                case WaitStop wait when wait.Seconds is < 1 or > 300:
                    error($"{path}.seconds", "Between 1 and 300.");
                    break;
            }
        }

    }

    /// <summary>Every console (pseudo console, pipes or RCON) takes commands, so only the commands and waits are checked.</summary>
    private static void ValidateSave(GameSaveDefinition save, Action<string, string> error)
    {
        if (save.Commands.Count is < 1 or > 4)
        {
            error("backup.save.commands", "Between 1 and 4 commands.");
        }

        if ((save.Resume?.Count ?? 0) > 4)
        {
            error("backup.save.resume", "At most 4 commands.");
        }

        foreach (var (list, name) in new[] { (save.Commands, "commands"), (save.Resume ?? [], "resume") })
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (!IsCommand(list[i]))
                {
                    error($"backup.save.{name}[{i}]", "A single line of up to 256 characters.");
                }
            }
        }

        if (save.Confirmation is { } confirmation && CompileError(confirmation) is { } patternError)
        {
            error("backup.save.confirmation", patternError);
        }

        if (save.WaitSeconds is < 0 or > 60)
        {
            error("backup.save.waitSeconds", "Between 0 and 60.");
        }

        if (save.TimeoutSeconds is < 1 or > 300)
        {
            error("backup.save.timeoutSeconds", "Between 1 and 300.");
        }
    }

    private static bool IsCommand(string command) => !string.IsNullOrWhiteSpace(command) && command.Length <= 256 && !command.Any(char.IsControl);

    internal static string? CompileError(string pattern)
    {
        if (pattern.Length > 1024)
        {
            return "At most 1024 characters.";
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"Invalid regular expression (non-backtracking): {ex.Message}";
        }
        catch (NotSupportedException ex)
        {
            return $"Not allowed in a non-backtracking regular expression: {ex.Message}";
        }
    }

    /// <summary>What templates can reference, and what they were seen referencing.</summary>
    private sealed class Context(Dictionary<string, VariableDefinition> variables, Dictionary<string, PortDefinition> ports, Action<string, string> error)
    {
        public Dictionary<string, VariableDefinition> Variables { get; } = variables;

        public Dictionary<string, PortDefinition> Ports { get; } = ports;

        public Action<string, string> Error { get; } = error;

        public bool HasRcon { get; set; }

        public bool UsesRconPassword { get; set; }
    }

    // \z, not $, in every pattern: '$' also matches before a final newline ("hostname\n" would write a second line).
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    /// <summary>A catalog's loader name (Modrinth's <c>neoforge</c>, <c>paper</c>).</summary>
    [GeneratedRegex(@"^[a-z0-9_-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogLoaderPattern();

    /// <summary>A catalog's name for a game (uMod's <c>rust</c>).</summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommunityPattern();

    /// <summary>A Thunderstore package: its namespace and name (<c>denikson-BepInExPack_Valheim</c>).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_]{1,64}-[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    public static partial Regex ThunderstorePackagePattern();

    /// <summary>What a mod's file may end with (<c>.jar</c>, <c>.cs</c>).</summary>
    [GeneratedRegex(@"^\.[a-z0-9]{1,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();

    // [0-9], not \d: other scripts' digits would be ordered as 0 (GameTemplate.CompareVersions).
    [GeneratedRegex(@"^[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}(-[0-9A-Za-z.-]{1,32})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VariableKeyPattern();

    // A player list's JSON property (name, xuid).
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ListFieldPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex OptionValuePattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PortNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BranchPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SourceCfgKeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PropertiesKeyPattern();

    /// <summary>An XML element's path from the root, the root and 1 to 7 names below it (no namespace prefixes).</summary>
    [GeneratedRegex(@"^(/[A-Za-z_][A-Za-z0-9_.-]{0,63}){2,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex XmlPathPattern();

    /// <summary>A JSON Pointer to an object member, 1 to 8 names deep; no escapes (<c>~</c>), so no <c>/</c> inside a name.</summary>
    [GeneratedRegex(@"^(/[A-Za-z0-9_.-]{1,64}){1,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex JsonPointerPattern();

    /// <summary>An object member a list's ids go into (<c>modId</c>).</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex JsonMemberPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentNamePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UnrealKeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_./:-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UnrealSectionPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex XmlElementPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AddonKeyPattern();

    // Neither part is '.' or '..' (a path, not a repository, in the API's URL).
    [GeneratedRegex(@"^(?!\.\.?/)[A-Za-z0-9_.-]{1,100}/(?!\.\.?\z)[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRepoPattern();

    // A channel of Microsoft's release metadata (dotnet/release-metadata/<channel>/releases.json): .NET 5 and later.
    [GeneratedRegex(@"^([5-9]|[1-9][0-9])\.0\z", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetChannelPattern();

    // Device names (the console's CONIN$ and CONOUT$ too, as ServerFilePath has them), matched on the name before its extension.
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    // An 8.3 short name (PULSAR~1), as the agent's file commands read it.
    [GeneratedRegex(@"^[^.~]{1,6}~[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShortName();
}

/// <summary><c>{{a.b.c}}</c> placeholders: nothing else (no functions, no expressions).</summary>
internal static partial class Placeholders
{
    public static IReadOnlyList<string[]> Parse(string template, out bool malformed)
    {
        var found = Pattern().Matches(template).Select(m => m.Groups[1].Value.Split('.')).ToList();
        // Any brace pair the pattern did not consume is a typo, not literal text.
        var rest = Strip(template);
        malformed = rest.Contains("{{", StringComparison.Ordinal) || rest.Contains("}}", StringComparison.Ordinal);
        return found;
    }

    public static string Replace(string template, Func<string[], string> value) => Pattern().Replace(template, m => value(m.Groups[1].Value.Split('.')));

    /// <summary>As <see cref="Replace(string, Func{string[], string})"/>, telling <paramref name="placed"/> where each value went in the result.</summary>
    public static string Replace(string template, Func<string[], string> value, Action<string[], int, int> placed)
    {
        var result = new StringBuilder();
        var last = 0;
        foreach (Match match in Pattern().Matches(template))
        {
            result.Append(template, last, match.Index - last);
            var parts = match.Groups[1].Value.Split('.');
            var text = value(parts);
            placed(parts, result.Length, result.Length + text.Length);
            result.Append(text);
            last = match.Index + match.Length;
        }

        return result.Append(template, last, template.Length - last).ToString();
    }

    /// <summary>The literal text of a template.</summary>
    public static string Strip(string template) => Pattern().Replace(template, "");

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*){1,2})\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
