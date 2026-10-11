using System.Globalization;
using System.Net;
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
    StopDefinition Stop)
{
    /// <summary>The server's allocations, one per manifest port it was given.</summary>
    public IReadOnlyList<BoundPort> Ports { get; init; } = [];

    /// <summary>Every port the manifest declares, bound or not (firewall rules of unbound ones are removed).</summary>
    public IReadOnlyList<string> PortNames { get; init; } = [];

    /// <summary>Written before every start (<see cref="ConfigFileWriter"/>).</summary>
    public IReadOnlyList<RenderedConfigFile> ConfigFiles { get; init; } = [];

    /// <summary>Where and how the agent reaches the game's remote console (<c>logfile+rcon</c>).</summary>
    public RconEndpoint? Rcon { get; init; }

    /// <summary>Where and how the agent asks the running game for its name, map and players.</summary>
    public QueryEndpoint? Query { get; init; }

    /// <summary>The lines the installed addons need in the game's files, kept before every start (<see cref="LineEditor"/>).</summary>
    public IReadOnlyList<LineEdit> LineEdits { get; init; } = [];
}

/// <summary>An <c>ip:port</c> pair of a node given to a server (both protocols). <c>0.0.0.0</c> means every interface.</summary>
public sealed record PortAssignment(string IpAddress, int Port)
{
    public const string AnyAddress = "0.0.0.0";

    public bool IsValid => Port is >= 1 and <= 65535 && IPAddress.TryParse(IpAddress, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && ip.ToString() == IpAddress;
}

public sealed record BoundPort(string Name, string IpAddress, int Port, IReadOnlyList<PortProtocol> Protocols);

/// <param name="Address">Loopback when the game listens on every interface.</param>
public sealed record RconEndpoint(RconProtocol Protocol, string Address, int Port, string Password)
{
    // The password is a credential: never in logs or exception messages.
    public override string ToString() => $"{Protocol} rcon at {Address}:{Port}";
}

/// <param name="Address">
/// <see cref="PortAssignment.AnyAddress"/> when the game listens on every interface: the agent asks where it answers
/// (srcds ignores queries from loopback).
/// </param>
public sealed record QueryEndpoint(QueryProtocol Protocol, string Address, int Port);

/// <summary>One line a configuration file gets; an empty <see cref="Value"/> removes the key.</summary>
public sealed record ConfigEntry(string Key, string Value, bool Secret)
{
    public override string ToString() => Secret ? $"{Key}=***" : $"{Key}={Value}";
}

public sealed record RenderedConfigFile(string Path, ConfigFileFormat Format, ConfigFileMode Mode, IReadOnlyList<ConfigEntry> Entries)
{
    /// <inheritdoc cref="ConfigFileDefinition.Section"/>
    public string? Section { get; init; }

    /// <inheritdoc cref="ConfigFileDefinition.Struct"/>
    public string? Struct { get; init; }
}

/// <summary>Everything a manifest is rendered with for one server.</summary>
/// <param name="Values">Variable values (defaults fill the gaps); checked again by the renderer, whoever sent them.</param>
/// <param name="Ports">Allocation of each manifest port (optional ports may be missing).</param>
/// <param name="ExtraArguments">Added at the end, one argv element each, when the manifest allows them.</param>
/// <param name="RconPassword">Generated and kept by the agent; required when the manifest has an RCON console.</param>
/// <param name="Directory">The server's absolute folder, known only to the agent; required by {{server.dir}}.</param>
/// <param name="Addons">Keys of the manifest's addons installed on the server.</param>
public sealed record ServerBinding(
    string ShortId,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyDictionary<string, PortAssignment>? Ports = null,
    IReadOnlyList<string>? ExtraArguments = null,
    string? RconPassword = null,
    string? Directory = null,
    IReadOnlyList<string>? Addons = null)
{
    public override string ToString() => $"ServerBinding {{ ShortId = {ShortId} }}";
}

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

