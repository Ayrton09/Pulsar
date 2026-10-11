using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

public sealed class ServerStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ServerRecordsAndPendingResultsSurviveARestartOfTheAgent()
    {
        var store = new ServerStore(new AgentPaths(_root), new PlainStateProtector());
        var spec = new ServerRuntimeSpec("abcd2345", "{}", new Dictionary<string, string?> { ["map"] = "de_dust2" }, ["-condebug"], AutoStart: true,
            RestartOnCrash: false);
        var record = new ServerRecord(Guid.NewGuid(), spec, DesiredRunning: true, ResumeOnStart: false);
        var result = new OperationCompleted(Guid.NewGuid(), record.ServerId, OperationOutcome.Failed, "install.failed", "disk full");

        store.Save(record);
        store.Save(record with { ResumeOnStart = true });
        store.SaveResult(result);
        var reopened = new ServerStore(new AgentPaths(_root), new PlainStateProtector());

        var loaded = Assert.Single(reopened.LoadAll());
        Assert.True(loaded.ResumeOnStart);
        Assert.Equal("de_dust2", loaded.Spec.Variables["map"]);
        Assert.Equal(["-condebug"], loaded.Spec.ExtraArguments);
        Assert.Equal(result, Assert.Single(reopened.PendingResults()));

        reopened.Delete(record.ServerId);
        reopened.DeleteResult(result.OperationId);
        Assert.Empty(reopened.LoadAll());
        Assert.Empty(reopened.PendingResults());
        Assert.Null(reopened.Load(record.ServerId));
    }

    [Fact]
    public void AnUnreadableRecordIsSetAsideAndTheOthersStillLoad()
    {
        // Corrupt JSON, or a state folder copied to another machine (DPAPI cannot unprotect it there).
        var store = new ServerStore(new AgentPaths(_root), new PlainStateProtector());
        var spec = new ServerRuntimeSpec("abcd2345", "{}", new Dictionary<string, string?>(), [], AutoStart: false, RestartOnCrash: false);
        var good = new ServerRecord(Guid.NewGuid(), spec, DesiredRunning: false, ResumeOnStart: false);
        store.Save(good);
        var corrupt = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_root, "state", "servers", $"{corrupt:N}.json"), "{ not json");
        var foreign = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_root, "state", "servers", $"{foreign:N}.json"), """{ "protection": "dpapi-elsewhere", "data": "AAAA" }""");

        var loaded = Assert.Single(store.LoadAll());

        Assert.Equal(good.ServerId, loaded.ServerId);
        Assert.True(File.Exists(Path.Combine(_root, "state", "servers", $"{corrupt:N}.json.unreadable")));
        Assert.True(File.Exists(Path.Combine(_root, "state", "servers", $"{foreign:N}.json.unreadable")));
        Assert.Null(store.Load(corrupt)); // set aside: provisioning that server again starts afresh
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void AnUnreadableResultIsSetAsideAndTheOthersAreStillPending()
    {
        var store = new ServerStore(new AgentPaths(_root), new PlainStateProtector());
        var result = new OperationCompleted(Guid.NewGuid(), Guid.NewGuid(), OperationOutcome.Succeeded, null, null);
        store.SaveResult(result);
        var corrupt = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_root, "state", "outbox", $"{corrupt:N}.json"), "{ \"operationId\": ");

        Assert.Equal(result, Assert.Single(store.PendingResults()));
        Assert.True(File.Exists(Path.Combine(_root, "state", "outbox", $"{corrupt:N}.json.unreadable")));
        Assert.Equal(result, Assert.Single(store.PendingResults()));
    }

    [Fact]
    public async Task ResultsAreReadWhileTheyAreWrittenAndDeleted()
    {
        // An operation saves its result while the results are delivered (read, then deleted): neither side may fail.
        var store = new ServerStore(new AgentPaths(_root), new PlainStateProtector());
        var kept = new OperationCompleted(Guid.NewGuid(), Guid.NewGuid(), OperationOutcome.Succeeded, null, null);
        store.SaveResult(kept);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var passing = new OperationCompleted(Guid.NewGuid(), Guid.NewGuid(), OperationOutcome.Failed, "x", "y");
                store.SaveResult(passing);
                store.SaveResult(kept); // replaced while it may be read
                store.DeleteResult(passing.OperationId);
            }
        }, TestContext.Current.CancellationToken);

        while (!stop.IsCancellationRequested)
        {
            Assert.Contains(kept, store.PendingResults());
        }

        await writer;
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "state", "outbox"), "*.unreadable"));
    }

    [Fact]
    public void ARecordHeldOpenByAnotherProgramIsSkippedNotSetAside()
    {
        // A scanner that opened it without sharing: the agent must still start, and the record is read next time.
        var store = new ServerStore(new AgentPaths(_root), new PlainStateProtector());
        var spec = new ServerRuntimeSpec("abcd2345", "{}", new Dictionary<string, string?>(), [], AutoStart: false, RestartOnCrash: false);
        var good = new ServerRecord(Guid.NewGuid(), spec, DesiredRunning: false, ResumeOnStart: false);
        var held = new ServerRecord(Guid.NewGuid(), spec with { ShortId = "abcd2346" }, DesiredRunning: false, ResumeOnStart: false);
        store.Save(good);
        store.Save(held);
        var file = Path.Combine(_root, "state", "servers", $"{held.ServerId:N}.json");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(good.ServerId, Assert.Single(store.LoadAll()).ServerId);
        }

        Assert.False(File.Exists(file + ".unreadable"));
        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void TemporaryFilesAWriteLeftBehindAreDeletedWhenTheStateIsLoaded()
    {
        // The agent stopped between creating a record's temporary file and moving it over the record. One being written
        // right now (a few seconds old) is left alone.
        var paths = new AgentPaths(_root);
        var store = new ServerStore(paths, new PlainStateProtector());
        string[] directories = [paths.ServersStateDirectory, paths.HostsStateDirectory, paths.PausedSavesDirectory, paths.OutboxDirectory];
        var orphans = new List<string>();
        var fresh = new List<string>();
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
            var orphan = Path.Combine(directory, $"{Guid.NewGuid():N}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(orphan, "{");
            File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));
            orphans.Add(orphan);
            var writing = Path.Combine(directory, $"{Guid.NewGuid():N}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(writing, "{");
            fresh.Add(writing);
        }

        Assert.Empty(store.LoadAll());
        Assert.Empty(store.LoadHosts());
        Assert.Empty(store.LoadPausedSaves());
        Assert.Empty(store.PendingResults());

        Assert.All(orphans, orphan => Assert.False(File.Exists(orphan), orphan));
        Assert.All(fresh, writing => Assert.True(File.Exists(writing), writing));
    }

    [Fact]
    public async Task OneRecordWrittenFromTwoPlacesAtOnceIsNeverLeftHalfWritten()
    {
        var store = new ServerStore(new AgentPaths(_root), new PlainStateProtector());
        var spec = new ServerRuntimeSpec("abcd2345", "{}", new Dictionary<string, string?>(), [], AutoStart: false, RestartOnCrash: false);
        var record = new ServerRecord(Guid.NewGuid(), spec, DesiredRunning: false, ResumeOnStart: false);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 50; n++)
            {
                store.Save(record with { DesiredRunning = (n + i) % 2 == 0 });
            }
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(record.ServerId, Assert.Single(store.LoadAll()).ServerId);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "state", "servers"), "*.tmp"));
    }
}
