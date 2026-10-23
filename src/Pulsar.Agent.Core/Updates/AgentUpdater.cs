using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Panel;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Updates;

/// <summary>What updating Pulsar needs of the machine (Windows: <c>WindowsUpdatePlatform</c>).</summary>
public interface IUpdatePlatform
{
    /// <summary>Null when this agent can update Pulsar on its machine; otherwise the error code and why not.</summary>
    (string Code, string Message)? CannotUpdate();

    /// <summary>Whether the panel is installed on this machine too (an update here updates it as well).</summary>
    bool HostsPanel();

    /// <summary>
    /// A new, empty folder named <paramref name="name"/> in <paramref name="updatesRoot"/>, which only SYSTEM and
    /// Administrators can change: made so when missing, refused when someone else could change it or it is a link.
    /// </summary>
    string PrepareFolder(string updatesRoot, string name);

    /// <summary>
    /// Starts the release's installer (<c>Install-Pulsar.ps1 -Update -Unattended</c>, with <c>-UpdateAttempt</c> when the panel
    /// gave the request an id: the installer writes it in its result), detached: it stops this agent. The task ends with
    /// its exit code only when it ends while this agent still runs (it refused, or failed before updating).
    /// </summary>
    Task<int> StartInstaller(string script, string workingDirectory, Guid? attempt);
}

/// <summary>Defaults for production; tests give their own keys and HTTP handler.</summary>
public sealed record AgentUpdaterOptions
{
    public UpdateVerifier Verifier { get; init; } = UpdateVerifier.Default;

    public Func<HttpMessageHandler> Handler { get; init; } = UpdateDownloads.CreateHandler;

    /// <summary>How often the installer's result file is looked at, to report the end of an update.</summary>
    public TimeSpan ResultPoll { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The longest an update may take before this agent takes another (the installer stops the agent long before).</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Where releases are unpacked and the installer writes its result; null: beside the agent's state folder.</summary>
    public string? UpdatesRoot { get; init; }

    /// <summary>
    /// Tests: sees every report of a running update, and every result sent, whether or not the panel is connected (once
    /// the panel took it, when it is).
    /// </summary>
    internal Func<UpdateReport, Task>? OnReport { get; init; }
}

/// <summary>
/// Updates Pulsar on this machine when the panel asks (<see cref="UpdatePulsarCommand"/>, Phase 19), trusting nothing
/// the panel sends but the version and the manifest's SHA-256: the release comes from GitHub, its manifest must be signed
/// by a trusted update key and newer than this agent, and the ZIP must have the manifest's size and SHA-256. It is
/// unpacked into a folder only SYSTEM and Administrators can change, and its installer does the rest (backup, update,
/// checks, rollback) and writes its result in <see cref="ResultFile"/>, which this agent reports after each Hello. Every
/// report carries the panel's id for the request (<see cref="UpdatePulsarCommand.Attempt"/>), the installer's result too:
/// the panel never takes an earlier request's result, sent again, for a later one's.
/// </summary>
public sealed partial class AgentUpdater : IDisposable
{
    private const string UserAgent = "Pulsar-Agent";
    private static readonly Regex s_releaseFolder = new(@"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})-[0-9a-f]{8}\z",
        RegexOptions.CultureInvariant);

    private readonly IUpdatePlatform _platform;
    private readonly PanelChannel _channel;
    private readonly TimeProvider _clock;
    private readonly ILogger<AgentUpdater> _logger;
    private readonly AgentUpdaterOptions _options;
    private readonly ITimer _poll;
    private readonly Lock _lock = new();
    private long _startedAt;
    private bool _running;
    private DateTimeOffset? _reported;

    public AgentUpdater(IUpdatePlatform platform, PanelChannel channel, AgentPaths paths, TimeProvider clock, ILogger<AgentUpdater> logger,
        AgentUpdaterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _platform = platform;
        _channel = channel;
        _clock = clock;
        _logger = logger;
        _options = options ?? new AgentUpdaterOptions();
        UpdatesRoot = _options.UpdatesRoot ?? Path.Combine(Path.GetDirectoryName(paths.Root) ?? paths.Root, "Updates");
        channel.OnConnected(ct => ReportResultAsync(always: true, ct));
        _poll = clock.CreateTimer(_ => _ = ReportResultAsync(always: false, CancellationToken.None), null, _options.ResultPoll, _options.ResultPoll);
    }

    /// <summary>Beside the agent's state folder: <c>%ProgramData%\Pulsar\Updates</c>, the installer's too.</summary>
    public string UpdatesRoot { get; }

    /// <summary>Where the installer writes how its last update ended (docs/guides/update.md).</summary>
    public string ResultFile => Path.Combine(UpdatesRoot, "last-update.json");

    /// <summary>Where the installer writes its logs: <c>%ProgramData%\Pulsar\Setup\logs</c>.</summary>
    private string SetupLogs => Path.Combine(Path.GetDirectoryName(UpdatesRoot) ?? UpdatesRoot, "Setup", "logs");

