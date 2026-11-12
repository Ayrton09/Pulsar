namespace Pulsar.Manifests;

/// <summary>The same sensitive-command policy for audit records and agent-generated console echoes.</summary>
public static class ConsoleCommandRedaction
{
    public static string Redact(string line, IReadOnlyList<string> sensitivePrefixes)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(sensitivePrefixes);
        var commands = line.Split(';');
        var redacted = false;
        for (var i = 0; i < commands.Length; i++)
        {
            var start = commands[i].TrimStart(' ', '\t', '"');
            if (sensitivePrefixes.FirstOrDefault(p => start.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix)
            {
                var leading = i == 0 ? string.Empty : commands[i][..^start.Length].Replace("\"", string.Empty, StringComparison.Ordinal);
                commands[i] = $"{leading}{prefix} ***";
                redacted = true;
            }
        }

        return redacted ? string.Join(';', commands) : line;
    }
}
