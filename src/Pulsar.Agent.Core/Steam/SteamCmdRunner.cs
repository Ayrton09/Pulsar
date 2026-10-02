using System.Globalization;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Steam;

/// <summary>Where the node's SteamCMD comes from.</summary>
public interface ISteamCmdSource
{
    /// <summary>Path of a <c>steamcmd.exe</c> verified as Valve's, downloaded from Valve the first time.</summary>
    /// <exception cref="GameProcessException">It cannot be downloaded or is not signed by Valve.</exception>
    Task<string> GetAsync(CancellationToken cancellationToken);
}

public sealed class SteamCmdOptions
{
    /// <summary>Installs running at the same time on the node (each server has its own SteamCMD; this bounds bandwidth and disk load).</summary>
    public int MaxConcurrentRuns { get; set; } = 2;

    /// <summary>Attempts per install: SteamCMD fails transiently (spike 0001: "Missing configuration", then success).</summary>
    public int MaxAttempts { get; set; } = 3;

    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>A run that prints nothing for this long is considered stuck: it is killed and retried.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Runs that only update SteamCMD itself (exit code 7) allowed before giving up.</summary>
    public int MaxSelfUpdates { get; set; } = 2;
}

/// <summary>An app to install or update in a server directory.</summary>
public sealed record SteamCmdJob(Guid ServerId, string ServerRoot, ProcessIdentity Identity, int AppId, string? Beta, string? BetaPassword, bool Validate)
{
    public override string ToString() => $"SteamCmdJob {{ ServerId = {ServerId}, AppId = {AppId} }}";
}

