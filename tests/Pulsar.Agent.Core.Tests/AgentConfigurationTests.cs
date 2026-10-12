using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

public sealed class AgentConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-agent-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AMissingConfigurationMeansNotEnrolled()
    {
        Assert.Null(new AgentConfigStore(new AgentPaths(_root)).Load());
    }

    [Fact]
    public void TheConfigurationRoundTrips()
    {
        var store = new AgentConfigStore(new AgentPaths(_root));
        var config = new AgentConfig(new Uri("https://panel.example.com/"), Guid.NewGuid(), Guid.NewGuid(),
            new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Tpm),
            [new PanelKey("sha256:abc", [1, 2, 3])], @"D:\PulsarServers", DateTimeOffset.UnixEpoch, Development: false);

        store.Save(config);
        store.Save(config with { ServersRoot = @"E:\Servers" });
        var loaded = store.Load()!;

        Assert.Equal(@"E:\Servers", loaded.ServersRoot);
        Assert.Equal(config.Key, loaded.Key);
        Assert.Equal(config.PanelKeys[0].PublicKey, loaded.PanelKeys[0].PublicKey);
        Assert.Contains("\"scope\": \"machine\"", File.ReadAllText(store.Paths.ConfigFile), StringComparison.Ordinal);
        Assert.False(File.Exists(store.Paths.ConfigFile + ".tmp"));
    }

    [Fact]
    public void TheFallbackAddressRoundTripsAndOlderConfigurationsHaveNone()
    {
        var store = new AgentConfigStore(new AgentPaths(_root));
        var config = new AgentConfig(new Uri("https://panel.example.com:8444/"), Guid.NewGuid(), Guid.NewGuid(),
            new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Tpm), [], null, DateTimeOffset.UnixEpoch, Development: false);

        store.Save(config);
        Assert.Null(store.Load()!.FallbackPanel);
        store.Save(config with { FallbackPanel = new Uri("https://panel.example.com:8443/") });

        Assert.Equal(new Uri("https://panel.example.com:8443/"), store.Load()!.FallbackPanel);
        Assert.Contains("\"fallbackPanel\": \"https://panel.example.com:8443/\"", File.ReadAllText(store.Paths.ConfigFile), StringComparison.Ordinal);
    }

    [Fact]
    public void MovingKeepsTheAddressLeftAsFallbackButNeverTheNewOne()
    {
        var main = new Uri("https://panel.example.com:8443/");
        var agents = new Uri("https://panel.example.com:8444/");
        var config = new AgentConfig(main, Guid.NewGuid(), Guid.NewGuid(),
            new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Tpm), [], null, DateTimeOffset.UnixEpoch, Development: false);

        var moved = config.MovedTo(agents);
        Assert.Equal((agents, main), (moved.Panel, moved.FallbackPanel));
        // Moved again: the first address stays the fallback.
        Assert.Equal(main, moved.MovedTo(new Uri("https://agents.example.com:9444/")).FallbackPanel);
        // Back where the fallback points: one address, no fallback to try in a loop.
        Assert.Equal((main, (Uri?)null), (moved.MovedTo(main).Panel, moved.MovedTo(main).FallbackPanel));
    }

    [Fact]
    public void AConfigurationWrittenBeforeKeyRotationStillLoads()
    {
        // As written by the first Phase 2 agents: no keyCreatedAt, no pendingRotation.
        var store = new AgentConfigStore(new AgentPaths(_root));
        Directory.CreateDirectory(store.Paths.ConfigDirectory);
        File.WriteAllText(store.Paths.ConfigFile, """
            {
              "panel": "https://panel.example.com/",
              "nodeId": "01a0f9d9-0c2c-7e69-9a34-f215e254f039",
              "credentialId": "01a0f9d9-2630-7913-9eec-dbea1640659d",
              "key": { "name": "PulsarAgent-1", "scope": "machine", "storage": "tpm" },
              "panelKeys": [ { "keyId": "sha256:abc", "publicKey": "AQID" } ],
              "serversRoot": null,
              "enrolledAt": "2026-10-01T23:42:33+00:00",
              "development": false
            }
            """);

        var config = store.Load()!;

        Assert.Null(config.KeyCreatedAt);
        Assert.Null(config.PendingRotation);
        Assert.Equal(config.EnrolledAt, config.CurrentKeyCreatedAt);
        Assert.Null(config.BackupsRoot);
        Assert.Equal(Path.Combine(store.Paths.Root, "backups"), config.BackupsRootOrDefault(store.Paths));
    }

    [Fact]
    public void BackupsGoWhereTheyWereToldAtEnrollment()
    {
        var paths = new AgentPaths(_root);
        var config = new AgentConfig(new Uri("https://panel.example.com/"), Guid.NewGuid(), Guid.NewGuid(),
            new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Tpm), [], null, DateTimeOffset.UnixEpoch, Development: false,
            BackupsRoot: @"E:\PulsarBackups");

        Assert.Equal(@"E:\PulsarBackups", config.BackupsRootOrDefault(paths));
        Assert.Equal(paths.BackupsDirectory, (config with { BackupsRoot = null }).BackupsRootOrDefault(paths));
    }

    [Theory]
    [InlineData("https://panel.example.com", "https://panel.example.com")]
    [InlineData("HTTPS://Panel.Example.com:8443/", "https://panel.example.com:8443")]
    [InlineData("https://panel.example.com:443", "https://panel.example.com")]
    [InlineData("http://localhost:5080", "http://localhost:5080")]
    [InlineData("http://127.0.0.1:5080", "http://127.0.0.1:5080")]
    public void PanelUrlsAreNormalizedToTheirOrigin(string value, string origin)
    {
        var panel = PanelAddress.Parse(value);

        Assert.Equal(origin, PanelAddress.Origin(panel));
        Assert.Equal(origin + "/", panel.ToString());
    }

    [Theory]
    [InlineData("http://panel.example.com")]
    [InlineData("ftp://panel.example.com")]
    [InlineData("panel.example.com")]
    [InlineData("https://panel.example.com/pulsar")]
    [InlineData("https://panel.example.com/?x=1")]
    [InlineData("https://user:pass@panel.example.com")]
    public void UnsafeOrAmbiguousPanelUrlsAreRejected(string value)
    {
        Assert.Throws<AgentException>(() => PanelAddress.Parse(value));
    }

    [Fact]
    public void BackoffGrowsExponentiallyUpToTheMaximumWithJitter()
    {
        var max = TimeSpan.FromSeconds(60);
        for (var i = 0; i < 50; i++)
        {
            var first = AgentConnection.Backoff(1, max);
            Assert.InRange(first, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1));
            var fourth = AgentConnection.Backoff(4, max);
            Assert.InRange(fourth, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
            var capped = AgentConnection.Backoff(30, max);
            Assert.InRange(capped, TimeSpan.FromSeconds(30), max);
        }
    }

    [Fact]
    public void ASessionClosedRightAfterItsHelloCountsAsAFailure()
    {
        // Two machines with one node identity (a cloned VM) close each other's session at every Hello: the backoff grows.
        var failures = 0;
        for (var i = 0; i < 5; i++)
        {
            failures = AgentConnection.FailuresAfterSession(failures, TimeSpan.FromSeconds(2));
        }

        Assert.Equal(5, failures);
        Assert.Equal(30, AgentConnection.FailuresAfterSession(30, TimeSpan.FromSeconds(10)));

        // A session that lasted is a working connection that was lost: retry soon.
        Assert.Equal(1, AgentConnection.FailuresAfterSession(30, TimeSpan.FromMinutes(5)));
        Assert.Equal(1, AgentConnection.FailuresAfterSession(0, TimeSpan.FromMinutes(1)));
    }
}
