namespace Pulsar.Agent;

/// <summary>
/// <c>PulsarAgent &lt;command&gt; [subcommand] [--option value] [--flag]</c>. Deliberately small: the agent has four
/// commands and must not pull a parsing library into the service.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string command, string? subcommand)
    {
        Command = command;
        Subcommand = subcommand;
    }

    public string Command { get; }

    public string? Subcommand { get; }

    public static CommandLine Parse(string[] args, IReadOnlySet<string> flags)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            return new CommandLine(args.Length == 0 ? "help" : args[0].TrimStart('-'), null);
        }

        var index = 1;
        string? subcommand = null;
        if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            subcommand = args[1];
            index = 2;
        }

        var line = new CommandLine(args[0].ToLowerInvariant(), subcommand?.ToLowerInvariant());
        for (; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                throw new UsageException($"Unexpected argument '{arg}'.");
            }

            var name = arg[2..];
            if (flags.Contains(name))
            {
                line._options[name] = null;
                continue;
            }

            // The next argument is the value even if it starts with dashes: enrollment tokens are base64url and can.
            if (index + 1 >= args.Length)
            {
                throw new UsageException($"--{name} needs a value.");
            }

            line._options[name] = args[++index];
        }

        return line;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.GetValueOrDefault(name);

    public string Required(string name) =>
        Value(name) is { Length: > 0 } value ? value : throw new UsageException($"--{name} is required.");

    /// <summary>Rejects options the command does not know (a typo must not silently change behavior).</summary>
    public void Allow(params string[] names)
    {
        var unknown = _options.Keys.Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            throw new UsageException($"Unknown option{(unknown.Count > 1 ? "s" : string.Empty)} for '{Command}': {string.Join(", ", unknown.Select(u => "--" + u))}.");
        }
    }
}

internal sealed class UsageException(string message) : Exception(message);
