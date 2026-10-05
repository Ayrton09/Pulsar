using System.Globalization;
using System.Text.RegularExpressions;

namespace Pulsar.Agent.Core.Steam;

/// <summary>Stages of an install, as reported to the panel (<c>OperationProgress.Stage</c>).</summary>
public static class InstallStages
{
    public const string Queued = "queued";
    public const string Preparing = "preparing";
    public const string Downloading = "downloading";
    public const string Verifying = "verifying";
    public const string Committing = "committing";
    public const string Installing = "installing";
    public const string Retrying = "retrying";
}

/// <summary>What one line of SteamCMD output says.</summary>
public abstract record SteamCmdLine
{
    private SteamCmdLine()
    {
    }

    /// <param name="Percent">0–100.</param>
    public sealed record Progress(string Stage, double Percent, long Done, long Total) : SteamCmdLine;

    /// <summary>SteamCMD updating itself (its first runs).</summary>
    public sealed record SelfUpdate : SteamCmdLine;

    public sealed record Success : SteamCmdLine;

    public sealed record Failure(string Message) : SteamCmdLine;
}

/// <summary>
/// Reads SteamCMD's output (spike 0001, F5). Some of it is translated to the machine's language, so only the
/// language-neutral lines are interpreted: <c>Update state (0x61) downloading, progress: 9.44 (238319757 / 2524405728)</c>,
/// <c>Success!</c> and <c>ERROR!</c>. Anything else is left alone (and kept for error reports).
/// </summary>
public static partial class SteamCmdOutput
{
    public static SteamCmdLine? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.Trim();
        if (UpdateState().Match(text) is { Success: true } update)
        {
            var label = update.Groups["label"].Value;
            var stage = label.Contains("download", StringComparison.OrdinalIgnoreCase) ? InstallStages.Downloading
                : label.Contains("verif", StringComparison.OrdinalIgnoreCase) || label.Contains("validat", StringComparison.OrdinalIgnoreCase) ? InstallStages.Verifying
                : label.Contains("commit", StringComparison.OrdinalIgnoreCase) ? InstallStages.Committing
                : InstallStages.Installing;
            var done = long.Parse(update.Groups["done"].Value, CultureInfo.InvariantCulture);
            var total = long.Parse(update.Groups["total"].Value, CultureInfo.InvariantCulture);
            var percent = total > 0 ? Math.Clamp(100.0 * done / total, 0, 100) : 0;
            return new SteamCmdLine.Progress(stage, Math.Round(percent, 2), done, total);
        }

        if (text.StartsWith("Success!", StringComparison.Ordinal))
        {
            return new SteamCmdLine.Success();
        }

        if (text.StartsWith("ERROR!", StringComparison.OrdinalIgnoreCase) || StateError().IsMatch(text))
        {
            return new SteamCmdLine.Failure(text.Length > 300 ? text[..300] : text);
        }

        // "[ 45%] Downloading update (12,345 of 67,890 KB)..." (localized): SteamCMD updating itself.
        return SelfUpdateLine().IsMatch(text) ? new SteamCmdLine.SelfUpdate() : null;
    }

    /// <summary>Whether a failure is worth retrying: SteamCMD's transient errors are common (spike 0001), these are not.</summary>
    public static (string Code, bool Retry) Classify(string? error)
    {
        if (error is null)
        {
            return ("steamcmd.failed", true);
        }

        // "state is 0x202 after update job" is how SteamCMD reports a full disk.
        if (error.Contains("disk", StringComparison.OrdinalIgnoreCase) || error.Contains("state is 0x202", StringComparison.OrdinalIgnoreCase))
        {
            return ("steamcmd.disk", false);
        }

        if (error.Contains("No subscription", StringComparison.OrdinalIgnoreCase) || error.Contains("Invalid platform", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Invalid Password", StringComparison.OrdinalIgnoreCase) || error.Contains("No license", StringComparison.OrdinalIgnoreCase))
        {
            return ("steamcmd.app_unavailable", false);
        }

        return ("steamcmd.failed", true);
    }

    /// <summary>
    /// "state is 0x6" (update required, files present): SteamCMD cannot update files whose build it has no manifests
    /// for, as after a copy from another install or an update interrupted at the wrong time. Another identical run
    /// fails the same way; forgetting the app's state and validating every file recovers.
    /// </summary>
    public static bool NeedsReset(string? error) => error is not null && StuckState().IsMatch(error);

    [GeneratedRegex(@"state is 0x0*6 after update job", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StuckState();

    [GeneratedRegex(@"^Update state \(0x[0-9a-fA-F]+\) (?<label>[^,]*), progress: [0-9.]+ \((?<done>\d+) / (?<total>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateState();

    [GeneratedRegex(@"^Error! App '\d+' state is 0x[0-9a-fA-F]+ after update job", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StateError();

    [GeneratedRegex(@"^\[\s*(\d{1,3}%|----)\]", RegexOptions.CultureInvariant)]
    private static partial Regex SelfUpdateLine();
}
