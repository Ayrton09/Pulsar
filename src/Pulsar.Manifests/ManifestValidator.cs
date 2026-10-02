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

    // Set by the agent itself (docs/architecture/04, "Environment").
    private static readonly HashSet<string> s_reservedEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "ComSpec", "SystemRoot", "windir", "PSModulePath", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
    };

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

        if (manifest.Platforms.Count == 0 || manifest.Platforms.Any(p => !s_platforms.Contains(p)) || manifest.Platforms.Distinct().Count() != manifest.Platforms.Count)
        {
            Error("platforms", $"Supported platforms: {string.Join(", ", s_platforms)}.");
        }

        foreach (var platform in manifest.Runtime.Keys.Where(p => !manifest.Platforms.Contains(p)))
        {
            Error($"runtime.{platform}", "Runtime for a platform the manifest does not declare.");
        }

        var variables = ValidateVariables(manifest.VariableList, Error);
        if (manifest.Install is { } install)
        {
            for (var i = 0; i < install.Steps.Count; i++)
            {
                var path = $"install.steps[{i}]";
                switch (install.Steps[i])
                {
                    case EnsureDirectoryStep step:
                        ValidateRelativePath($"{path}.path", step.Path, Error);
                        break;
                    case TestGameServerStep step:
                        ValidateRelativePath($"{path}.path", step.Path, Error);
                        if (manifest.Key != TestGameKey)
                        {
                            Error(path, $"Only the '{TestGameKey}' manifest can install the test game server.");
                        }

                        break;
                }
            }
        }

        ManifestConsoleMode? mode = null;
        foreach (var platform in manifest.Platforms)
        {
            if (!manifest.Runtime.TryGetValue(platform, out var runtime))
            {
                Error("runtime", $"Missing the runtime for {platform}.");
                continue;
            }

            mode = runtime.Console.Mode;
            ValidateRuntime($"runtime.{platform}", runtime, variables, Error);
        }

        ValidateReadiness(manifest.Readiness, Error);
        ValidateStop(manifest.Stop, mode, Error);
        return errors;
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

    internal static string? RelativePathError(string path)
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

            if (segment.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*'))
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

            if (string.IsNullOrWhiteSpace(variable.Label))
            {
                error($"{path}.label", "Required.");
            }

            if (variable.Secret && variable.Default is not null)
            {
                error($"{path}.default", "Secret variables cannot have a default value.");
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

                    break;
            }

            if (variable.Default is { } value && VariableValues.Check(variable, VariableValues.FromJson(value)) is { } defaultError)
            {
                error($"{path}.default", defaultError);
            }
        }

        return byKey;
    }

    private static void ValidateRuntime(string path, RuntimeDefinition runtime, Dictionary<string, VariableDefinition> variables, Action<string, string> error)
    {
        ValidateRelativePath($"{path}.executable", runtime.Executable, error);
        if (IsValidRelativePath(runtime.Executable) && !runtime.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error($"{path}.executable", "Only .exe files can be started (no scripts, installers or interpreters).");
        }

        if (runtime.WorkingDirectory is { } workingDirectory)
        {
            ValidateRelativePath($"{path}.workingDirectory", workingDirectory, error);
        }

        ValidateArguments($"{path}.arguments", runtime.ArgumentList, variables, error);
        foreach (var (name, value) in runtime.Environment ?? new Dictionary<string, string>())
        {
            if (s_reservedEnvironment.Contains(name) || !EnvironmentNamePattern().IsMatch(name))
            {
                error($"{path}.environment.{name}", "Reserved or invalid environment variable name.");
            }

            ValidateTemplate($"{path}.environment.{name}", value, variables, error);
        }

        var console = runtime.Console;
        if (console.Mode == ManifestConsoleMode.LogfileRcon)
        {
            error($"{path}.console.mode", "'logfile+rcon' is not supported by this version of Pulsar yet.");
        }

        if (console.InputNewline is { } newline && !s_newlines.Contains(newline))
        {
            error($"{path}.console.inputNewline", "Use \"\\n\", \"\\r\\n\" or \"\\r\".");
        }

        if (console.Mode == ManifestConsoleMode.Pty && console.Encoding != ManifestConsoleEncoding.Utf8)
        {
            error($"{path}.console.encoding", "A pseudo console always produces UTF-8.");
        }
    }

    private static void ValidateArguments(string path, IReadOnlyList<ArgumentTemplate> arguments, Dictionary<string, VariableDefinition> variables,
        Action<string, string> error)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case ArgumentText text:
                    ValidateTemplate($"{path}[{i}]", text.Text, variables, error, argument: true);
                    break;
                case ArgumentCondition condition:
                    var key = condition.If.StartsWith("var.", StringComparison.Ordinal) ? condition.If[4..] : null;
                    if (key is null || !variables.TryGetValue(key, out var variable) || variable.Type is not (VariableType.Boolean or VariableType.String))
                    {
                        error($"{path}[{i}].if", "Must be 'var.<key>' of a boolean or string variable.");
                    }

                    ValidateArguments($"{path}[{i}].then", condition.Then, variables, error);
                    break;
            }
        }
    }

    private static void ValidateTemplate(string path, string template, Dictionary<string, VariableDefinition> variables, Action<string, string> error,
        bool argument = false)
    {
        var placeholders = Placeholders.Parse(template, out var malformed);
        foreach (var placeholder in placeholders)
        {
            switch (placeholder)
            {
                case ["server", "shortId"]:
                    break;
                case ["var", var key, .. var rest] when rest.Length <= 1:
                    if (!variables.TryGetValue(key, out var variable))
                    {
                        error(path, $"Unknown variable '{key}'.");
                    }
                    else if (rest is [var sub] && (variable.Type != VariableType.Enum || variable.Options!.Any(o => o.Map?.ContainsKey(sub) != true)))
                    {
                        error(path, $"'{key}.{sub}' is not a sub-value every option of the enum maps.");
                    }
                    else if (argument && variable.Type == VariableType.String && variable.Pattern is null)
                    {
                        error(path, $"String variable '{key}' is used in arguments, so it needs a 'pattern'.");
                    }

                    break;
                case ["port", ..]:
                    error(path, "Port placeholders are not supported by this version of Pulsar yet.");
                    break;
                default:
                    error(path, $"Unknown placeholder '{{{{{string.Join('.', placeholder)}}}}}'.");
                    break;
            }
        }

        if (malformed)
        {
            error(path, "Malformed placeholder: use {{var.<key>}}, {{var.<key>.<sub>}} or {{server.shortId}}.");
        }
    }

    private static void ValidateReadiness(ReadinessDefinition readiness, Action<string, string> error)
    {
        switch (readiness)
        {
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
        }
    }

    private static void ValidateStop(StopDefinition stop, ManifestConsoleMode? mode, Action<string, string> error)
    {
        if (stop.TimeoutSeconds is < 1 or > 3600)
        {
            error("stop.timeoutSeconds", "Between 1 and 3600.");
        }

        for (var i = 0; i < stop.Steps.Count; i++)
        {
            var path = $"stop.steps[{i}]";
            switch (stop.Steps[i])
            {
                case ConsoleCommandStop command when string.IsNullOrWhiteSpace(command.Command) || command.Command.Length > 256
                    || command.Command.Any(char.IsControl):
                    error($"{path}.command", "A single line of up to 256 characters.");
                    break;
                case CtrlCStop when mode != ManifestConsoleMode.Pty:
                    error(path, "Ctrl+C needs console mode 'pty'.");
                    break;
                case WaitStop wait when wait.Seconds is < 1 or > 300:
                    error($"{path}.seconds", "Between 1 and 300.");
                    break;
            }
        }
    }

    internal static string? CompileError(string pattern)
    {
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

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^\d{1,6}\.\d{1,6}\.\d{1,6}(-[0-9A-Za-z.-]{1,32})?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableKeyPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentNamePattern();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}

/// <summary><c>{{a.b.c}}</c> placeholders: nothing else (no functions, no expressions).</summary>
internal static partial class Placeholders
{
    public static IReadOnlyList<string[]> Parse(string template, out bool malformed)
    {
        var found = Pattern().Matches(template).Select(m => m.Groups[1].Value.Split('.')).ToList();
        // Any brace pair the pattern did not consume is a typo, not literal text.
        malformed = Pattern().Replace(template, "").Contains("{{", StringComparison.Ordinal) || Pattern().Replace(template, "").Contains("}}", StringComparison.Ordinal);
        return found;
    }

    public static string Replace(string template, Func<string[], string> value) => Pattern().Replace(template, m => value(m.Groups[1].Value.Split('.')));

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*){1,2})\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