/// <summary>
/// Runs SteamCMD for a server (docs/architecture/06, "SteamCMD"). Each server has its own copy in its directory and
/// SteamCMD runs as the server's account, under a pseudo console (live output), in the server's Job: it can write only
/// where the server can, and a server can never tamper with another one's SteamCMD. The copy updates itself on its
/// first run (exit code 7); transient failures are retried; cancelling kills its Job.
/// </summary>
public sealed partial class SteamCmdRunner(
    IGameProcessManager processes,
    ISteamCmdSource source,
    IServerFiles files,
    SteamCmdOptions options,
    TimeProvider clock,
    ILogger<SteamCmdRunner> logger) : IDisposable
{
    /// <summary>The server's SteamCMD, relative to its directory.</summary>
    public const string Executable = ".pulsar/steamcmd/steamcmd.exe";

    private const string WorkingDirectory = ".pulsar/steamcmd";
    private const int SelfUpdateExitCode = 7;

    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.MaxConcurrentRuns));

    public void Dispose() => _slots.Dispose();

    /// <param name="progress">Stage and percent (null when unknown), throttled; awaited, so keep it quick.</param>
    /// <exception cref="GameProcessException">The install failed (<c>steamcmd.*</c> codes).</exception>
    /// <exception cref="OperationCanceledException">Cancelled; SteamCMD was killed.</exception>
    public async Task RunAsync(SteamCmdJob job, Func<string, double?, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);
        await progress(InstallStages.Queued, null);
        await _slots.WaitAsync(cancellationToken);
        try
        {
            await progress(InstallStages.Preparing, null);
            await EnsureCopyAsync(job, cancellationToken);
            var attempts = 0;
            var selfUpdates = 0;
            while (true)
            {
                var result = await RunOnceAsync(job, progress, cancellationToken);
                if (result.Succeeded)
                {
                    LogInstalled(job.ServerId, job.AppId, attempts + 1);
                    return;
                }

                if (result.SelfUpdated && selfUpdates++ < options.MaxSelfUpdates)
                {
                    LogSelfUpdated(job.ServerId);
                    continue;
                }

                attempts++;
                var (code, retry) = SteamCmdOutput.Classify(result.Error);
                var message = result.Error ?? (result.Stalled
                    ? $"SteamCMD printed nothing for {options.StallTimeout.TotalMinutes:0} minutes."
                    : $"SteamCMD exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
                LogAttemptFailed(job.ServerId, job.AppId, attempts, result.ExitCode, message, string.Join(" | ", result.Tail));
                if (!retry || attempts >= options.MaxAttempts)
                {
                    throw new GameProcessException(code, message);
                }

                await progress(InstallStages.Retrying, null);
                var delay = options.RetryDelays.Count == 0 ? TimeSpan.Zero : options.RetryDelays[Math.Min(attempts - 1, options.RetryDelays.Count - 1)];
                await Task.Delay(delay, clock, cancellationToken);
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>The command line: argv elements, never a string for a shell (docs/architecture/06).</summary>
    public static IReadOnlyList<string> Arguments(SteamCmdJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var arguments = new List<string>
        {
            // Exit on the first failed command instead of carrying on, and never wait for a password at a prompt.
            "+@ShutdownOnFailedCommand", "1",
            "+@NoPromptForPassword", "1",
            // Before the login, or SteamCMD ignores it (docs/architecture/06).
            "+force_install_dir", Path.TrimEndingDirectorySeparator(job.ServerRoot),
            "+login", "anonymous",
            "+app_update", job.AppId.ToString(CultureInfo.InvariantCulture),
        };
        if (job.Beta is { } beta)
        {
            arguments.AddRange(["-beta", beta]);
            if (job.BetaPassword is { Length: > 0 } password)
            {
                arguments.AddRange(["-betapassword", password]);
            }
        }

        if (job.Validate)
        {
            arguments.Add("validate");
        }

        arguments.Add("+quit");
        return arguments;
    }

    private async Task EnsureCopyAsync(SteamCmdJob job, CancellationToken cancellationToken)
    {
        using (var existing = files.OpenRead(job.ServerRoot, Executable))
        {
            if (existing is not null)
            {
                return; // it keeps itself up to date
            }
        }

        var bootstrapper = await source.GetAsync(cancellationToken);
        await using var stream = File.OpenRead(bootstrapper);
        files.Write(job.ServerRoot, Executable, stream);
    }

    private async Task<RunResult> RunOnceAsync(SteamCmdJob job, Func<string, double?, Task> progress, CancellationToken cancellationToken)
    {
        var spec = new ProcessLaunchSpec(job.ServerId, job.ServerRoot, Executable, Arguments(job), WorkingDirectory, new Dictionary<string, string>(),
            ConsoleMode.Pty, ConsoleEncoding.Utf8, job.Identity);
        var process = await processes.StartAsync(spec, cancellationToken);
        using var stall = new CancellationTokenSource(options.StallTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall.Token);
        var output = new RunOutput(new ProgressReporter(progress, clock));
        // A pseudo console's output ends only when it is closed (disposing the process): read it alongside.
        var pump = PumpAsync(process, output, stall);
        try
        {
            var exitCode = await process.Exited.WaitAsync(linked.Token);
            await process.DisposeAsync();
            await pump;
            return new RunResult(output.Succeeded && exitCode == 0, exitCode == SelfUpdateExitCode && !output.Succeeded, exitCode, output.Error,
                [.. output.Tail], Stalled: false);
        }
        catch (OperationCanceledException)
        {
            process.TerminateTree(1);
            await process.DisposeAsync();
            await pump;
            if (cancellationToken.IsCancellationRequested)
            {
                LogCancelled(job.ServerId, job.AppId);
                throw;
            }

            return new RunResult(false, false, 1, null, [.. output.Tail], Stalled: true);
        }
    }

    private async Task PumpAsync(IGameProcess process, RunOutput output, CancellationTokenSource stall)
    {
        var lines = new ConsoleLines();
        await foreach (var chunk in process.Output.ReadAllAsync())
        {
            if (!stall.IsCancellationRequested)
            {
                stall.CancelAfter(options.StallTimeout);
            }

            foreach (var line in lines.Add(chunk.Text))
            {
                await output.AddAsync(line);
            }
        }
    }

    /// <summary>What a run printed that matters.</summary>
    private sealed class RunOutput(ProgressReporter reporter)
    {
        public Queue<string> Tail { get; } = new();

        public bool Succeeded { get; private set; }

        public string? Error { get; private set; }

        public async Task AddAsync(string line)
        {
            if (line.Length == 0)
            {
                return;
            }

            Tail.Enqueue(line);
            if (Tail.Count > 20)
            {
                Tail.Dequeue();
            }

            switch (SteamCmdOutput.Parse(line))
            {
                case SteamCmdLine.Progress update:
                    await reporter.ReportAsync(update.Stage, update.Percent);
                    break;
                case SteamCmdLine.SelfUpdate:
                    await reporter.ReportAsync(InstallStages.Preparing, null);
                    break;
                case SteamCmdLine.Success:
                    Succeeded = true;
                    break;
                case SteamCmdLine.Failure failure:
                    Error = failure.Message;
                    break;
            }
        }
    }

    private sealed record RunResult(bool Succeeded, bool SelfUpdated, int ExitCode, string? Error, IReadOnlyList<string> Tail, bool Stalled);

    /// <summary>Forwards progress when the stage changes, the percent moves by a point, or two seconds passed.</summary>
    private sealed class ProgressReporter(Func<string, double?, Task> progress, TimeProvider clock)
    {
        private string? _stage;
        private double? _percent;
        private DateTimeOffset _last;

        public async Task ReportAsync(string stage, double? percent)
        {
            var now = clock.GetUtcNow();
            if (stage == _stage && (percent is null || (_percent is { } previous && Math.Abs(percent.Value - previous) < 1)) && now - _last < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _stage = stage;
            _percent = percent;
            _last = now;
            await progress(stage, percent);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: app {AppId} installed by SteamCMD (attempt {Attempt})")]
    private partial void LogInstalled(Guid serverId, int appId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: SteamCMD updated itself; running it again")]
    private partial void LogSelfUpdated(Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Server {ServerId}: SteamCMD attempt {Attempt} for app {AppId} failed (exit code {ExitCode}): {Error}. Last lines: {Tail}")]
    private partial void LogAttemptFailed(Guid serverId, int appId, int attempt, int exitCode, string error, string tail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: SteamCMD for app {AppId} cancelled")]
    private partial void LogCancelled(Guid serverId, int appId);
}
