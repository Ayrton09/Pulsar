using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Steam;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

public sealed class SteamCmdOutputTests
{
    [Theory]
    [InlineData(" Update state (0x61) downloading, progress: 9.44 (238319757 / 2524405728)", "downloading", 9.44)]
    [InlineData(" Update state (0x81) verifying update, progress: 100.00 (74000614728 / 74000614728)", "verifying", 100)]
    [InlineData(" Update state (0x101) committing, progress: 50.00 (5 / 10)", "committing", 50)]
    [InlineData(" Update state (0x3) reconfiguring, progress: 0.00 (0 / 0)", "installing", 0)]
    public void ReadsProgressLines(string line, string stage, double percent)
    {
        var progress = Assert.IsType<SteamCmdLine.Progress>(SteamCmdOutput.Parse(line));

        Assert.Equal(stage, progress.Stage);
        Assert.Equal(percent, progress.Percent, 2);
    }

    [Fact]
    public void ReadsResultsAndIgnoresTranslatedChatter()
    {
        Assert.IsType<SteamCmdLine.Success>(SteamCmdOutput.Parse("Success! App '730' fully installed."));
        Assert.IsType<SteamCmdLine.Success>(SteamCmdOutput.Parse("Success! App '232330' already up to date."));
        Assert.Equal("ERROR! Failed to install app '232330' (Missing configuration)",
            Assert.IsType<SteamCmdLine.Failure>(SteamCmdOutput.Parse("ERROR! Failed to install app '232330' (Missing configuration)")).Message);
        Assert.IsType<SteamCmdLine.Failure>(SteamCmdOutput.Parse("Error! App '730' state is 0x202 after update job."));
        Assert.IsType<SteamCmdLine.SelfUpdate>(SteamCmdOutput.Parse("[  0%] Buscando actualizaciones disponibles..."));
        Assert.IsType<SteamCmdLine.SelfUpdate>(SteamCmdOutput.Parse("[----] Verificando instalación..."));
        Assert.Null(SteamCmdOutput.Parse("Connecting anonymously to Steam Public...OK"));
        Assert.Null(SteamCmdOutput.Parse("Esperando la información del usuario...OK"));
    }

    [Theory]
    [InlineData("ERROR! Failed to install app '232330' (Missing configuration)", "steamcmd.failed", true)]
    [InlineData("ERROR! Timed out waiting for AppInfo update.", "steamcmd.failed", true)]
    [InlineData("ERROR! Failed to install app '730' (Disk write failure)", "steamcmd.disk", false)]
    [InlineData("Error! App '730' state is 0x202 after update job.", "steamcmd.disk", false)]
    [InlineData("ERROR! Failed to install app '730' (No subscription)", "steamcmd.app_unavailable", false)]
    [InlineData(null, "steamcmd.failed", true)]
    public void ClassifiesFailures(string? error, string code, bool retry)
    {
        Assert.Equal((code, retry), SteamCmdOutput.Classify(error));
    }

    [Theory]
    [InlineData("Login Failure: Invalid Password", "steamcmd.login_failed")]
    [InlineData("Logging in user 'servers' [U:1:1234] to Steam Public...FAILED (Invalid Password)", "steamcmd.login_failed")]
    [InlineData("FAILED with result code 5", "steamcmd.login_failed")]
    [InlineData("Login Failure: Invalid Login Auth Code", "steamcmd.guard_required")]
    [InlineData("FAILED (Two-factor code mismatch)", "steamcmd.guard_required")]
    [InlineData("FAILED with result code 63", "steamcmd.guard_required")]
    [InlineData("Steam asked for a Steam Guard code.", "steamcmd.guard_required")]
    [InlineData("FAILED (Rate Limit Exceeded)", "steamcmd.rate_limited")]
    [InlineData("FAILED with result code 84", "steamcmd.rate_limited")]
    // A beta branch's password is not a login.
    [InlineData("ERROR! Failed to install app '232330' (Invalid Password)", "steamcmd.app_unavailable")]
    public void LoginFailuresAreNotRetried(string error, string code)
    {
        Assert.Equal((code, false), SteamCmdOutput.Classify(error));
    }

