using System.Text;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// Builds a Windows command line from argv so that <c>CommandLineToArgvW</c> and the MSVCRT parser (what almost every
/// game uses) give back exactly the same arguments: one element stays one argument, whatever quotes, spaces or
/// backslashes it contains (docs/architecture/04, "Argument templates"). Never passed through <c>cmd.exe</c>.
/// </summary>
internal static class WindowsCommandLine
{
    public static string Build(string executable, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var builder = new StringBuilder();
        // argv[0] follows simpler rules (no backslash escapes); a path cannot contain quotes anyway.
        builder.Append('"').Append(executable).Append('"');
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            Append(builder, argument);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string argument)
    {
        if (argument.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Arguments cannot contain NUL characters.", nameof(argument));
        }

        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                // Backslashes before the closing quote are doubled so they do not escape it.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        builder.Append('"');
    }
}
