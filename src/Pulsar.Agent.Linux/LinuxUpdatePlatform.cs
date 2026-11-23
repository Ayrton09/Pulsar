using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Updates;
using Pulsar.Contracts;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Updating Pulsar on Linux (docs/guides/update.md, "Linux"): only the root service that install-pulsar-agent.sh installed
/// in <see cref="AgentFolder"/> updates. The release's tarball is unpacked in <see cref="Root"/>, which only root can
/// change (the script, which runs from there as root, checks it again), and its install-pulsar-agent.sh --update runs in a
/// systemd unit of its own: it stops this service, whose processes systemd ends with it.
/// </summary>
/// <param name="development">Whether this agent was enrolled with --dev.</param>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxUpdatePlatform(Func<bool> development) : IUpdatePlatform
{
    /// <summary>Where install-pulsar-agent.sh puts the agent's programs.</summary>
    public const string AgentFolder = "/opt/pulsar/agent";

    /// <summary>What install-pulsar-agent.sh writes once it installed the agent.</summary>
    public const string InstallState = "/etc/pulsar/agent-install.json";

    /// <summary>What install-pulsar.sh writes once it installed the panel.</summary>
    public const string PanelInstallState = "/etc/pulsar/install.json";

    /// <summary>The scripts' own folder for updates: their result, backups and previous programs, and the releases unpacked.</summary>
    public const string Root = "/var/lib/pulsar/updates";

    private const uint OwnerOnly = 0x1C0;

    /// <summary>Tests: where the agent runs from, and the files that say what is installed.</summary>
    internal string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    internal string AgentState { get; init; } = InstallState;

    internal string PanelState { get; init; } = PanelInstallState;

    public string ArchiveName(UpdateVersion version) => UpdateRelease.LinuxTarballName(version);

    public string InstallerName => "install-pulsar-agent.sh";

    public void Extract(string archive, string destination, UpdateVersion version) =>
        UpdateTarball.Extract(archive, destination, UpdateRelease.LinuxFolder(version));

    public string UpdatesRoot(AgentPaths paths) => Root;

    public string SetupLogs(string updatesRoot) => "/var/log/pulsar/setup";

    public (string Code, string Message)? CannotUpdate()
    {
        if (!LinuxDirectorySecurity.IsElevated())
        {
            return ("update.not_service", "This agent does not run as the pulsar-agent service: update it with install-pulsar-agent.sh --update.");
        }

        if (development())
        {
            return ("update.development", "A development agent is updated with a new build, not from the releases.");
        }

        return !File.Exists(AgentState) || Path.TrimEndingDirectorySeparator(BaseDirectory) != AgentFolder
            ? ("update.not_installed", $"This agent was not installed in {AgentFolder} by install-pulsar-agent.sh: update it by hand (docs/guides/update.md).")
            : null;
    }

    public bool HostsPanel() => File.Exists(PanelState);

    public string PrepareFolder(string updatesRoot, string name)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(updatesRoot));
        // Nobody else may replace a folder above it either: renaming one away and putting another in its place would put
        // whatever they like where this agent unpacks and runs the installer as root.
        if (LinuxDirectorySecurity.FindReplaceableAncestors(root) is { Count: > 0 } replaceable)
        {
            throw new InvalidOperationException($"A folder above {root} could be replaced by another user than root: {string.Join("; ", replaceable.Take(5))}.");
        }

        if (Libc.Lstat(root) is null)
        {
            // Made root's alone at once; one someone else made first, in between, is refused below.
            Directory.CreateDirectory(root, (UnixFileMode)OwnerOnly);
        }

        if (!IsPrivate(root))
        {
            throw new InvalidOperationException($"{root} could be changed by another user than root (or is a link): delete it, and update again.");
        }

        var folder = Path.Combine(root, name);
        if (Libc.Lstat(folder) is not null)
        {
            throw new IOException($"{folder} exists already.");
        }

        Directory.CreateDirectory(folder, (UnixFileMode)OwnerOnly);
        return IsPrivate(folder) ? folder : throw new InvalidOperationException($"{folder} could be changed by another user than root.");
    }

    public Task<int> StartInstaller(string script, string workingDirectory, Guid? attempt)
    {
        // A unit of its own, outside this service's: stopping the service ends every process of it, and the update stops it
        // (systemd-run, which waits for the unit, goes with it; the unit does not). Its exit code is seen only when it ends
        // while this agent runs.
        var start = new ProcessStartInfo(SystemdUnits.Locate("systemd-run"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in (string[])[
            "--unit", $"pulsar-update-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}", "--description", "Pulsar update",
            "--collect", "--wait", "--property", "UMask=0077", "--setenv", "LC_ALL=C", "--working-directory", workingDirectory,
            "--", SystemdUnits.Locate("bash"), script, "--update", "--unattended",
            .. attempt is { } id ? (string[])["--update-attempt", id.ToString("D")] : []])
        {
            start.ArgumentList.Add(argument);
        }

        // English, whatever the machine's language: what it says of the unit is read.
        start.Environment["LC_ALL"] = "C";
        var process = Process.Start(start) ?? throw new Win32Exception("systemd-run could not be started.");
        process.StandardInput.Close();
        return ExitCodeAsync(process);
    }

    private static async Task<int> ExitCodeAsync(Process process)
    {
        using (process)
        {
            // Read, so that a full pipe never blocks it; the installer writes its own log.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return ExitCodeOf(await error + await output, process.ExitCode);
        }
    }

    /// <summary>
    /// The unit's exit code, from what <c>systemd-run --wait</c> says of it: systemd-run itself ends with 0 whatever the
    /// unit's end. A unit killed, or an end it does not describe, counts as a failure.
    /// </summary>
    internal static int ExitCodeOf(string said, int ownExitCode)
    {
        if (ExitedPattern().Match(said) is { Success: true } exited)
        {
            return int.Parse(exited.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return ownExitCode != 0 ? ownExitCode : said.Contains("Finished with result: success", StringComparison.Ordinal) ? 0 : 1;
    }

    [GeneratedRegex(@"Main processes terminated with: code=exited/status=([0-9]{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex ExitedPattern();

    /// <summary>Root's, not a link, a folder, and closed to group and others (as the scripts make it).</summary>
    internal static bool IsPrivate(string folder) =>
        Libc.Lstat(folder) is { IsSymbolicLink: false, IsDirectory: true, Uid: 0 } stat && (stat.Permissions & 0x3F) == 0;
}
