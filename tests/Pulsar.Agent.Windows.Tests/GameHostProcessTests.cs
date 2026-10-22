using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Processes;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// Games in real host processes (<c>PulsarAgent host</c>, docs/architecture/06, "Server hosts"): the test plays the
/// agent, whose connection ends without stopping the game, and comes back for it.
/// </summary>
public sealed class GameHostProcessTests : IAsyncDisposable
{
    private readonly TestServerDirectory _server = new();
    private readonly string _data = Directory.CreateTempSubdirectory("pulsar-host-data-").FullName;
    private readonly TestJob _job = new();
    private readonly WindowsGameHostLauncher _launcher;
    private readonly GameHostManager _hosts;
    private readonly List<GameHostReference> _started = [];

    public GameHostProcessTests()
    {
        _launcher = new WindowsGameHostLauncher(Path.Combine(AppContext.BaseDirectory, "agent", "PulsarAgent.exe"), _data, null,
            NullLogger<WindowsGameHostLauncher>.Instance, development: true);
        _hosts = Manager(_launcher);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var host in _started)
        {
            _launcher.Kill(host);
        }

        _job.Dispose();
        _server.Dispose();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A host's log file (or the program it ran from) closes a moment after it ends.
        }

        return ValueTask.CompletedTask;
    }

    [WindowsFact]
    public async Task AGameOutlivesTheAgentsConnectionAndTheNextAgentContinuesItsConsole()
    {
        var process = await StartAsync();
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");
        var gameId = process.ProcessId;

        await process.DetachAsync();
        await output.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(_launcher.IsAlive(process.Host));
        using (var game = Process.GetProcessById(gameId))
        {
            Assert.False(game.HasExited);
        }

        var again = await _hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        Assert.Equal(gameId, again.ProcessId);
        var resumed = new OutputCollector(again);
        await again.WriteInputAsync("say still here\r\n", TestContext.Current.CancellationToken);
        await resumed.WaitForAsync("still here");
        Assert.Contains("Test server ready", resumed.Text, StringComparison.Ordinal); // asked from 0: everything the host kept

        await again.WriteInputAsync("quit\r\n", TestContext.Current.CancellationToken);
        Assert.Equal(0, await again.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await again.DisposeAsync();
        await WaitUntilAsync(() => !_launcher.IsAlive(process.Host));
    }

    [WindowsFact]
    public async Task KillingTheHostKillsItsGameTree()
    {
        var process = await StartAsync();
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");
        await process.WriteInputAsync("spawn\r\n", TestContext.Current.CancellationToken);
        await output.WaitForAsync("grandchild");
        Assert.Equal(3, process.CountProcesses()); // the game, its child and its grandchild (the console host is not in the job)
        var gameId = process.ProcessId;

        Assert.True(_launcher.Kill(process.Host)); // gone when it returns
        Assert.True(_launcher.Kill(process.Host)); // already gone is gone

        Assert.Equal(HostedGameProcess.HostLostExitCode, await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => !ProcessRuns(gameId));
        Assert.Null(await _hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken));
    }

    [WindowsFact]
    public void AProcessThatCannotBeOpenedIsNotTakenForGone()
    {
        // The System process (id 4) cannot be opened without elevation: "access denied" says nothing about whether a host
        // runs, and only "no such process" means it does not.
        // Without a pipe of that name the host is gone (a stale record whose id a protected process has now); with one owned
        // by this account's default owner, as a host's is, it may be running. (The mapping itself is tested without
        // privileges above; this is the real thing where pid 4 is denied.)
        var pipeName = "pulsar-host-" + Guid.NewGuid().ToString("N")[..16];
        var system = new GameHostReference(pipeName, 4, DateTimeOffset.UtcNow);
        Assert.False(_launcher.IsAlive(system));
        Assert.True(_hosts.End(system));

        // Every look at the pipe's owner takes a free instance for a moment (a host would take the next connection after
        // it): enough of them, and nobody serving.
        var instances = Enumerable.Range(0, 16)
            .Select(_ => new NamedPipeServerStream(pipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)).ToList();
        try
        {
            var error = Record.Exception(() => _launcher.IsAlive(system));
            Assert.SkipWhen(error is null, "Elevated: the System process can be opened, so there is nothing that cannot be checked.");

            Assert.Equal(GameHostManager.HostUnverifiable, Assert.IsType<GameProcessException>(error).Code);
            Assert.False(_launcher.Kill(system)); // not ended, and the caller keeps its record
            Assert.Null(WindowsGameHostLauncher.ProgramOf(system));
            Assert.False(_hosts.End(system));
        }
        finally
        {
            instances.ForEach(instance => instance.Dispose());
        }

        // A process id nothing uses is simply gone.
        var gone = new GameHostReference("pulsar-host-none", int.MaxValue - 3, DateTimeOffset.UtcNow);
        Assert.False(_launcher.IsAlive(gone));
        Assert.True(_launcher.Kill(gone));
        Assert.True(_hosts.End(gone));
    }

    [WindowsTheory]
    [InlineData(87, false, true)] // ERROR_INVALID_PARAMETER: no such process
    [InlineData(87, true, true)] // ... whatever the pipe says
    [InlineData(5, false, true)] // access denied, and no pipe that may be the host's: the host is gone (a stale record, a reused id)
    [InlineData(5, true, false)] // access denied, and a pipe that may be the host's: it may well be running
    [InlineData(31, false, true)] // any other failure is judged the same way
    [InlineData(31, true, false)]
    public void WhatAFailedOpenMeansDependsOnTheErrorAndTheHostsPipe(int error, bool hostsPipe, bool gone)
    {
        // The mapping on its own, so it is checked whatever rights the tests run with (pid 4 opens when elevated).
        var host = new GameHostReference("pulsar-host-0123456789abcdef", 4, DateTimeOffset.UtcNow);

        if (gone)
        {
            Assert.True(WindowsGameHostLauncher.IsGoneAfterFailedOpen(host, error, _ => hostsPipe));
        }
        else
        {
            var unverifiable = Assert.Throws<GameProcessException>(() => WindowsGameHostLauncher.IsGoneAfterFailedOpen(host, error, _ => hostsPipe));
            Assert.Equal(GameHostManager.HostUnverifiable, unverifiable.Code);
        }
    }

    // Any account can create a pipe under a host's name (the names can be listed), and keep its instances busy: only one
    // owned by the agent's default owner, which no game server's account can give a pipe, or the host's id and start time
    // in the process list, keep a host that cannot be opened from counting as gone.
    [WindowsTheory]
    [InlineData(nameof(WindowsGameHostLauncher.PipeProbe.Missing), false, null, false)]
    [InlineData(nameof(WindowsGameHostLauncher.PipeProbe.Refused), false, null, false)] // a host's pipe always lets the agent's account in
    [InlineData(nameof(WindowsGameHostLauncher.PipeProbe.Owned), true, null, true)]
    [InlineData(nameof(WindowsGameHostLauncher.PipeProbe.Owned), false, null, false)] // a game server's account made it
    [InlineData(nameof(WindowsGameHostLauncher.PipeProbe.Unknown), false, false, false)] // every instance busy: the process list decides
    [InlineData(nameof(WindowsGameHostLauncher.PipeProbe.Unknown), false, true, true)]
    [InlineData(nameof(WindowsGameHostLauncher.PipeProbe.Unknown), false, null, true)] // nothing can tell: it may be running
    public void OnlyThePipesOwnerOrTheProcessListKeepAHostThatCannotBeOpened(string found, bool ownedByAgent, bool? listed, bool mayRun)
    {
        var probe = Enum.Parse<WindowsGameHostLauncher.PipeProbe>(found);
        var agent = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var other = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        SecurityIdentifier? owner = probe == WindowsGameHostLauncher.PipeProbe.Owned ? ownedByAgent ? agent : other : null;
        var host = new GameHostReference("pulsar-host-0123456789abcdef", 4, DateTimeOffset.UtcNow);
        bool MayRun(GameHostReference h) => WindowsGameHostLauncher.MayBeRunning(h, _ => (probe, owner), agent, _ => listed);

        Assert.Equal(mayRun, MayRun(host));
        if (mayRun)
        {
            Assert.Equal(GameHostManager.HostUnverifiable,
                Assert.Throws<GameProcessException>(() => WindowsGameHostLauncher.IsGoneAfterFailedOpen(host, 5, MayRun)).Code);
        }
        else
        {
            Assert.True(WindowsGameHostLauncher.IsGoneAfterFailedOpen(host, 5, MayRun));
        }
    }

    [WindowsFact]
    public async Task APipesOwnerIsReadWithoutItsServersHelp()
    {
        var name = "pulsar-host-" + Guid.NewGuid().ToString("N")[..16];
        // Not this process (a reused id): the process list says so when the pipe cannot.
        var host = new GameHostReference(name, Environment.ProcessId, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Equal((WindowsGameHostLauncher.PipeProbe.Missing, null), WindowsGameHostLauncher.ProbePipe(name));
        Assert.False(WindowsGameHostLauncher.MayBeRunning(host));

        using var identity = WindowsIdentity.GetCurrent();
        await using (var pipe = GameHostPipes.CreateServer(name))
        {
            // As a host makes it, waiting for the agent: owned by the account's default owner. A look connects for a
            // moment and leaves, as a client the host serves and lets go.
            async Task<T> LookAsync<T>(Func<T> look)
            {
                var looked = pipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
                var result = look();
                await looked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                pipe.Disconnect();
                return result;
            }

            Assert.Equal((WindowsGameHostLauncher.PipeProbe.Owned, identity.Owner), await LookAsync(() => WindowsGameHostLauncher.ProbePipe(name)));
            Assert.True(await LookAsync(() => WindowsGameHostLauncher.MayBeRunning(host)));

            // Its only instance taken: the owner cannot be read, and the process list decides.
            var waiting = pipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(10_000, TestContext.Current.CancellationToken);
            await waiting;
            Assert.Equal(WindowsGameHostLauncher.PipeProbe.Unknown, WindowsGameHostLauncher.ProbePipe(name).Probe);
            Assert.False(WindowsGameHostLauncher.MayBeRunning(host));
        }

        // One that does not let this account read it (not even as its owner): never a host's.
        var refusing = new PipeSecurity();
        refusing.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.Synchronize, AccessControlType.Allow));
        refusing.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-3-4"), PipeAccessRights.Synchronize, AccessControlType.Allow)); // OWNER RIGHTS
        await using (var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, refusing))
        {
            Assert.Equal((WindowsGameHostLauncher.PipeProbe.Refused, null), WindowsGameHostLauncher.ProbePipe(name));
            Assert.False(WindowsGameHostLauncher.MayBeRunning(host));
        }
    }

    [WindowsFact]
    public async Task AHostIsFoundInTheProcessListByItsIdAndStartTime()
    {
        var process = await StartAsync();

        Assert.True(WindowsGameHostLauncher.IsListed(process.Host));
        Assert.False(WindowsGameHostLauncher.IsListed(process.Host with { ProcessStartedAt = process.Host.ProcessStartedAt.AddTicks(1) })); // a reused id
        Assert.False(WindowsGameHostLauncher.IsListed(process.Host with { ProcessId = int.MaxValue - 3 }));

        await process.DisposeAsync();
    }

    [WindowsFact]
    public async Task APipeIsTrustedOnlyFromItsOwnHostProcess()
    {
        var process = await StartAsync();
        var impostor = process.Host with { ProcessId = Environment.ProcessId };
        await process.DetachAsync();

        await using var pipe = GameHostPipes.CreateClient(process.Host.Pipe);
        await pipe.ConnectAsync(30_000, TestContext.Current.CancellationToken);
        Assert.True(_launcher.Serves(pipe, process.Host));
        Assert.False(_launcher.Serves(pipe, impostor));
        // A reused process id (the right id, another start time), and another process started at the host's time.
        var reused = process.Host with { ProcessStartedAt = process.Host.ProcessStartedAt.AddSeconds(-1) };
        Assert.False(_launcher.IsAlive(reused));
        Assert.False(_launcher.Serves(pipe, reused));
        Assert.False(_launcher.IsAlive(impostor));
        Assert.Null(WindowsGameHostLauncher.ProgramOf(reused));

        // As the pipe is created: open to the host's own account only (not, say, to the Administrators group that
        // owns what SYSTEM creates), and owned by the account's default owner, which the agent's end checks.
        using var identity = WindowsIdentity.GetCurrent();
        var security = pipe.GetAccessControl();
        var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>());
        Assert.Equal((identity.User, AccessControlType.Allow), ((SecurityIdentifier)rule.IdentityReference, rule.AccessControlType));
        Assert.Equal(identity.Owner, security.GetOwner(typeof(SecurityIdentifier)));
    }

    [WindowsFact]
    public async Task OnlyItsCreatorMayTouchAHostAndAdministratorsMayOnlyEndIt()
    {
        var process = await StartAsync();
        using var host = Process.GetProcessById(process.Host.ProcessId);

        var acl = new RawSecurityDescriptor(new KernelObjectSecurity(host.SafeHandle).GetSecurityDescriptorSddlForm(AccessControlSections.Access))
            .DiscretionaryAcl!;

        using var identity = WindowsIdentity.GetCurrent();
        const int All = 0x001FFFFF; // PROCESS_ALL_ACCESS
        const int EndAndWait = 0x00100000 | 0x1000 | 0x1; // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE
        var entries = acl.Cast<CommonAce>().Select(a => (a.SecurityIdentifier.Value, a.AceType, a.AccessMask)).ToList();
        // Nobody else: no Users, no Everyone, not the game's account. Run as SYSTEM (the CI step), its creator is SYSTEM.
        Assert.Equal(identity.IsSystem ? 2 : 3, entries.Count);
        Assert.Contains((new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, AceType.AccessAllowed, All), entries);
        Assert.Contains((identity.User!.Value, AceType.AccessAllowed, All), entries);
        Assert.Contains((new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value, AceType.AccessAllowed, EndAndWait), entries);

        _launcher.Kill(process.Host); // its creator ends it, and waits for it
        Assert.True(host.HasExited);
    }

    [WindowsFact]
    public async Task HostsRunFromACopyOfTheProgramThatStaysWhileOneRunsFromIt()
    {
        var copies = Path.Combine(_data, "host-programs");
        var program = Path.Combine(AppContext.BaseDirectory, "agent");
        var launcher = new WindowsGameHostLauncher(() => GameHostProgram.Prepare(program, "PulsarAgent.exe", copies), _data, copies,
            NullLogger<WindowsGameHostLauncher>.Instance, development: true);
        var process = await StartAsync(Manager(launcher));
        Assert.StartsWith(copies, launcher.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(launcher.Executable, WindowsGameHostLauncher.ProgramOf(process.Host), StringComparer.OrdinalIgnoreCase);

        // An older version nobody runs from goes; the one a host runs from stays.
        var old = Directory.CreateDirectory(Path.Combine(copies, "0123456789abcdef")).FullName;
        File.WriteAllText(Path.Combine(old, "PulsarAgent.exe"), "an older agent");
        launcher.CleanUpPrograms([process.Host]);
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(launcher.Executable));

        // A copy a host still runs from stays even when that host is not recognized (its path spelled another way, say).
        var other = new WindowsGameHostLauncher(Path.Combine(copies, "fedcba9876543210", "PulsarAgent.exe"), _data, copies,
            NullLogger<WindowsGameHostLauncher>.Instance, development: true);
        other.CleanUpPrograms([]);
        Assert.True(File.Exists(launcher.Executable));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(launcher.Executable)!, "PulsarAgent.dll")));

        await process.DisposeAsync();
        await WaitUntilAsync(() => !launcher.IsAlive(process.Host));
    }

    [WindowsFact]
    public async Task StartsFailWhileTheProgramCannotBeCopiedAndWorkOnceItCan()
    {
        // A full disk when the agent starts must not keep it from starting (and from managing the games that run).
        var full = true;
        var launcher = new WindowsGameHostLauncher(
            () => full ? throw new IOException("There is not enough space on the disk.") : Path.Combine(AppContext.BaseDirectory, "agent", "PulsarAgent.exe"),
            _data, Path.Combine(_data, "host-programs"), NullLogger<WindowsGameHostLauncher>.Instance, development: true);
        var hosts = Manager(launcher);

        launcher.CleanUpPrograms([]);
        var error = await Assert.ThrowsAsync<GameProcessException>(() => StartAsync(hosts));
        Assert.Equal("host.start_failed", error.Code);
        Assert.Contains("not enough space", error.Message, StringComparison.Ordinal);

        full = false;
        var process = await StartAsync(hosts);
        await process.DisposeAsync();
        await WaitUntilAsync(() => !launcher.IsAlive(process.Host));
    }

    private Task<IHostedGameProcess> StartAsync() => StartAsync(_hosts);

    private async Task<IHostedGameProcess> StartAsync(GameHostManager hosts)
    {
        var process = (IHostedGameProcess)await hosts.StartAsync(_server.Spec(ConsoleMode.Pty, "--ready-after", "100"), TestContext.Current.CancellationToken);
        _started.Add(process.Host);
        return process;
    }

    private GameHostManager Manager(WindowsGameHostLauncher launcher) =>
        new(TestServerDirectory.Manager(), new JobbedHostLauncher(launcher, _job), NullLogger<GameHostManager>.Instance);

    /// <summary>A process's security, read through a handle (only the DACL).</summary>
    private sealed class KernelObjectSecurity(SafeHandle handle)
        : NativeObjectSecurity(false, ResourceType.KernelObject, handle, AccessControlSections.Access)
    {
        public override Type AccessRightType => typeof(int);

        public override Type AccessRuleType => typeof(AccessRule);

        public override Type AuditRuleType => typeof(AuditRule);

        public override AccessRule AccessRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type) => throw new NotSupportedException();

        public override AuditRule AuditRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags) => throw new NotSupportedException();
    }

    private static bool ProcessRuns(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