    [Fact]
    public void LoginLinesAndGuardPromptsAreRead()
    {
        Assert.IsType<SteamCmdLine.Failure>(SteamCmdOutput.Parse("Login Failure: Invalid Password"));
        Assert.IsType<SteamCmdLine.Failure>(SteamCmdOutput.Parse("Logging in user 'servers' to Steam Public...FAILED (Rate Limit Exceeded)"));
        Assert.IsType<SteamCmdLine.GuardRequested>(SteamCmdOutput.Parse("Steam Guard code:"));
        Assert.IsType<SteamCmdLine.GuardRequested>(SteamCmdOutput.Parse("Two-factor code:"));
        Assert.Null(SteamCmdOutput.Parse("Logging in user 'servers' [U:1:1234] to Steam Public...OK"));
    }

    [Theory]
    [InlineData("ERROR! Failed to install app '232330' (Missing configuration)", true)]
    [InlineData("Error! App '730' state is 0x202 after update job.", true)]
    [InlineData("ERROR! Timed out waiting for AppInfo update.", true)]
    [InlineData("FAILED (Rate Limit Exceeded)", true)]
    [InlineData("Logging in user 'servers' to Steam Public...FAILED (Expired)", false)]
    [InlineData("Login Failure: Invalid Password", false)]
    [InlineData(null, false)]
    public void OnlyFailuresPastTheLoginLeaveACachedLoginValid(string? error, bool valid)
    {
        Assert.Equal(valid, SteamCmdOutput.LeavesLoginValid(error));
    }

    [Theory]
    [InlineData("Error! App '232330' state is 0x6 after update job.", true)]
    [InlineData("Error! App '232330' state is 0x602 after update job.", false)]
    [InlineData("Error! App '232330' state is 0x202 after update job.", false)]
    [InlineData(null, false)]
    public void OnlyState0x6NeedsAReset(string? error, bool reset)
    {
        Assert.Equal(reset, SteamCmdOutput.NeedsReset(error));
    }
}

