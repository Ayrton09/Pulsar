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
}