    /// <summary>What this agent announces: that it can update Pulsar here, and whether the panel runs here.</summary>
    public IEnumerable<string> Capabilities =>
        [.. _platform.CannotUpdate() is null ? new[] { AgentCapabilities.UpdatePulsar } : [], .. _platform.HostsPanel() ? new[] { AgentCapabilities.PanelHost } : []];

    /// <summary>Checks the command and starts the update in the background: its steps and end come as <see cref="UpdateReport"/>s.</summary>
    public CommandAck Start(Guid commandId, UpdatePulsarCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_platform.CannotUpdate() is { } cannot)
        {
            return Refuse(commandId, cannot.Code, cannot.Message);
        }

        if (!UpdateVersion.TryParse(command.Version, out var version) || command.ManifestSha256 is not { Length: 64 }
            || !command.ManifestSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return Refuse(commandId, "update.invalid", "The version or the manifest's SHA-256 is malformed.");
        }

        if (UpdateVersion.Installed(AgentInfo.Version) is not { } installed || !version.IsNewerThan(installed))
        {
            return Refuse(commandId, "update.not_newer", $"This node runs {AgentInfo.Version}: {version} is not newer.");
        }

        lock (_lock)
        {
            if (_running && _clock.GetElapsedTime(_startedAt) < _options.Deadline)
            {
                return Refuse(commandId, "update.in_progress", "An update is under way on this node.");
            }

            _running = true;
            _startedAt = _clock.GetTimestamp();
        }

        LogStarting(commandId, installed.Version, version);
        _ = Task.Run(() => RunAsync(installed.Version, version, command.ManifestSha256, command.Attempt));
        return new CommandAck(commandId, AckStatus.Accepted, null, null);
    }

    public void Dispose() => _poll.Dispose();

