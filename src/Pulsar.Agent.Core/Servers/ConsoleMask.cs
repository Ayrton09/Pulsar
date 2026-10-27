namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Hides a server's secrets (<see cref="Pulsar.Manifests.LaunchPlan.Secrets"/>) in its console output: a game that prints
/// its command line (Unreal Engine's do) or its settings would show its passwords to whoever reads the console. Output
/// comes in chunks split anywhere, so the end of a chunk that may begin a secret is held until the next one. Not thread
/// safe: <see cref="ServerConsole"/> serializes its calls.
/// </summary>
public sealed class ConsoleMask
{
    public const string Mask = "********";

    /// <summary>The most secrets kept, the newest runs' first.</summary>
    public const int MaximumSecrets = 64;

    private string[] _secrets = [];
    private string[] _recent = []; // newest first
    private string _held = "";

    /// <summary>
    /// The secrets hidden from now on (a run of the game starting), besides those of earlier runs (at most
    /// <see cref="MaximumSecrets"/> in all): output of an earlier run that comes late, or of a game that started with a
    /// password since changed, still has its own masked. Returns what was held for the previous ones, to write before the
    /// new run's output.
    /// </summary>
    public string Use(IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var held = Flush();
        _recent = [.. secrets.Where(s => s.Length > 0).Concat(_recent).Distinct(StringComparer.Ordinal).Take(MaximumSecrets)];
        // The longest first: a secret that holds another is hidden whole.
        _secrets = [.. _recent.OrderByDescending(s => s.Length)];
        return held;
    }

    /// <summary>What can be written of <paramref name="text"/> (after what was held), its secrets masked.</summary>
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_secrets.Length == 0)
        {
            return text;
        }

        var all = _held.Length == 0 ? text : _held + text;
        foreach (var secret in _secrets)
        {
            all = all.Replace(secret, Mask, StringComparison.Ordinal);
        }

        var hold = Holding(all);
        _held = all[^hold..];
        return all[..^hold];
    }

    /// <summary>
    /// What is held (the game wrote nothing after it, or exited): the start of a secret long enough to tell the secret by
    /// is masked, a shorter one written as it is.
    /// </summary>
    public string Flush()
    {
        var held = _held;
        _held = "";
        return held.Length >= Pulsar.Manifests.LaunchPlan.MinimumSecretLength ? Mask : held;
    }

    // The longest end of the text that is the start of a secret (shorter than it: whole ones were masked already).
    private int Holding(string text)
    {
        var longest = 0;
        foreach (var secret in _secrets)
        {
            for (var length = Math.Min(secret.Length - 1, text.Length); length > longest; length--)
            {
                if (text.AsSpan(text.Length - length).SequenceEqual(secret.AsSpan(0, length)))
                {
                    longest = length;
                    break;
                }
            }
        }

        return longest;
    }
}