            // Integers in their plain form: "+8" after "+maxplayers" would be read as a command of its own.
            if (variable.Type == VariableType.Integer && !string.IsNullOrEmpty(value))
            {
                value = long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
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

                if (variable.Pattern is not { } pattern)
                {
                    return null;
                }

                try
                {
                    return Regex.IsMatch(value, WholePattern(pattern), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, ManifestValidator.RegexTimeout)
                        ? null
                        : "Does not match the allowed format.";
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    // Refused when the manifest is validated; never an unhandled error for a manifest that skipped it.
                    return "The template's format for this value is not a valid regular expression.";
                }
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

    /// <summary>
    /// A string variable's pattern as it runs: the whole value, anchored or not (a substring match would accept
    /// anything with one matching character). The validator compiles this same text.
    /// </summary>
    internal static string WholePattern(string pattern) => $@"\A(?:{pattern})\z";

    /// <summary>A default value in its canonical text form.</summary>
    public static string? FromJson(JsonElement value) => value.ValueKind switch
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
    public static LaunchPlan Render(GameManifest manifest, string platform, ServerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(binding);
        if (!manifest.Runtime.TryGetValue(platform, out var runtime))
        {
            throw new LaunchPlanException($"The manifest has no runtime for {platform}.");
        }

        var resolved = VariableValues.Resolve(manifest, binding.Values);
        var definitions = manifest.VariableList.ToDictionary(v => v.Key, StringComparer.Ordinal);
        var ports = BindPorts(manifest, binding.Ports ?? new Dictionary<string, PortAssignment>());
        var rcon = runtime.Console.Rcon is { } rconDefinition ? RconEndpointFor(rconDefinition, ports, binding.RconPassword) : null;

        string Placeholder(string[] parts) => parts switch
        {
            ["server", "shortId"] => binding.ShortId,
            ["server", "dir"] => binding.Directory ?? throw new LaunchPlanException("The server folder is not known."),
            ["var", var key] => resolved[key] ?? "",
            ["var", var key, var sub] => definitions[key].Options?.FirstOrDefault(o => o.Value == resolved[key])?.Map?.GetValueOrDefault(sub)
                ?? throw new LaunchPlanException($"'{key}' has no value to take '{sub}' from."),
            ["port", var name] => ports.TryGetValue(name, out var port) ? port.Port.ToString(CultureInfo.InvariantCulture) : throw NoAllocation(name),
            ["port", var name, "ip"] => ports.TryGetValue(name, out var port) ? port.IpAddress : throw NoAllocation(name),
            ["rcon", "password"] => rcon?.Password ?? throw new LaunchPlanException("The manifest has no RCON console."),
            _ => throw new LaunchPlanException($"Unsupported placeholder {string.Join('.', parts)}."),
        };

        bool IsSecret(string template) => Placeholders.Parse(template, out _).Any(p => p is ["rcon", "password"]
            || (p is ["var", var key, ..] && definitions.TryGetValue(key, out var variable) && variable.Secret));

        var reserved = ReservedArgumentNames(runtime.ArgumentList);
        var commands = IsCommandLine(runtime, reserved);
        var arguments = new List<string>();
        void Add(IReadOnlyList<ArgumentTemplate> templates)
        {
            foreach (var template in templates)
            {
                switch (template)
                {
                    case ArgumentText text:
                        var values = new List<(int Start, int End, bool Secret)>();
                        var argument = Placeholders.Replace(text.Text, Placeholder, (parts, start, end) =>
                        {
                            if (parts is ["var", var key] && definitions[key].Type == VariableType.String)
                            {
                                values.Add((start, end, definitions[key].Secret));
                            }
                        });
                        foreach (var (start, end, secret) in values)
                        {
                            CheckArgumentValue(argument, start, end, secret, commands);
                        }

                        arguments.Add(argument);
                        break;
                    case ArgumentCondition condition:
                        var holds = condition.If.Split('.') switch
                        {
                            ["var", var key] => resolved.GetValueOrDefault(key) is { Length: > 0 } flag && flag != "false",
                            ["port", var name] => ports.ContainsKey(name),
                            _ => false,
                        };
                        if (holds)
                        {
                            Add(condition.Then);
                        }
                        else if (condition.Else is { } otherwise)
                        {
                            Add(otherwise);
                        }

                        break;
                }
            }
        }

        Add(runtime.ArgumentList);
        foreach (var extra in binding.ExtraArguments ?? [])
        {
            if (!runtime.AllowExtraArguments)
            {
                throw new LaunchPlanException("This game does not allow extra arguments.");
            }

            if (extra.Length is 0 or > 512 || extra.Any(char.IsControl))
            {
                throw new LaunchPlanException("Extra arguments are single lines of 1 to 512 characters.");
            }

            CheckExtraArgument(extra, runtime, reserved, commands);
            arguments.Add(extra);
        }

        var environment = (runtime.Environment ?? new Dictionary<string, string>())
            .ToDictionary(e => e.Key, e => Placeholders.Replace(e.Value, Placeholder), StringComparer.OrdinalIgnoreCase);

        var configFiles = manifest.ConfigFileList.Select(file => new RenderedConfigFile(file.Path, file.Format, file.Mode,
        [
            .. file.Set.Select(entry =>
            {
                var value = Placeholders.Replace(entry.Value, Placeholder);
                var secret = IsSecret(entry.Value);
                if (ConfigFileWriter.ValueError(file.Format, value) is { } error)
                {
                    throw new LaunchPlanException(secret ? $"{file.Path} {entry.Key}: {error}" : $"{file.Path} {entry.Key}: {error} ('{value}')");
                }

                return new ConfigEntry(entry.Key, value, secret);
            }),
        ])
        { Section = file.Section, Struct = file.Struct }).ToList();

        return new LaunchPlan(runtime.Executable, runtime.WorkingDirectory ?? "", arguments, environment, runtime.Console, manifest.Readiness, manifest.Stop)
        {
            // Internal ports (a console only the agent uses) are never opened in the firewall.
            Ports = [.. manifest.PortList.Where(p => ports.ContainsKey(p.Name) && !p.Internal).Select(p => new BoundPort(p.Name, ports[p.Name].IpAddress, ports[p.Name].Port, p.Protocols))],
            PortNames = [.. manifest.PortList.Select(p => p.Name)],
            ConfigFiles = configFiles,
            Rcon = rcon,
            Query = manifest.Query is { } query ? QueryEndpointFor(query, ports) : null,
            LineEdits = [.. manifest.AddonList.Where(a => binding.Addons?.Contains(a.Key) == true).SelectMany(a => a.LineEditList)],
        };
    }

    /// <summary>Whether <paramref name="binding"/> renders, without the agent's own RCON password (the panel's check).</summary>
    public static string? Check(GameManifest manifest, string platform, ServerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        try
        {
            // The panel checks without the agent's secrets and paths: stand-ins take their place.
            _ = Render(manifest, platform, binding with
            {
                RconPassword = binding.RconPassword ?? new string('x', 32),
                Directory = binding.Directory ?? "C:/Pulsar/servers/" + binding.ShortId,
            });
            return null;
        }
        catch (LaunchPlanException ex)
        {
            return ex.Message;
        }
    }

    private static Dictionary<string, PortAssignment> BindPorts(GameManifest manifest, IReadOnlyDictionary<string, PortAssignment> given)
    {
        var definitions = manifest.PortList.ToDictionary(p => p.Name, StringComparer.Ordinal);
        if (given.Keys.FirstOrDefault(k => !definitions.ContainsKey(k)) is { } unknown)
        {
            throw new LaunchPlanException($"Unknown port '{unknown}'.");
        }

        if (definitions.Values.FirstOrDefault(p => p.Required && !given.ContainsKey(p.Name)) is { } missing)
        {
            throw NoAllocation(missing.Name);
        }

        if (given.FirstOrDefault(p => !p.Value.IsValid) is { Key: not null } invalid)
        {
            throw new LaunchPlanException($"Port '{invalid.Key}' has an invalid allocation.");
        }

        if (given.Values.GroupBy(p => p.Port).Any(g => g.Count() > 1))
        {
            throw new LaunchPlanException("Two ports of the server have the same port number.");
        }

        foreach (var (name, assignment) in given)
        {
            if (definitions[name].Follows is { } follow
                && (!given.TryGetValue(follow.Port, out var followed) || followed.IpAddress != assignment.IpAddress || assignment.Port != followed.Port + follow.Offset))
            {
                throw new LaunchPlanException($"Port '{name}' must be port '{follow.Port}' + {follow.Offset} on the same address.");
            }
        }

        return given.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    private static QueryEndpoint QueryEndpointFor(QueryDefinition definition, Dictionary<string, PortAssignment> ports)
    {
        if (!ports.TryGetValue(definition.Port, out var port))
        {
            throw NoAllocation(definition.Port);
        }

        return new QueryEndpoint(definition.Protocol, port.IpAddress, port.Port);
    }

    private static RconEndpoint RconEndpointFor(RconDefinition definition, Dictionary<string, PortAssignment> ports, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new LaunchPlanException("The RCON password is missing.");
        }

        if (!ports.TryGetValue(definition.Port, out var port))
        {
            throw NoAllocation(definition.Port);
        }

        var address = port.IpAddress == PortAssignment.AnyAddress ? IPAddress.Loopback.ToString() : port.IpAddress;
        return new RconEndpoint(definition.Protocol, address, port.Port, password);
    }

    // The manifest's own switches and commands ("-port", "+rcon_password"…): extra arguments come after them and would win.
    private static HashSet<string> ReservedArgumentNames(IReadOnlyList<ArgumentTemplate> templates)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            switch (template)
            {
                case ArgumentText { Text: ['-' or '+', ..] text } when !ArgumentName(text).Contains("{{", StringComparison.Ordinal):
                    names.Add(ArgumentName(text));
                    break;
                case ArgumentCondition condition:
                    names.UnionWith(ReservedArgumentNames(condition.Then));
                    names.UnionWith(ReservedArgumentNames(condition.Else ?? []));
                    break;
            }
        }

