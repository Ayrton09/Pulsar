using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>Games a backup paused before the agent stopped (<see cref="BackupSource.ResumePausedAsync"/>).</summary>
public sealed class BackupSourceTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-backup-source-tests", Guid.NewGuid().ToString("N"));
    private readonly ServerStore _store;
    private readonly List<ServerSupervisor> _supervisors = [];

    public BackupSourceTests() => _store = new ServerStore(new AgentPaths(_root), new PlainStateProtector());

    public async ValueTask DisposeAsync()
    {
        foreach (var supervisor in _supervisors)
        {
            await supervisor.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AGameThatCannotBeToldToResumeIsTriedAgainByTheNextRunAndTheOthersAreResumed()
    {
        var (broken, brokenGame) = await RunningAsync();
        brokenGame.InputError = new IOException("The console's pipe broke.");
        var (refusing, refusingGame) = await RunningAsync();
        refusingGame.InputError = new InvalidOperationException("The host does not answer."); // what a hosted game says then
        var (working, workingGame) = await RunningAsync();
        var supervisors = new[] { broken, refusing, working }.ToDictionary(s => s.ServerId);
        foreach (var supervisor in supervisors.Keys)
        {
            _store.SavePausedSave(new PausedSave(supervisor, ["save-on"]));
        }

        await BackupSource.ResumePausedAsync(_store, id => supervisors.GetValueOrDefault(id), NullLogger.Instance);

        Assert.Equal(["save-on\r\n"], workingGame.Input);
        Assert.Equal(new[] { broken.ServerId, refusing.ServerId }.Order(), _store.LoadPausedSaves().Select(p => p.ServerId).Order());

        brokenGame.InputError = null;
        refusingGame.InputError = null;
        await BackupSource.ResumePausedAsync(_store, id => supervisors.GetValueOrDefault(id), NullLogger.Instance);

        Assert.Equal(["save-on\r\n"], brokenGame.Input);
        Assert.Equal(["save-on\r\n"], refusingGame.Input);
        Assert.Empty(_store.LoadPausedSaves());
    }

    [Fact]
    public async Task AGameNoLongerRunningIsForgottenItSavesByItself()
    {
        var (stopped, game) = await RunningAsync();
        await stopped.KillAsync();
        await stopped.WaitUntilSettledAsync();
        _store.SavePausedSave(new PausedSave(stopped.ServerId, ["save-on"]));

        await BackupSource.ResumePausedAsync(_store, _ => stopped, NullLogger.Instance);

        Assert.Empty(game.Input);
        Assert.Empty(_store.LoadPausedSaves());
    }

    private async Task<(ServerSupervisor Supervisor, FakeGameProcess Game)> RunningAsync()
    {
        var processes = new FakeProcessManager();
        var events = new RecordingEvents();
        var plan = new LaunchPlan("bin/game.exe", "", [], new Dictionary<string, string>(),
            new ConsoleDefinition(ManifestConsoleMode.Pty, InputNewline: "\r\n"),
            new LogPatternReadiness("^ready", 60),
            new StopDefinition([new ConsoleCommandStop("quit")], 15));
        var definition = new ServerDefinition(Guid.NewGuid(), "abcd1234", Path.Combine(_root, "server"), ProcessIdentity.CurrentUser, plan,
            AutoStart: false, RestartOnCrash: false);
        var supervisor = new ServerSupervisor(definition, processes, events, TimeProvider.System, new SupervisorOptions(), NullLogger.Instance);
        _supervisors.Add(supervisor);
        await supervisor.StartAsync();
        await supervisor.FlushAsync();
        processes.Last.Print("ready\n");
        await events.WaitForAsync(RuntimeState.Running);
        return (supervisor, processes.Last);
    }
}
