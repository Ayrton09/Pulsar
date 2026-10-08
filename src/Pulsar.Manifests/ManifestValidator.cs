using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pulsar.Manifests;

/// <summary>Semantic rules beyond the JSON shape (docs/architecture/04, "Validation").</summary>
public static partial class ManifestValidator
{
    public const string WindowsX64 = "windows-x64";

    /// <summary>Manifest key of Pulsar's own test game (<c>games/_test</c>); the only one allowed the test server step.</summary>
    public const string TestGameKey = "pulsar-test";

    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly string[] s_platforms = [WindowsX64];
    private static readonly string[] s_newlines = ["\n", "\r\n", "\r"];
    private static readonly HashSet<string> s_noPorts = [];

    // Set by the agent itself (docs/architecture/04, "Environment").
    private static readonly HashSet<string> s_reservedEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "ComSpec", "SystemRoot", "windir", "PSModulePath", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
    };

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
        }

        ValidateAddons(manifest.AddonList, Error);
        return errors;
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
                    if (!GitHubRepoPattern().IsMatch(github.Repo))
                    {
                        error($"{path}.source.repo", "A GitHub repository, 'owner/name'.");
                    }

                    if (github.Asset.Length is 0 or > 200 || CompileError(github.Asset) is not null)
                    {
                        error($"{path}.source.asset", "A regular expression (non-backtracking, at most 200 characters) for the asset's file name.");
                    }

                    if (github.Tag is { } tag && (!BranchPattern().IsMatch(tag) || github.Prerelease))
                    {
                        error($"{path}.source.tag", "1–64 letters, digits, '_', '-' or '.'; not with 'prerelease'.");
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
        else if (RelativePathError(value) is { } message)
        {
            error(path, message);
        }
    }

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

            if (segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' '))
            {
                return "Segments cannot start with a space or end with a dot or a space.";
            }

            if (segment.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '|' || (!allowWildcards && c is '?' or '*')))
            {
                return "Contains characters Windows does not allow in names.";
            }

            if (ReservedName().IsMatch(segment))
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

            switch (variable.Type)
            {
                case VariableType.String:
                    if (variable.Pattern is { } pattern && CompileError(pattern) is { } patternError)
                    {
                        error($"{path}.pattern", patternError);
                    }

                    if (variable.MaxLength is < 1 or > 4096)
                    {
                        error($"{path}.maxLength", "Between 1 and 4096.");
                    }

                    break;
                case VariableType.Integer:
                    if (variable.Min is null || variable.Max is null || variable.Min > variable.Max)
                    {
                        error(path, "Integer variables need 'min' and 'max' (min ≤ max).");
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

            if (variable.Default is { } value && VariableValues.Check(variable, VariableValues.FromJson(value)) is { } defaultError)
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

                    if (steam.Login != SteamCmdStep.AnonymousLogin)
                    {
                        context.Error($"{path}.login", "Only 'anonymous' is supported.");
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
                case MinecraftServerStep { VersionVar: { } versionVar }
                    when !context.Variables.TryGetValue(versionVar, out var version) || version.Secret
                         || version.Type is not (VariableType.String or VariableType.Enum)
                         || (version.Type == VariableType.String && version.Pattern is null):
                    context.Error($"{path}.versionVar", "Must name a string variable with a 'pattern', or an enum.");
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
                ValidateRelativePath($"{path}.console.logFile", logFile, context.Error);
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
        else if (console.LogFile is not null)
        {
            context.Error($"{path}.console.logFile", "'logFile' belongs to the 'logfile+rcon' mode.");
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
                        case ["var", var key] when context.Variables.TryGetValue(key, out var variable) && variable.Type is VariableType.Boolean or VariableType.String:
                            break;
                        case ["port", var name] when context.Ports.ContainsKey(name):
                            guarded = new HashSet<string>(guardedPorts, StringComparer.Ordinal) { name };
                            break;
                        default:
                            context.Error($"{path}[{i}].if", "Must be 'var.<key>' of a boolean or string variable, or 'port.<name>'.");
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
                    else if (rest is [_] && !variable.Required && variable.Default is null)
                    {
                        // Unset, there would be no option to take the sub-value from.
                        context.Error(path, $"Enum variable '{key}' is used with a sub-value, so it needs a default or to be required.");
                    }
                    else if (use != TemplateUse.Environment && variable.Type == VariableType.String && variable.Pattern is null)
                    {
                        context.Error(path, $"String variable '{key}' is used in arguments or configuration files, so it needs a 'pattern'.");
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

        if (files.Select(f => f.Path.ToUpperInvariant()).Distinct().Count() != files.Count)
        {
            context.Error("configFiles", "Each file can be declared once.");
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

            foreach (var (key, value) in file.Set)
            {
                var keyPattern = file.Format switch
                {
                    ConfigFileFormat.SourceCfg => SourceCfgKeyPattern(),
                    ConfigFileFormat.UnrealIni => UnrealKeyPattern(),
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

    private static void ValidateGlobs(string path, IReadOnlyList<string> globs, Action<string, string> error)
    {
        for (var i = 0; i < globs.Count; i++)
        {
            if (globs[i].Length is 0 or > 260 || RelativePathError(globs[i], allowWildcards: true) is not null)
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
                case CtrlCStop when mode != ManifestConsoleMode.Pty:
                    error(path, "Ctrl+C needs console mode 'pty'.");
                    break;
                case WaitStop wait when wait.Seconds is < 1 or > 300:
                    error($"{path}.seconds", "Between 1 and 300.");
                    break;
            }
        }

        static bool IsCommand(string command) => !string.IsNullOrWhiteSpace(command) && command.Length <= 256 && !command.Any(char.IsControl);
    }

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

    [GeneratedRegex(@"^\d{1,6}\.\d{1,6}\.\d{1,6}(-[0-9A-Za-z.-]{1,32})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VariableKeyPattern();

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

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRepoPattern();

    // Device names (the console's CONIN$ and CONOUT$ too, as ServerFilePath has them), with or without an extension.
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
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

    /// <summary>The literal text of a template.</summary>
    public static string Strip(string template) => Pattern().Replace(template, "");

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*){1,2})\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