        return names;
    }

    /// <summary>
    /// An extra argument may not set what the manifest sets, nor a sensitive setting (it would be kept in clear in the
    /// server's settings). On RCON consoles (Source), Pulsar's RCON is off limits too, and ';' would start another command.
    /// </summary>
    private static void CheckExtraArgument(string extra, RuntimeDefinition runtime, HashSet<string> reserved, bool commands)
    {
        if (commands && extra.Contains(';', StringComparison.Ordinal))
        {
            throw new LaunchPlanException($"The extra argument '{extra}' has a ';', which starts another command.");
        }

        // Every word: one argument may still be split at spaces by the game.
        foreach (var word in extra.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word is not ['-' or '+', ..])
            {
                continue;
            }

            var name = ArgumentName(word);
            var setting = name.TrimStart('-', '+');
            if (reserved.Contains(name)
                || (runtime.Console.SensitiveCommandPrefixes ?? []).Any(p => setting.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || (commands && setting.Contains("rcon", StringComparison.OrdinalIgnoreCase)))
            {
                throw new LaunchPlanException($"The extra argument '{name}' is set by Pulsar or by the game's settings.");
            }
        }
    }

    /// <summary>
    /// Source-like command lines ('+' arguments are console commands, as on RCON consoles): ';' starts another command
    /// there, a '"' ends the quoted text, and RCON is Pulsar's.
    /// </summary>
    private static bool IsCommandLine(RuntimeDefinition runtime, HashSet<string> reserved) =>
        runtime.Console.Mode == ManifestConsoleMode.LogfileRcon || reserved.Any(n => n.StartsWith('+'));

    // "-port=27015" and "-port 27015" set the same thing: the name is what comes before '=' or a space.
    private static string ArgumentName(string text) => text.Split(' ', '=')[0];

    private static LaunchPlanException NoAllocation(string port) => new($"Port '{port}' has no allocation.");

    /// <summary>
    /// A string value placed in an argument never becomes a flag or a Source console command (docs/architecture/04),
    /// whatever pattern the manifest declared: it is one argument, but a game may split it at spaces, so no word of it
    /// may read as one either ('-' or '+' before a letter; "EU - 24/7" and "+1" stay text), with the template's text
    /// after it ("x -" before "?Port=7777"); on command lines it has no ';' or '"' either.
    /// </summary>
    /// <param name="argument">The rendered argument; the value is <c>argument[start..end]</c>.</param>
    private static void CheckArgumentValue(string argument, int start, int end, bool secret, bool commands)
    {
        var value = argument[start..end];
        if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || value[0] is '-' or '+'))
        {
            throw new LaunchPlanException(secret
                ? "An argument value cannot start with '-', '+' or whitespace."
                : $"An argument value cannot start with '-', '+' or whitespace: '{value}'.");
        }

        if (commands && value.AsSpan().IndexOfAny(';', '"') >= 0)
        {
            throw new LaunchPlanException(secret
                ? "An argument value of this game cannot have a ';' or a '\"': it would start another console command."
                : $"An argument value of this game cannot have a ';' or a '\"': it would start another console command: '{value}'.");
        }

        // Up to the first character after the value: a word the value's last space starts in the template's text counts.
        for (var i = start + 1; i <= end && i < argument.Length - 1; i++)
        {
            if (argument[i] is '-' or '+' && char.IsWhiteSpace(argument[i - 1]) && !char.IsWhiteSpace(argument[i + 1]) && !char.IsAsciiDigit(argument[i + 1]))
            {
                throw new LaunchPlanException(secret
                    ? "No word of an argument value can start with '-' or '+'."
                    : $"No word of an argument value can start with '-' or '+': '{value}'.");
            }
        }
    }
}
