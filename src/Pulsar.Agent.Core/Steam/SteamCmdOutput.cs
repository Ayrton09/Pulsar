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

    /// <summary>Steam asks for a Steam Guard code (by mail or from the mobile app): nobody is there to type it.</summary>
    public sealed record GuardRequested(string Message) : SteamCmdLine;
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

        if (IsGuardPrompt(text))
        {
            return new SteamCmdLine.GuardRequested(Cut(text));
        }

        // "Login Failure: Invalid Password", "...FAILED (Rate Limit Exceeded)", "FAILED with result code 5": a login.
        if (text.StartsWith("ERROR!", StringComparison.OrdinalIgnoreCase) || StateError().IsMatch(text)
            || text.Contains("Login Failure", StringComparison.OrdinalIgnoreCase) || LoginFailed().IsMatch(text))
        {
            return new SteamCmdLine.Failure(Cut(text));
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

        // A login Steam refused: trying again the same way fails the same way, and too many tries lock the account out.
        var result = ResultCode().Match(error) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        if (result == 84 || error.Contains("Rate Limit", StringComparison.OrdinalIgnoreCase))
        {
            return ("steamcmd.rate_limited", false);
        }

        if (result is 63 or 65 or 85 or 88 || IsGuardPrompt(error) || error.Contains("Login Auth Code", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Account Logon Denied", StringComparison.OrdinalIgnoreCase) || error.Contains("code mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return ("steamcmd.guard_required", false);
        }

        if (result is 5 or 18 || (IsLogin(error) && error.Contains("Invalid Password", StringComparison.OrdinalIgnoreCase)))
        {
            return ("steamcmd.login_failed", false);
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

    /// <summary>
    /// Whether a failure says nothing against the cached login the run signed in with: SteamCMD got as far as the app (an
    /// error about it, its state or the disk; <c>app_update</c> runs only once signed in, the first failed command ending
    /// the run), or Steam limits sign-ins (another one with the password would only make it worse). Anything else, an
    /// exit with no error included, may be the login itself.
    /// </summary>
    public static bool LeavesLoginValid(string? error) =>
        error is not null && (AppError().IsMatch(error) || Classify(error).Code is "steamcmd.disk" or "steamcmd.app_unavailable" or "steamcmd.rate_limited");

    /// <summary>Whether SteamCMD stopped at (or failed on) a Steam Guard code.</summary>
    public static bool IsGuardPrompt(string text) =>
        text.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase) || text.Contains("Two-factor", StringComparison.OrdinalIgnoreCase)
        || text.Contains("two factor", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an error is about signing in (rather than an app's beta password, say).</summary>
    public static bool IsLogin(string error) =>
        error.Contains("Login Failure", StringComparison.OrdinalIgnoreCase) || error.Contains("Logging in", StringComparison.OrdinalIgnoreCase)
        || LoginFailed().IsMatch(error);

    private static string Cut(string text) => text.Length > 300 ? text[..300] : text;

    [GeneratedRegex(@"FAILED\s*(\(|with result code)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LoginFailed();

    [GeneratedRegex(@"result code (\d{1,4})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ResultCode();

    [GeneratedRegex(@"\bapp '\d+'|\bAppInfo\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AppError();

    [GeneratedRegex(@"state is 0x0*6 after update job", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StuckState();

    [GeneratedRegex(@"^Update state \(0x[0-9a-fA-F]+\) (?<label>[^,]*), progress: [0-9.]+ \((?<done>\d+) / (?<total>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateState();

    [GeneratedRegex(@"^Error! App '\d+' state is 0x[0-9a-fA-F]+ after update job", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StateError();

    [GeneratedRegex(@"^\[\s*(\d{1,3}%|----)\]", RegexOptions.CultureInvariant)]
    private static partial Regex SelfUpdateLine();
}
