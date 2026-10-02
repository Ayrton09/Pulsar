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
        var store = new ServerStore(new AgentPaths(_root));
        var spec = new ServerRuntimeSpec("abcd2345", "{}", new Dictionary<string, string?> { ["map"] = "de_dust2" }, ["-condebug"], AutoStart: true,
            RestartOnCrash: false);
        var record = new ServerRecord(Guid.NewGuid(), spec, DesiredRunning: true, ResumeOnStart: false);
        var result = new OperationCompleted(Guid.NewGuid(), record.ServerId, OperationOutcome.Failed, "install.failed", "disk full");

        store.Save(record);
        store.Save(record with { ResumeOnStart = true });
        store.SaveResult(result);
        var reopened = new ServerStore(new AgentPaths(_root));

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
}