    private async Task RunAsync(UpdateVersion from, UpdateVersion version, string manifestSha256, Guid? attempt)
    {
        string? folder = null;
        try
        {
            await ReportAsync(UpdateStatuses.Downloading, from, version, attempt, null);
            RemoveEarlierReleases();
            using var client = new HttpClient(_options.Handler(), disposeHandler: true) { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"{UserAgent}/{AgentInfo.Version}");
            var manifest = await UpdateDownloads.GetBytesAsync(client, UpdateRelease.File(version, UpdateRelease.ManifestName),
                UpdateRelease.MaxManifestBytes, CancellationToken.None);
            var signature = await UpdateDownloads.GetBytesAsync(client, UpdateRelease.File(version, UpdateRelease.SignatureName),
                UpdateRelease.MaxSignatureBytes, CancellationToken.None);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(manifest)), manifestSha256, StringComparison.Ordinal))
            {
                throw new UpdateDownloadException($"The release {version} on GitHub is not the one the panel checked: nothing was installed.");
            }

            var check = _options.Verifier.Verify(manifest, signature);
            if (check.Manifest is not { } release)
            {
                throw new UpdateDownloadException($"The release {version} was refused ({check.ErrorCode}): {check.Error}");
            }

            if (release.Version != version.ToString())
            {
                throw new UpdateDownloadException($"The release named {version} says it is {release.Version}: nothing was installed.");
            }

            if (UpdateVersion.TryParse(release.MinimumUpdateFrom, out var minimum) && from < minimum)
            {
                throw new UpdateDownloadException($"{version} updates {minimum} and later: update to {minimum} first, or use the setups.");
            }

            var zip = release.Find(UpdateRelease.ZipName(version))!;
            folder = _platform.PrepareFolder(UpdatesRoot, $"{version}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
            var archive = Path.Combine(folder, zip.Name);
            await UpdateDownloads.DownloadAsync(client, UpdateRelease.File(version, zip.Name), zip, archive, CancellationToken.None);
            var unpacked = Path.Combine(folder, "release");
            UpdateArchive.Extract(archive, unpacked);
            File.Delete(archive);
            var script = Path.Combine(unpacked, "Install-Pulsar.ps1");
            if (!File.Exists(script) || ReleaseVersion(Path.Combine(unpacked, "release.json")) != version.ToString())
            {
                throw new UpdateDownloadException($"The release {version}'s ZIP is not a Pulsar release of that version.");
            }

            await ReportAsync(UpdateStatuses.Installing, from, version, attempt, null);
            LogInstalling(version, unpacked);
            var started = _clock.GetUtcNow();
            var exitCode = await _platform.StartInstaller(script, unpacked, attempt);

            // Still running: the installer ended without updating (another update held its lock, its folder was not
            // trusted, a check failed). Its result, when it wrote one for this update, says why; otherwise its exit code.
            // This update's: its request's id when it has one, else this version's written since the installer started.
            LogInstallerEnded(version, exitCode);
            var own = ReadResult() is { } result
                && (attempt is not null ? result.Attempt == attempt : result.To == version.ToString() && result.At >= started.AddSeconds(-5));

            // Done here before it is reported, as a failure is: once its end is told, the next update may be asked.
            TryDelete(folder);
            folder = null;
            lock (_lock)
            {
                _running = false;
            }

            if (own)
            {
                await ReportResultAsync(always: true, CancellationToken.None);
            }
            else
            {
                await ReportAsync(UpdateStatuses.Failed, from, version, attempt,
                    $"The installer ended with exit code {exitCode} before updating anything. Its log is in {SetupLogs}.");
            }
        }
        catch (Exception ex)
        {
            // Whatever failed (a download, a check, the folder, something unexpected), the update ends here, is reported, and
            // the next one may be asked at once.
            LogFailed(version, ex.Message);
            if (folder is not null)
            {
                TryDelete(folder);
            }

            lock (_lock)
            {
                _running = false;
            }

            await ReportAsync(UpdateStatuses.Failed, from, version, attempt, ex.Message);
        }
    }

    /// <summary>
    /// The installer's result, sent after each Hello (<paramref name="always"/>) or when it changed, with the request it
    /// was for: the panel takes it only for that request. A missing or malformed file reports nothing.
    /// </summary>
    internal async Task ReportResultAsync(bool always, CancellationToken cancellationToken)
    {
        if (ReadResult() is not { } result || (!always && result.At == _reported))
        {
            return;
        }

        // An update this agent runs ends in RunAsync: an older result sent again after a Hello says nothing about it.
        if (await _channel.DeliverAsync(nameof(IAgentHub.UpdateReport), result, cancellationToken))
        {
            _reported = result.At;
        }

        if (_options.OnReport is { } seen)
        {
            await seen(result);
        }
    }

    /// <summary>
    /// Earlier updates' folders (<c>&lt;version&gt;-&lt;random&gt;</c>, each a whole release): deleted when a new update starts,
    /// unless made within <see cref="AgentUpdaterOptions.Deadline"/>, so that no installer still running loses its files.
    /// A link is removed, never followed. The installer's own (backups, previous, its result and lock) are not touched.
    /// </summary>
    internal void RemoveEarlierReleases()
    {
        var root = new DirectoryInfo(UpdatesRoot);
        if (!root.Exists)
        {
            return;
        }

        foreach (var folder in root.EnumerateDirectories())
        {
            if (!s_releaseFolder.IsMatch(folder.Name) || _clock.GetUtcNow() - folder.CreationTimeUtc < _options.Deadline)
            {
                continue;
            }

            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                folder.Delete();
            }
            else
            {
                TryDelete(folder.FullName);
            }
        }
    }

    internal UpdateReport? ReadResult()
    {
        try
        {
            var info = new FileInfo(ResultFile);
            if (!info.Exists || info.Length > 16 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var read = JsonSerializer.Deserialize<InstallerResult>(File.ReadAllBytes(info.FullName), ProtocolJson.Options);
            if (read is not { Status: { } status, FinishedAt: { } at } || !UpdateStatuses.IsFinal(status))
            {
                return null;
            }

            // An id that is not one is no request's: the result is reported without it.
            return new UpdateReport(status, Bounded(read.From, 32), Bounded(read.To, 32),
                read.Parts?.Where(p => p is "agent" or "panel").Distinct().ToList(), Bounded(read.Error, UpdateReport.MaxErrorLength), at,
                Guid.TryParseExact(read.Attempt, "D", out var attempt) ? attempt : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task ReportAsync(string status, UpdateVersion from, UpdateVersion to, Guid? attempt, string? error)
    {
        var report = new UpdateReport(status, from.ToString(), to.ToString(), null, Bounded(error, UpdateReport.MaxErrorLength), _clock.GetUtcNow(),
            attempt);
        await _channel.DeliverAsync(nameof(IAgentHub.UpdateReport), report, CancellationToken.None);
        if (_options.OnReport is { } seen)
        {
            await seen(report);
        }
    }

    private static string? ReleaseVersion(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static string? Bounded(string? text, int length) => text is null ? null : text.Length <= length ? text : text[..length];

    private void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotDeleted(folder, ex.Message);
        }
    }

    private CommandAck Refuse(Guid commandId, string code, string message)
    {
        LogRefused(commandId, code);
        return new CommandAck(commandId, AckStatus.Rejected, code, message);
    }

    /// <summary>The installer's <c>last-update.json</c> (<c>attempt</c> only from installers that know request ids).</summary>
    private sealed record InstallerResult(string? From, string? To, IReadOnlyList<string>? Parts, string? Status, string? Error, DateTimeOffset? FinishedAt,
        string? Attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {CommandId}: from {From} to {To}, downloading the release from GitHub")]
    private partial void LogStarting(Guid commandId, UpdateVersion from, UpdateVersion to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update to {To}: the release is verified and unpacked in {Folder}; its installer takes over")]
    private partial void LogInstalling(UpdateVersion to, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The installer of {To} ended with exit code {ExitCode} while this agent ran: it did not update")]
    private partial void LogInstallerEnded(UpdateVersion to, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update to {To} failed: {Reason}")]
    private partial void LogFailed(UpdateVersion to, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused update command {CommandId}: {Code}")]
    private partial void LogRefused(Guid commandId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the update folder {Folder}: {Reason}")]
    private partial void LogNotDeleted(string folder, string reason);
}
