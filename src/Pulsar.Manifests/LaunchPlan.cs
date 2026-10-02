using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pulsar.Manifests;

/// <summary>
/// What the agent starts: a manifest rendered for one server. Built only by <see cref="LaunchPlanRenderer"/>; the panel
/// never sends a command line or an executable path of its own (docs/architecture/03).
/// </summary>
public sealed record LaunchPlan(
    string Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    ConsoleDefinition Console,
    ReadinessDefinition Readiness,
    StopDefinition Stop);

/// <summary>A value that cannot be used for a variable, or a manifest that cannot run on a platform.</summary>
public sealed class LaunchPlanException : Exception
{
    public LaunchPlanException()
        : base("The manifest cannot be rendered.")
    {
    }

    public LaunchPlanException(string message)
        : base(message)
    {
    }

    public LaunchPlanException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Variable values in their canonical text form ("10", "true", "competitive") and their checks.</summary>
public static class VariableValues
{
    /// <summary>The manifest defaults overlaid with <paramref name="values"/>, each checked against its definition.</summary>
    public static IReadOnlyDictionary<string, string?> Resolve(GameManifest manifest, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(values);
        var definitions = manifest.VariableList.ToDictionary(v => v.Key, StringComparer.Ordinal);
        if (values.Keys.FirstOrDefault(k => !definitions.ContainsKey(k)) is { } unknown)
        {
            throw new LaunchPlanException($"Unknown variable '{unknown}'.");
        }

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var variable in manifest.VariableList)
        {
            var value = values.TryGetValue(variable.Key, out var given) ? given : variable.Default is { } d ? FromJson(d) : null;
            if (Check(variable, value) is { } error)
            {
                throw new LaunchPlanException($"{variable.Key}: {error}");
            }

            resolved[variable.Key] = value;
        }

        return resolved;
    }

    /// <summary>Null when <paramref name="value"/> is acceptable for <paramref name="variable"/>, otherwise why not.</summary>
    public static string? Check(VariableDefinition variable, string? value)
    {
        ArgumentNullException.ThrowIfNull(variable);
        if (string.IsNullOrEmpty(value))
        {
            return variable.Required ? "A value is required." : null;
        }

        if (value.Any(char.IsControl))
        {
            return "Control characters are not allowed.";
        }

        switch (variable.Type)
        {
            case VariableType.String:
                if (value.Length > (variable.MaxLength ?? 256))
                {
                    return $"At most {variable.MaxLength ?? 256} characters.";
                }

                if (variable.Pattern is { } pattern && !Regex.IsMatch(value, pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                        ManifestValidator.RegexTimeout))
                {
                    return "Does not match the allowed format.";
                }

                return null;
            case VariableType.Integer:
                return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                    && number >= variable.Min && number <= variable.Max
                    ? null
                    : $"An integer between {variable.Min} and {variable.Max}.";
            case VariableType.Boolean:
                return value is "true" or "false" ? null : "Either true or false.";
            case VariableType.Enum:
                return variable.Options?.Any(o => o.Value == value) == true ? null : "Not one of the options.";
            default:
                return "Unknown variable type.";
        }
    }

    internal static string? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => value.GetRawText(),
    };
}

/// <summary>Renders a manifest's runtime for one server (docs/architecture/04, "Argument templates").</summary>
public static class LaunchPlanRenderer
{
    /// <param name="values">Variable values (defaults fill the gaps); checked again here, whoever sent them.</param>
    /// <param name="extraArguments">Added at the end, one argv element each, when the manifest allows them.</param>
    public static LaunchPlan Render(GameManifest manifest, string platform, IReadOnlyDictionary<string, string?> values, string shortId,
        IReadOnlyList<string>? extraArguments = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.Runtime.TryGetValue(platform, out var runtime))
        {
            throw new LaunchPlanException($"The manifest has no runtime for {platform}.");
        }

        var resolved = VariableValues.Resolve(manifest, values);
        var definitions = manifest.VariableList.ToDictionary(v => v.Key, StringComparer.Ordinal);

        string Placeholder(string[] parts) => parts switch
        {
            ["server", "shortId"] => shortId,
            ["var", var key] => resolved[key] ?? "",
            ["var", var key, var sub] => definitions[key].Options!.Single(o => o.Value == resolved[key]).Map![sub],
            _ => throw new LaunchPlanException($"Unsupported placeholder {string.Join('.', parts)}."),
        };

        var arguments = new List<string>();
        void Add(IReadOnlyList<ArgumentTemplate> templates)
        {
            foreach (var template in templates)
            {
                switch (template)
                {
                    case ArgumentText text:
                        arguments.Add(Placeholders.Replace(text.Text, parts =>
                        {
                            var value = Placeholder(parts);
                            if (parts is ["var", var key] && definitions[key].Type == VariableType.String)
                            {
                                CheckArgumentValue(value);
                            }

                            return value;
                        }));
                        break;
                    case ArgumentCondition condition:
                        if (resolved.GetValueOrDefault(condition.If[4..]) is { Length: > 0 } flag && flag != "false")
                        {
                            Add(condition.Then);
                        }

                        break;
                }
            }
        }

        Add(runtime.ArgumentList);
        foreach (var extra in extraArguments ?? [])
        {
            if (!runtime.AllowExtraArguments)
            {
                throw new LaunchPlanException("This game does not allow extra arguments.");
            }

            if (extra.Length > 512 || extra.Any(char.IsControl))
            {
                throw new LaunchPlanException("Extra arguments are single lines of up to 512 characters.");
            }

            arguments.Add(extra);
        }

        var environment = (runtime.Environment ?? new Dictionary<string, string>())
            .ToDictionary(e => e.Key, e => Placeholders.Replace(e.Value, Placeholder), StringComparer.OrdinalIgnoreCase);
        return new LaunchPlan(runtime.Executable, runtime.WorkingDirectory ?? "", arguments, environment, runtime.Console, manifest.Readiness, manifest.Stop);
    }

    /// <summary>
    /// A string value placed in an argument never becomes a flag or a Source console command (docs/architecture/04),
    /// whatever pattern the manifest declared.
    /// </summary>
    private static void CheckArgumentValue(string value)
    {
        if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || value[0] is '-' or '+'))
        {
            throw new LaunchPlanException($"An argument value cannot start with '-', '+' or whitespace: '{value}'.");
        }
    }
}
