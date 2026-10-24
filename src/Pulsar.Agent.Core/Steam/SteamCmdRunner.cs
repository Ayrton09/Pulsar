using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;

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
/// <param name="Login">The Steam account to log in with (Phase 13); null: anonymous.</param>
public sealed record SteamCmdJob(Guid ServerId, string ServerRoot, ProcessIdentity Identity, int AppId, string? Beta, string? BetaPassword, bool Validate,
    SteamLogin? Login = null)
{
    public override string ToString() => $"SteamCmdJob {{ ServerId = {ServerId}, AppId = {AppId}, Login = {Login?.UserName ?? "anonymous"} }}";
}

/// <summary>
/// Runs SteamCMD for a server (docs/architecture/06, "SteamCMD"). Each server has its own copy in its directory and
/// SteamCMD runs as the server's account, under a pseudo console (live output), in the server's Job: it can write only
/// where the server can, and a server can never tamper with another one's SteamCMD. The copy updates itself on its
/// first run (exit code 7); transient failures are retried; cancelling kills its Job.
/// With a Steam account, the copy is first rebuilt from the verified bootstrapper (the server's account could have
/// changed it to catch the password), and the login SteamCMD cached last time (<see cref="SteamLoginStore"/>) is put in
/// the copy's <c>config</c> folder for the run and taken back afterwards; runs of one account wait for each other, so
/// they never race on that login.
/// </summary>
public sealed partial class SteamCmdRunner(
    IGameProcessManager processes,
    ISteamCmdSource source,
    IServerFiles files,
    IServerFileManager fileManager,
    SteamLoginStore logins,
    SteamCmdOptions options,
    TimeProvider clock,
    ILogger<SteamCmdRunner> logger) : IDisposable
{
    /// <summary>The server's SteamCMD, relative to its directory.</summary>
    public const string Executable = ".pulsar/steamcmd/steamcmd.exe";

    /// <summary>Where a Steam account's cached login is while it is lent to the server's SteamCMD, relative to the server's directory.</summary>
    public const string LentLogin = WorkingDirectory + "/" + SteamLoginStore.CachedLoginFile;

    private const string WorkingDirectory = ".pulsar/steamcmd";
    private const int SelfUpdateExitCode = 7;

    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.MaxConcurrentRuns));
    private readonly Dictionary<Guid, SemaphoreSlim> _accounts = [];

    public void Dispose()
    {
        _slots.Dispose();
        lock (_accounts)
        {
            foreach (var account in _accounts.Values)
            {
                account.Dispose();
            }
        }
    }

    /// <param name="progress">Stage and percent (null when unknown), throttled; awaited, so keep it quick.</param>
    /// <exception cref="GameProcessException">The install failed (<c>steamcmd.*</c> codes).</exception>
    /// <exception cref="OperationCanceledException">Cancelled; SteamCMD was killed.</exception>
    public Task RunAsync(SteamCmdJob job, Func<string, double?, Task> progress, CancellationToken cancellationToken) =>
        RunAsync(job, progress, console: null, cancellationToken);

    /// <param name="console">
    /// The server's console: each line SteamCMD prints goes there as printed (as other panels show an install), with the
    /// account's password, Steam Guard code, user name and Steam ID and the beta password blanked out, and a note when a
    /// run is tried again. Null: nowhere.
    /// </param>
    public async Task RunAsync(SteamCmdJob job, Func<string, double?, Task> progress, ServerConsole? console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);
        CheckBetaPassword(job.BetaPassword);
        CheckLogin(job.Login);
        await progress(InstallStages.Queued, null);
        // The account first, then a slot: a run waiting for its account holds no slot another account could use.
        var account = job.Login is { } owner ? AccountLock(owner.AccountId) : null;
        if (account is not null)
        {
            await account.WaitAsync(cancellationToken);
        }

        try
        {
            await _slots.WaitAsync(cancellationToken);
            try
            {
                await RunJobAsync(job, progress, console, cancellationToken);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            account?.Release();
        }
    }

    private async Task RunJobAsync(SteamCmdJob job, Func<string, double?, Task> progress, ServerConsole? console, CancellationToken cancellationToken)
    {
        await progress(InstallStages.Preparing, null);
        await PrepareCopyAsync(job, cancellationToken);
        var attempts = 0;
        var selfUpdates = 0;
        var reset = false;
        var dropped = false;
        // A Steam Guard code answers a challenge: that run signs in afresh. Otherwise the cached login spares the password.
        var cached = job.Login is { GuardCode: null } login ? logins.Load(login) : null;
        console?.Note(job.Login is null
            ? $"SteamCMD downloads app {job.AppId.ToString(CultureInfo.InvariantCulture)} anonymously"
            : $"SteamCMD downloads app {job.AppId.ToString(CultureInfo.InvariantCulture)} with the panel's Steam account", clock.GetUtcNow());
        while (true)
        {
            var result = await RunOnceAsync(job, cached, progress, console, cancellationToken);
            if (result.Succeeded)
            {
                Keep(job.Login, result.CachedLogin);
                LogInstalled(job.ServerId, job.AppId, attempts + 1);
                return;
            }

            if (result.SelfUpdated && selfUpdates++ < options.MaxSelfUpdates)
            {
                // SteamCMD only updated itself and signed nobody in: the account's login stays as it was.
                LogSelfUpdated(job.ServerId);
                console?.Note("SteamCMD updated itself; running it again", clock.GetUtcNow());
                continue;
            }

            var (code, retry) = SteamCmdOutput.Classify(result.Error);
            if (job.Login is { } account)
            {
                if (code is "steamcmd.login_failed" or "steamcmd.guard_required" || (cached is not null && !SteamCmdOutput.LeavesLoginValid(result.Error)))
                {
                    // Refused, or failing before the app: the cached login may have expired or been revoked. Dropped, and
                    // once more with the password (which may then ask for a code).
                    logins.Forget(account.AccountId);
                    if (cached is not null && !dropped)
                    {
                        LogCachedLoginDropped(job.ServerId, account.UserName);
                        console?.Note("The Steam account's saved sign-in did not work; signing in with its password", clock.GetUtcNow());
                        cached = null;
                        dropped = true;
                        continue;
                    }
                }
                else
                {
                    Keep(account, result.CachedLogin);
                }

                // What this run cached spares the password next time, and a Steam Guard code it already spent.
                cached = logins.Load(account);
            }

            attempts++;
            var message = result.Message ?? (result.Stalled
                ? $"SteamCMD printed nothing for {options.StallTimeout.TotalMinutes:0} minutes."
                : $"SteamCMD exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
            LogAttemptFailed(job.ServerId, job.AppId, attempts, result.ExitCode, message, string.Join(" | ", result.Tail));
            if (!retry || attempts >= options.MaxAttempts)
            {
                throw new GameProcessException(code, message);
            }

            await progress(InstallStages.Retrying, null);
            if (!reset && SteamCmdOutput.NeedsReset(result.Error))
            {
                // Through the server's files: the folder is the game's to write, links included.
                files.Delete(job.ServerRoot, $"steamapps/appmanifest_{job.AppId.ToString(CultureInfo.InvariantCulture)}.acf");
                job = job with { Validate = true };
                reset = true;
                LogReset(job.ServerId, job.AppId);
                console?.Note("The download was stuck (state 0x6); SteamCMD runs again checking every file", clock.GetUtcNow());
                continue;
            }

            var delay = options.RetryDelays.Count == 0 ? TimeSpan.Zero : options.RetryDelays[Math.Min(attempts - 1, options.RetryDelays.Count - 1)];
            console?.Note(string.Create(CultureInfo.InvariantCulture,
                $"{ForConsole(job, message)} Trying again in {delay.TotalSeconds:0} s (attempt {attempts + 1} of {options.MaxAttempts})"), clock.GetUtcNow());
            await Task.Delay(delay, clock, cancellationToken);
        }
    }

    private SemaphoreSlim AccountLock(Guid accountId)
    {
        lock (_accounts)
        {
            if (!_accounts.TryGetValue(accountId, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _accounts.Add(accountId, semaphore);
            }

            return semaphore;
        }
    }

    /// <summary>The command line: argv elements, never a string for a shell (docs/architecture/06).</summary>
    /// <param name="cachedLogin">The account's cached login is in place: the user name alone signs in (no password on the command line).</param>
    public static IReadOnlyList<string> Arguments(SteamCmdJob job, bool cachedLogin = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        var arguments = new List<string>
        {
            // Exit on the first failed command instead of carrying on, and never wait for a password at a prompt.
            "+@ShutdownOnFailedCommand", "1",
            "+@NoPromptForPassword", "1",
            // Before the login, or SteamCMD ignores it (docs/architecture/06).
            "+force_install_dir", Path.TrimEndingDirectorySeparator(job.ServerRoot),
            "+login",
        };
        if (job.Login is not { } login)
        {
            arguments.Add("anonymous");
        }
        else if (cachedLogin)
        {
            arguments.Add(login.UserName);
        }
        else
        {
            // SteamCMD's "login <user> <password> [<Steam Guard code>]".
            CheckLogin(login);
            arguments.AddRange([login.UserName, login.Password]);
            if (login.GuardCode is { Length: > 0 } code)
            {
                arguments.Add(code);
            }
        }

        arguments.AddRange(["+app_update", job.AppId.ToString(CultureInfo.InvariantCulture)]);
        if (job.Beta is { } beta)
        {
            arguments.AddRange(["-beta", beta]);
            if (job.BetaPassword is { Length: > 0 } password)
            {
                CheckBetaPassword(password);
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

    /// <summary>
    /// A beta branch password is the value of a server variable its owner sets, put on SteamCMD's command line: SteamCMD
    /// splits that line on spaces and quotes and reads an argument starting with + or - as a command or an option, so a
    /// password like "x +quit" would not reach Steam whole and would add commands. The rule the panel applies to Steam
    /// account passwords applies (never with the value in the message).
    /// </summary>
    private static void CheckBetaPassword(string? password)
    {
        if (!IsSafeArgument(password))
        {
            throw new GameProcessException("steamcmd.beta_password_invalid",
                "The beta branch password must be at most 64 characters, without spaces, double quotes or control characters, and not start with + or -.");
        }
    }

    /// <summary>
    /// The account's password and Steam Guard code go on the same command line: the agent applies the panel's rules itself
    /// (a password by the rule above, a code is 5 ASCII letters or digits) instead of trusting what it was sent, and never
    /// puts either value in the message.
    /// </summary>
    private static void CheckLogin(SteamLogin? login)
    {
        if (login is null)
        {
            return;
        }

        if (!IsSafeArgument(login.Password))
        {
            throw new GameProcessException("steamcmd.login_failed",
                "The Steam account's password must be at most 64 characters, without spaces, double quotes or control characters, and not start with + or -.");
        }

        if (login.GuardCode is { Length: > 0 } code && (code.Length != 5 || !code.All(char.IsAsciiLetterOrDigit)))
        {
            throw new GameProcessException("steamcmd.guard_required", "A Steam Guard code is 5 letters or digits.");
        }
    }

    private static bool IsSafeArgument(string? value) =>
        value is not { Length: > 0 }
        || (value.Length <= 64 && value[0] is not ('+' or '-') && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '"'));

    /// <summary>
    /// The server's SteamCMD. An anonymous run keeps the copy it finds (it keeps itself up to date; nothing secret is at
    /// stake). A run with an account first deletes the whole folder, as the server's account and never through a link,
    /// and starts again from the bootstrapper verified as Valve's: the server's account controls that folder, and a
    /// steamcmd.exe it replaced or a DLL it planted beside it would get the password. The copy then updates itself again.
    /// </summary>
    private async Task PrepareCopyAsync(SteamCmdJob job, CancellationToken cancellationToken)
    {
        if (job.Login is null)
        {
            using var existing = files.OpenRead(job.ServerRoot, Executable);
            if (existing is not null)
            {
                return;
            }
        }
        else
        {
            try
            {
                fileManager.Delete(new ProvisionedServer(job.ServerRoot, job.Identity), WorkingDirectory);
            }
            catch (FileManagerException ex) when (ex.Code == FileErrors.NotFound)
            {
                // No copy yet.
            }
            catch (FileManagerException ex)
            {
                throw new GameProcessException("steamcmd.failed", $"The server's SteamCMD could not be replaced before signing in: {ex.Message}", ex);
            }

            LogCopyRebuilt(job.ServerId);
        }

        var bootstrapper = await source.GetAsync(cancellationToken);
        await using var stream = File.OpenRead(bootstrapper);
        files.Write(job.ServerRoot, Executable, stream);
    }

    /// <param name="cached">The account's cached login, lent to this run; null signs in with the password (or anonymously).</param>
    private async Task<RunResult> RunOnceAsync(SteamCmdJob job, byte[]? cached, Func<string, double?, Task> progress, ServerConsole? console,
        CancellationToken cancellationToken)
    {
        if (job.Login is null)
        {
            return await RunProcessAsync(job, cachedLogin: false, progress, console, cancellationToken);
        }

        Lend(job, cached);
        RunResult result;
        try
        {
            result = await RunProcessAsync(job, cached is not null, progress, console, cancellationToken);
        }
        catch
        {
            Keep(job.Login, TakeBackIfAlone(job));
            throw;
        }

        return result with { CachedLogin = TakeBackIfAlone(job) };
    }

    /// <summary>
    /// <see cref="TakeBack"/> once nothing of the server's account runs outside the run's Job. The sweep before the
    /// install happened before SteamCMD started: a process of the server that outlived it could write its own
    /// <c>config.vdf</c> meanwhile, and the agent would lend that to every server using the account. If the sweep killed
    /// anything, or failed, the file is deleted and not kept.
    /// </summary>
    private byte[]? TakeBackIfAlone(SteamCmdJob job)
    {
        int killed;
        try
        {
            killed = processes.TerminateStrayProcesses(job.Identity);
        }
        catch
        {
            // Nobody can say what ran beside the run: the login still leaves the server's folder, and is not kept.
            TakeBack(job);
            throw;
        }

        var cached = TakeBack(job);
        if (killed > 0)
        {
            LogLoginNotKept(job.ServerId, killed);
            return null;
        }

        return cached;
    }

    private async Task<RunResult> RunProcessAsync(SteamCmdJob job, bool cachedLogin, Func<string, double?, Task> progress, ServerConsole? console,
        CancellationToken cancellationToken)
    {
        var spec = new ProcessLaunchSpec(job.ServerId, job.ServerRoot, Executable, Arguments(job, cachedLogin), WorkingDirectory,
            new Dictionary<string, string>(), ConsoleMode.Pty, ConsoleEncoding.Utf8, job.Identity);
        var process = await processes.StartAsync(spec, cancellationToken);
        using var stall = new CancellationTokenSource(options.StallTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall.Token);
        // Everything secret that went on the command line, should SteamCMD echo it (the beta password too, with or without an account).
        string[] secrets = [job.Login?.Password ?? string.Empty, job.Login?.GuardCode ?? string.Empty, job.BetaPassword ?? string.Empty];
        Action<string>? echo = console is null ? null : line => console.Write(ForConsole(job, line) + "\r\n", clock.GetUtcNow());
        var output = new RunOutput(new ProgressReporter(progress, clock), secrets, echo);
        // A pseudo console's output ends only when it is closed (disposing the process): read it alongside.
        var pump = PumpAsync(process, output, stall);
        try
        {
            var exitCode = await process.Exited.WaitAsync(linked.Token);
            await process.DisposeAsync();
            await pump;
            return new RunResult(output.Succeeded && exitCode == 0, exitCode == SelfUpdateExitCode && !output.Succeeded, exitCode, output.Error,
                output.Message, [.. output.Tail], Stalled: false);
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

            // Stopped at a Steam Guard prompt, or printing nothing for too long.
            return new RunResult(false, false, 1, output.Error, output.Message, [.. output.Tail], Stalled: output.Error is null);
        }
    }

    /// <summary>Puts the account's cached login where SteamCMD reads it, or makes sure none is there (a full sign-in).</summary>
    private void Lend(SteamCmdJob job, byte[]? cached)
    {
        files.Delete(job.ServerRoot, LentLogin);
        if (cached is not null)
        {
            using var content = new MemoryStream(cached, writable: false);
            files.Write(job.ServerRoot, LentLogin, content);
        }
    }

    /// <summary>
    /// Takes the login SteamCMD cached out of the server's folder; null when there is none (or something far too large
    /// for one). Read through the server's files (as any of the server's files), with a size limit.
    /// </summary>
    private byte[]? TakeBack(SteamCmdJob job)
    {
        byte[]? content = null;
        using (var stream = files.OpenRead(job.ServerRoot, LentLogin))
        {
            if (stream is not null)
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while (buffer.Length <= SteamLoginStore.MaxSize && (read = stream.Read(chunk)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                }

                content = buffer.Length <= SteamLoginStore.MaxSize ? buffer.ToArray() : null;
            }
        }

        files.Delete(job.ServerRoot, LentLogin);
        return content is { Length: > 0 } ? content : null;
    }

    /// <summary>Keeps what a run that may have signed in cached, for the account with these credentials.</summary>
    private void Keep(SteamLogin? login, byte[]? cached)
    {
        if (login is not null && cached is not null)
        {
            logins.Save(login, cached);
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

            // A prompt has no line end: SteamCMD would wait for a Steam Guard code nobody types. Stop it now. It may arrive
            // in pieces (a read ends anywhere) or with VT sequences inside, so the text of the unfinished line is tested
            // too, and the lines this read completed.
            var added = lines.Add(chunk.Text);
            if (SteamCmdOutput.IsGuardPrompt(chunk.Text) || added.Any(SteamCmdOutput.IsGuardPrompt) || SteamCmdOutput.IsGuardPrompt(lines.PendingText))
            {
                if (output.Error is null)
                {
                    output.GuardRequested();
                }

                await stall.CancelAsync();
            }

            foreach (var line in added)
            {
                await output.AddAsync(line);
            }
        }
    }

    /// <summary>
    /// Text for the server's console, which whoever may read the server's console sees: the run's secrets and, with an
    /// account, its user name and Steam ID are blanked out (the panel shows the account's name only to those who manage
    /// the panel's settings). Each line is matched whole: a secret the pseudo console wrapped across two lines would show
    /// its parts, which SteamCMD's short login lines never need (residual).
    /// </summary>
    internal static string ForConsole(SteamCmdJob job, string text)
    {
        foreach (var hidden in (string?[])[job.Login?.Password, job.Login?.GuardCode, job.BetaPassword])
        {
            if (hidden is { Length: > 0 })
            {
                text = text.Replace(hidden, "***", StringComparison.Ordinal);
            }
        }

        return HideAccount(job.Login, text);
    }

    /// <summary>The account's user name and Steam ID blanked out of a text for the server's console; anonymous: as it is.</summary>
    public static string HideAccount(SteamLogin? login, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (login is not { UserName.Length: > 0 })
        {
            return text;
        }

        // Steam names are not case-sensitive: SteamCMD may print it otherwise than it was typed.
        return SteamId().Replace(text.Replace(login.UserName, "***", StringComparison.OrdinalIgnoreCase), "[***]");
    }

    [GeneratedRegex(@"\[U:[0-9]:[0-9]{1,12}\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SteamId();

    /// <summary>
    /// What a run printed that matters. Lines are read as printed (a one-letter password or a five-digit code also appears
    /// in "Success!" or a progress line), and kept with the account's password and code, should SteamCMD echo them,
    /// blanked out: <see cref="Tail"/> and <see cref="Message"/> are what gets logged and reported.
    /// </summary>
    /// <param name="echo">Each line printed, as printed, for the server's console (which blanks out what it must).</param>
    private sealed class RunOutput(ProgressReporter reporter, IReadOnlyList<string> secrets, Action<string>? echo)
    {
        private const int MaxMessageLength = 300;

        public Queue<string> Tail { get; } = new();

        public bool Succeeded { get; private set; }

        /// <summary>The error as printed, to classify it: never logged or reported.</summary>
        public string? Error { get; private set; }

        /// <summary>The error with the secrets blanked out.</summary>
        public string? Message { get; private set; }

        public void GuardRequested() => Error = Message = "Steam asked for a Steam Guard code.";

        public async Task AddAsync(string line)
        {
            if (line.Length == 0)
            {
                return;
            }

            echo?.Invoke(line);
            Tail.Enqueue(Redact(line));
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
                    (Error, Message) = (failure.Message, Cut(Redact(line.Trim())));
                    break;
                case SteamCmdLine.GuardRequested guard when Error is null:
                    (Error, Message) = (guard.Message, Cut(Redact(line.Trim())));
                    break;
            }
        }

        // Blanked out before the line is cut: a secret across the cut would leave its first characters.
        private string Redact(string text) =>
            secrets.Where(s => s.Length > 0).Aggregate(text, (current, secret) => current.Replace(secret, "***", StringComparison.Ordinal));

        private static string Cut(string text) => text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;
    }

    /// <param name="Error">As printed, to classify the failure.</param>
    /// <param name="Message">The error with the account's secrets blanked out, for logs and reports.</param>
    /// <param name="CachedLogin">The login SteamCMD cached, taken out of the server's folder (runs with an account).</param>
    private sealed record RunResult(bool Succeeded, bool SelfUpdated, int ExitCode, string? Error, string? Message, IReadOnlyList<string> Tail, bool Stalled,
        byte[]? CachedLogin = null);

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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Server {ServerId}: app {AppId} is stuck in state 0x6; its app manifest was removed and SteamCMD runs again validating every file")]
    private partial void LogReset(Guid serverId, int appId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: SteamCMD failed with the cached login of {UserName}; signing in with the password")]
    private partial void LogCachedLoginDropped(Guid serverId, string userName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: SteamCMD signs in with an account; its copy starts again from the verified bootstrapper")]
    private partial void LogCopyRebuilt(Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Server {ServerId}: {Killed} process(es) of the server's account were still running after SteamCMD; the login it cached is not kept")]
    private partial void LogLoginNotKept(Guid serverId, int killed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: SteamCMD for app {AppId} cancelled")]
    private partial void LogCancelled(Guid serverId, int appId);
}
