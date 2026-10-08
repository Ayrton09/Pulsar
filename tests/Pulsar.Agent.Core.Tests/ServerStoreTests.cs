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
}