public sealed class SteamCmdRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-steamcmd-runner", Guid.NewGuid().ToString("N"));
    private readonly FakeProcessManager _processes = new();
    private readonly FakeSource _source;
    private readonly List<(string Stage, double? Percent)> _progress = [];
    private readonly SteamLoginStore _logins;

    public SteamCmdRunnerTests()
    {
        Directory.CreateDirectory(_root);
        _source = new FakeSource(Path.Combine(_root, "bootstrapper.exe"));
        _logins = new SteamLoginStore(new AgentPaths(Path.Combine(_root, "agent")), new PlainStateProtector());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task InstallsAndReportsProgress()
    {
        _processes.OnStarted = (process, _) =>
        {
            process.Print("Connecting anonymously to Steam Public...OK\r\n");
            process.Print(" Update state (0x61) downloading, progress: 10.00 (10 / 100)\r\n Update state (0x61) downloading, progress: 10.20 (102 / 1000)\r\n");
            process.Print(" Update state (0x61) downloading, progress: 60.00 (60 / 100)\r\n Update state (0x81) verifying update, progress: 100.00 (100 / 100)\r\n");
            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            ("queued", null), ("preparing", null), ("downloading", 10), ("downloading", 60), ("verifying", 100),
        ], _progress);
        var spec = Assert.Single(_processes.Specs);
        Assert.Equal(SteamCmdRunner.Executable, spec.Executable);
        Assert.Equal(ConsoleMode.Pty, spec.ConsoleMode);
        Assert.Equal(ProcessIdentity.Account("pulsar_abcd2345"), spec.Identity);
        Assert.Equal("bootstrapper", File.ReadAllText(Path.Combine(_root, "server", ".pulsar", "steamcmd", "steamcmd.exe")));
    }

    [Fact]
    public void TheCommandLineSetsTheDirectoryBeforeLoggingIn()
    {
        var arguments = SteamCmdRunner.Arguments(Job() with { Beta = "public_beta", BetaPassword = "pw", Validate = true });

        Assert.Equal(
        [
            "+@ShutdownOnFailedCommand", "1", "+@NoPromptForPassword", "1", "+force_install_dir", Path.Combine(_root, "server"), "+login", "anonymous",
            "+app_update", "232330", "-beta", "public_beta", "-betapassword", "pw", "validate", "+quit",
        ], arguments);
        Assert.DoesNotContain("pw", Job().ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x +quit")]
    [InlineData("+quit")]
    [InlineData("-beta")]
    [InlineData("a\"b")]
    [InlineData("tab\there")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public async Task ABetaPasswordSteamCmdWouldNotReceiveWholeFailsTheInstall(string password)
    {
        // SteamCMD splits its command line on spaces and reads "+..." as one of its commands.
        _processes.OnStarted = (process, _) =>
        {
            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        var failed = await Assert.ThrowsAsync<GameProcessException>(() =>
            runner.RunAsync(Job() with { Beta = "public_beta", BetaPassword = password }, Report, TestContext.Current.CancellationToken));

        Assert.Equal("steamcmd.beta_password_invalid", failed.Code);
        Assert.DoesNotContain(password, failed.Message, StringComparison.Ordinal);
        Assert.Empty(_processes.Specs);
        Assert.Throws<GameProcessException>(() => SteamCmdRunner.Arguments(Job() with { Beta = "public_beta", BetaPassword = password }));
    }

    [Fact]
    public async Task ASelfUpdateRunsSteamCmdAgainWithoutCountingAsAFailure()
    {
        _processes.OnStarted = (process, _) =>
        {
            if (_processes.Started.Count == 1)
            {
                process.Print("[ 50%] Downloading update (1,000 of 2,000 KB)...\r\n");
                process.Exit(7);
                return;
            }

            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);

        Assert.Equal(2, _processes.Started.Count);
        Assert.Contains(("preparing", null), _progress);
    }

    [Fact]
    public async Task TransientFailuresAreRetried()
    {
        _processes.OnStarted = (process, _) =>
        {
            if (_processes.Started.Count == 1)
            {
                process.Print("ERROR! Failed to install app '232330' (Missing configuration)\r\n");
                process.Exit(8);
                return;
            }

            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);

        Assert.Equal(2, _processes.Started.Count);
        Assert.Contains(("retrying", null), _progress);
    }

    [Fact]
    public async Task AttemptsAreBounded()
    {
        _processes.OnStarted = (process, _) => process.Exit(8);
        using var runner = Runner();

        var error = await Assert.ThrowsAsync<GameProcessException>(() => runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken));

        Assert.Equal("steamcmd.failed", error.Code);
        Assert.Equal(3, _processes.Started.Count);
        Assert.Contains("exited with code 8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFullDiskIsNotRetried()
    {
        _processes.OnStarted = (process, _) =>
        {
            process.Print("Error! App '730' state is 0x202 after update job.\r\n");
            process.Exit(8);
        };
        using var runner = Runner();

        var error = await Assert.ThrowsAsync<GameProcessException>(() => runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken));

        Assert.Equal("steamcmd.disk", error.Code);
        Assert.Single(_processes.Started);
    }

    [Fact]
    public async Task AnInstallStuckInState0x6IsResetAndValidated()
    {
        // SteamCMD cannot update files whose build it has no manifests for (copied from another install, or an
        // interrupted update): its app manifest goes, and a validating run checks every file instead.
        var manifest = Path.Combine(_root, "server", "steamapps", "appmanifest_232330.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"StateFlags\" \"6\" }");
        var manifestAtSecondRun = true;
        _processes.OnStarted = (process, _) =>
        {
            if (_processes.Started.Count == 1)
            {
                process.Print("Error! App '232330' state is 0x6 after update job.\r\n");
                process.Exit(8);
                return;
            }

            manifestAtSecondRun = File.Exists(manifest);
            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);

        Assert.False(manifestAtSecondRun);
        Assert.DoesNotContain("validate", _processes.Specs[0].Arguments);
        Assert.Contains("validate", _processes.Specs[1].Arguments);
        Assert.Contains(("retrying", null), _progress);
    }

    [Fact]
    public async Task AnInstallStillStuckAfterItsResetFails()
    {
        var manifest = Path.Combine(_root, "server", "steamapps", "appmanifest_232330.acf");
        _processes.OnStarted = (process, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            File.WriteAllText(manifest, "written again");
            process.Print("Error! App '232330' state is 0x6 after update job.\r\n");
            process.Exit(8);
        };
        using var runner = Runner();

        var error = await Assert.ThrowsAsync<GameProcessException>(() => runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken));

        Assert.Equal("steamcmd.failed", error.Code);
        Assert.Equal(3, _processes.Started.Count);
        Assert.True(File.Exists(manifest)); // reset once, not at every attempt
        Assert.All(_processes.Specs.Skip(1), spec => Assert.Contains("validate", spec.Arguments));
    }

    [Fact]
    public async Task CancellingKillsSteamCmd()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _processes.OnStarted = (process, _) =>
        {
            process.Print(" Update state (0x61) downloading, progress: 1.00 (1 / 100)\r\n");
            cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
        };
        using var runner = Runner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Job(), Report, cancel.Token));

        Assert.Equal(1, Assert.Single(_processes.Started).TerminatedWith);
    }

    [Fact]
    public async Task AStuckRunIsKilledAndRetried()
    {
        var clock = new FakeTimeProvider();
        _processes.OnStarted = (process, _) =>
        {
            if (_processes.Started.Count == 2)
            {
                process.Print("Success! App '232330' fully installed.\r\n");
                process.Exit(0);
            }
        };
        using var runner = Runner(clock);

        var run = runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);
        while (_processes.Started.Count == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        clock.Advance(TimeSpan.FromMinutes(11));
        while (_processes.Started.Count < 2)
        {
            clock.Advance(TimeSpan.FromSeconds(10)); // the retry delay
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await run;
        Assert.Equal(1, _processes.Started[0].TerminatedWith);
    }

    [Fact]
    public async Task TheServersCopyIsKeptOnceItExists()
    {
        var copy = Path.Combine(_root, "server", ".pulsar", "steamcmd", "steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, "updated by itself");
        _processes.OnStarted = (process, _) =>
        {
            process.Print("Success! App '232330' already up to date.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);

        Assert.Equal("updated by itself", File.ReadAllText(copy));
        Assert.Equal(0, _source.Calls);
    }

    [Fact]
    public async Task ARunWithAnAccountRebuildsTheServersCopyFromTheBootstrapper()
    {
        // The server's account controls its copy: a replaced steamcmd.exe or a DLL planted beside it would get the password.
        var folder = Path.Combine(_root, "server", ".pulsar", "steamcmd");
        Directory.CreateDirectory(Path.Combine(folder, "package"));
        File.WriteAllText(Path.Combine(folder, "steamcmd.exe"), "planted");
        File.WriteAllText(Path.Combine(folder, "version.dll"), "planted");
        File.WriteAllText(Path.Combine(folder, "package", "steam_cmd_win32.zip"), "planted");
        var seen = new List<string>();
        _processes.OnStarted = (process, _) =>
        {
            seen.Add(string.Join(",", Directory.EnumerateFileSystemEntries(folder).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal)));
            seen.Add(File.ReadAllText(Path.Combine(folder, "steamcmd.exe")));
            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job() with { Login = new SteamLogin(Guid.NewGuid(), "servers", "hunter2-password") }, Report,
            TestContext.Current.CancellationToken);

        Assert.Equal(["steamcmd.exe", "bootstrapper"], seen);
        Assert.Equal(1, _source.Calls);
    }

    [Fact]
    public void AnAccountSignsInWithItsPasswordOrItsCachedLogin()
    {
        var login = new SteamLogin(Guid.NewGuid(), "servers", "hunter2-password", "F7K2Q");

        var full = SteamCmdRunner.Arguments(Job() with { Login = login });
        var cached = SteamCmdRunner.Arguments(Job() with { Login = login with { GuardCode = null } }, cachedLogin: true);

        Assert.Equal(["+login", "servers", "hunter2-password", "F7K2Q", "+app_update"], full.SkipWhile(a => a != "+login").Take(5));
        Assert.Equal(["+login", "servers", "+app_update"], cached.SkipWhile(a => a != "+login").Take(3));
        Assert.DoesNotContain("hunter2", (Job() with { Login = login }).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCachedLoginIsKeptByTheAgentAndLentForEachRunOnly()
    {
        var account = Guid.NewGuid();
        var login = new SteamLogin(account, "servers", "hunter2-password");
        var seen = new List<string?>();
        _processes.OnStarted = (process, spec) =>
        {
            // What SteamCMD finds when it starts, and the login it caches when it is done.
            seen.Add(File.Exists(CachedLogin) ? File.ReadAllText(CachedLogin) : null);
            Directory.CreateDirectory(Path.GetDirectoryName(CachedLogin)!);
            File.WriteAllText(CachedLogin, $"token-{_processes.Started.Count}");
            process.Print("Logging in user 'servers' to Steam Public...OK\r\nSuccess! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job() with { Login = login }, Report, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(CachedLogin)); // never left in the server's folder
        Assert.Equal("token-1", Cached(account));

        await runner.RunAsync(Job() with { Login = login }, Report, TestContext.Current.CancellationToken);

        Assert.Equal([null, "token-1"], seen);
        Assert.Contains("hunter2-password", _processes.Specs[0].Arguments);
        Assert.DoesNotContain("hunter2-password", _processes.Specs[1].Arguments); // the cached login spares the password
        Assert.Equal("token-2", Cached(account));
        Assert.False(File.Exists(CachedLogin));
    }

    [Fact]
    public async Task ACachedLoginSteamRefusesIsForgottenAndThePasswordUsed()
    {
        var account = Guid.NewGuid();
        _logins.Save(new SteamLogin(account, "servers", "hunter2-password"), "expired"u8.ToArray());
        _processes.OnStarted = (process, spec) =>
        {
            if (!spec.Arguments.Contains("hunter2-password"))
            {
                process.Print("Logging in user 'servers' to Steam Public...FAILED (Invalid Password)\r\n");
                process.Exit(5);
                return;
            }

            File.WriteAllText(CachedLogin, "fresh");
            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job() with { Login = new SteamLogin(account, "servers", "hunter2-password") }, Report, TestContext.Current.CancellationToken);

        Assert.Equal(2, _processes.Started.Count);
        Assert.Equal("fresh", Cached(account));
    }

    [Fact]
    public async Task ACachedLoginThatFailsBeforeTheAppIsDroppedAndThePasswordUsedOnce()
    {
        // An expired cache does not always say so: anything but an error about the app drops it, once.
        var account = Guid.NewGuid();
        var login = new SteamLogin(account, "servers", "hunter2-password");
        using (var runner = Runner())
        {
            _processes.OnStarted = (process, _) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachedLogin)!);
                File.WriteAllText(CachedLogin, "first");
                process.Print("Success! App '232330' fully installed.\r\n");
                process.Exit(0);
            };
            await runner.RunAsync(Job() with { Login = login }, Report, TestContext.Current.CancellationToken);

            _processes.OnStarted = (process, spec) =>
            {
                if (!spec.Arguments.Contains("hunter2-password"))
                {
                    process.Exit(8);
                    return;
                }

                File.WriteAllText(CachedLogin, "fresh");
                process.Print("Success! App '232330' fully installed.\r\n");
                process.Exit(0);
            };
            await runner.RunAsync(Job() with { Login = login }, Report, TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, _processes.Started.Count);
        Assert.Contains("hunter2-password", _processes.Specs[2].Arguments);
        Assert.Equal("fresh", Cached(account));
    }

    [Fact]
    public async Task AnErrorAboutTheAppKeepsTheCachedLogin()
    {
        var account = Guid.NewGuid();
        var login = new SteamLogin(account, "servers", "hunter2-password");
        _processes.OnStarted = (process, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachedLogin)!);
            File.WriteAllText(CachedLogin, "token");
            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();
        await runner.RunAsync(Job() with { Login = login }, Report, TestContext.Current.CancellationToken);
        _processes.OnStarted = (process, _) =>
        {
            if (_processes.Started.Count == 2)
            {
                process.Print("ERROR! Failed to install app '232330' (Missing configuration)\r\n");
                process.Exit(8);
                return;
            }

            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };

        await runner.RunAsync(Job() with { Login = login }, Report, TestContext.Current.CancellationToken);

        Assert.Equal(3, _processes.Started.Count);
        Assert.All(_processes.Specs.Skip(1), spec => Assert.DoesNotContain("hunter2-password", spec.Arguments));
    }

    [Fact]
    public async Task ACachedLoginIsNotUsedOnceTheAccountsPasswordChanged()
    {
        var account = Guid.NewGuid();
        var seen = new List<string?>();
        _processes.OnStarted = (process, _) =>
        {
            seen.Add(File.Exists(CachedLogin) ? File.ReadAllText(CachedLogin) : null);
            Directory.CreateDirectory(Path.GetDirectoryName(CachedLogin)!);
            File.WriteAllText(CachedLogin, $"token-{_processes.Started.Count}");
            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job() with { Login = new SteamLogin(account, "servers", "hunter2-password") }, Report, TestContext.Current.CancellationToken);
        await runner.RunAsync(Job() with { Login = new SteamLogin(account, "servers", "new-password") }, Report, TestContext.Current.CancellationToken);
        await runner.RunAsync(Job() with { Login = new SteamLogin(account, "renamed", "new-password") }, Report, TestContext.Current.CancellationToken);

        Assert.Equal([null, null, null], seen);
        Assert.Contains("new-password", _processes.Specs[1].Arguments);
        Assert.Contains("new-password", _processes.Specs[2].Arguments);
    }

    [Fact]
    public async Task ARetryAfterSigningInWithACodeUsesTheLoginItCached()
    {
        // The code is spent: the retry signs in with what the first run cached.
        var account = Guid.NewGuid();
        var seen = new List<string?>();
        _processes.OnStarted = (process, _) =>
        {
            seen.Add(File.Exists(CachedLogin) ? File.ReadAllText(CachedLogin) : null);
            Directory.CreateDirectory(Path.GetDirectoryName(CachedLogin)!);
            File.WriteAllText(CachedLogin, $"token-{_processes.Started.Count}");
            if (_processes.Started.Count == 1)
            {
                process.Print("ERROR! Failed to install app '232330' (Missing configuration)\r\n");
                process.Exit(8);
                return;
            }

            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job() with { Login = new SteamLogin(account, "servers", "hunter2-password", "F7K2Q") }, Report,
            TestContext.Current.CancellationToken);

        Assert.Equal([null, "token-1"], seen);
        Assert.Equal(["+login", "servers", "+app_update"], _processes.Specs[1].Arguments.SkipWhile(a => a != "+login").Take(3));
        Assert.Equal("token-2", Cached(account));
    }

    [Fact]
    public async Task OutputIsReadBeforeTheSecretsAreBlankedOut()
    {
        // A one-letter password and a five-digit code also appear in what SteamCMD prints for other reasons.
        _processes.OnStarted = (process, _) =>
        {
            process.Print(" Update state (0x61) downloading, progress: 10.00 (10000 / 100000)\r\nSuccess! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job() with { Login = new SteamLogin(Guid.NewGuid(), "servers", "e", "10000") }, Report, TestContext.Current.CancellationToken);

        Assert.Single(_processes.Started);
        Assert.Contains(("downloading", 10), _progress);
    }

    [Fact]
    public async Task AStopAtASteamGuardPromptAsksForTheCode()
    {
        var account = Guid.NewGuid();
        _processes.OnStarted = (process, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachedLogin)!);
            File.WriteAllText(CachedLogin, "half a login");
            // The prompt has no line end, and SteamCMD would wait for it forever.
            process.Print("This computer has not been authenticated for your account using Steam Guard.\r\nSteam Guard code:");
        };
        using var runner = Runner();

        var error = await Assert.ThrowsAsync<GameProcessException>(() =>
            runner.RunAsync(Job() with { Login = new SteamLogin(account, "servers", "hunter2-password") }, Report, TestContext.Current.CancellationToken));

        Assert.Equal("steamcmd.guard_required", error.Code);
        Assert.Equal(1, Assert.Single(_processes.Started).TerminatedWith);
        Assert.Null(Cached(account));
        Assert.False(File.Exists(CachedLogin));
    }

    [Fact]
    public async Task ThePasswordNeverReachesTheErrorOrTheLog()
    {
        _processes.OnStarted = (process, _) =>
        {
            process.Print("login servers hunter2-password\r\nLogin Failure: Invalid Password\r\n");
            process.Exit(5);
        };
        using var runner = Runner();

        var error = await Assert.ThrowsAsync<GameProcessException>(() =>
            runner.RunAsync(Job() with { Login = new SteamLogin(Guid.NewGuid(), "servers", "hunter2-password") }, Report, TestContext.Current.CancellationToken));

        Assert.Equal("steamcmd.login_failed", error.Code);
        Assert.Single(_processes.Started); // a refused password is not tried again
        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunsOfOneAccountWaitForEachOther()
    {
        var gate = new TaskCompletionSource();
        var login = new SteamLogin(Guid.NewGuid(), "servers", "hunter2-password");
        _processes.OnStarted = async (process, _) =>
        {
            if (_processes.Started.Count == 1)
            {
                await gate.Task;
            }

            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        var first = runner.RunAsync(Job() with { Login = login }, Report, TestContext.Current.CancellationToken);
        var second = runner.RunAsync(Job() with { ServerRoot = Path.Combine(_root, "other"), Login = login }, Report, TestContext.Current.CancellationToken);
        var anonymous = runner.RunAsync(Job() with { ServerRoot = Path.Combine(_root, "third") }, Report, TestContext.Current.CancellationToken);
        await anonymous; // another account (or none) is not held up

        Assert.Equal(2, _processes.Started.Count);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(3, _processes.Started.Count);
    }

    private string CachedLogin => Path.Combine(_root, "server", ".pulsar", "steamcmd", "config", "config.vdf");

    /// <summary>The login the agent keeps for the account, while its password is still hunter2-password.</summary>
    private string? Cached(Guid account) =>
        _logins.Load(new SteamLogin(account, "servers", "hunter2-password")) is { } login ? System.Text.Encoding.UTF8.GetString(login) : null;

    private SteamCmdRunner Runner(TimeProvider? clock = null) => new(_processes, _source, new DirectServerFiles(), new DirectServerFileManager(), _logins,
        new SteamCmdOptions { RetryDelays = clock is null ? [TimeSpan.Zero] : [TimeSpan.FromSeconds(10)] }, clock ?? TimeProvider.System,
        NullLogger<SteamCmdRunner>.Instance);

    private SteamCmdJob Job() => new(Guid.NewGuid(), Path.Combine(_root, "server"), ProcessIdentity.Account("pulsar_abcd2345"), 232330, null, null, false);

    private Task Report(string stage, double? percent)
    {
        lock (_progress)
        {
            _progress.Add((stage, percent));
        }

        return Task.CompletedTask;
    }

    private sealed class FakeSource(string path) : ISteamCmdSource
    {
        public int Calls { get; private set; }

        public Task<string> GetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            File.WriteAllText(path, "bootstrapper");
            return Task.FromResult(path);
        }
    }
}
