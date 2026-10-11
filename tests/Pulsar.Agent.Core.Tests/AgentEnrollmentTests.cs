using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// Enrollment against a panel that limits enrollments per address (docs/architecture/03, "Bulk enrollment"): a rollout
/// behind one NAT address waits as the panel says, for a while, and never retries a refused token.
/// </summary>
public sealed class AgentEnrollmentTests : IDisposable
{
    private const string Panel = "https://panel.example.com/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-enrollment-tests", Guid.NewGuid().ToString("N"));
    private readonly ECDsa _panelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly MemoryKeys _keys = new();
    private readonly PlainFolders _folders = new();

    private PanelKey PanelKey => new(ProtocolCrypto.Fingerprint(_panelKey.ExportSubjectPublicKeyInfo()), _panelKey.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        _panelKey.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ThePanelSayingTooManyAttemptsIsWaitedOut()
    {
        var panel = new FakePanel(this, busyAnswers: 3, retryAfter: TimeSpan.FromSeconds(40));
        List<TimeSpan> waits = [];
        var enrollment = Enrollment(panel);
        enrollment.Waiting = waits.Add;

        var config = await RunAsync(enrollment.EnrollAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(panel.NodeId, config.NodeId);
        Assert.Equal(4, panel.Enrollments);
        Assert.Equal(3, waits.Count);
        Assert.All(waits, w => Assert.InRange(w, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(50)));
        Assert.Single(_keys.Names); // one key for every attempt
    }

    [Fact]
    public async Task ItGivesUpAfterTenMinutesAndKeepsNoKey()
    {
        var panel = new FakePanel(this, busyAnswers: int.MaxValue, retryAfter: TimeSpan.FromSeconds(60));
        var enrollment = Enrollment(panel);

        var failed = await Assert.ThrowsAsync<AgentException>(() => RunAsync(enrollment.EnrollAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("EnrollPermitLimit", failed.Message, StringComparison.Ordinal);
        Assert.InRange(panel.Enrollments, 8, 11);
        Assert.Empty(_keys.Names);
    }

    [Fact]
    public async Task ARefusedTokenIsNotTriedAgain()
    {
        var panel = new FakePanel(this, busyAnswers: 0, retryAfter: null) { Refuse = true };

        await Assert.ThrowsAsync<AgentException>(() => RunAsync(Enrollment(panel).EnrollAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Equal(1, panel.Enrollments);
    }

    [Fact]
    public async Task TheServersRootIsSecuredBeforeAnythingIsSent()
    {
        var chosen = Path.Combine(_root, "games");
        var panel = new FakePanel(this, busyAnswers: 0, retryAfter: null);

        await RunAsync(Enrollment(panel).EnrollAsync(Request() with { ServersRoot = chosen }, TestContext.Current.CancellationToken));
        await RunAsync(Enrollment(panel).EnrollAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal([chosen, Path.Combine(_root, "servers")], _folders.ServersRoots);
    }

    [Fact]
    public async Task AServersRootThatCannotBeSecuredStopsTheEnrollment()
    {
        var panel = new FakePanel(this, busyAnswers: 0, retryAfter: null);
        _folders.Refuse = true;

        await Assert.ThrowsAsync<AgentException>(() => RunAsync(Enrollment(panel).EnrollAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Equal(0, panel.Enrollments);
        Assert.Empty(_keys.Names);
    }

    [Fact]
    public async Task ADevelopmentEnrollmentNeverReplacesAProductionOne()
    {
        // enroll --dev without --data-dir targets the default state directory, where the service's enrollment lives.
        var configs = new AgentConfigStore(new AgentPaths(_root));
        var production = new AgentConfig(new Uri(Panel), Guid.NewGuid(), Guid.NewGuid(), new NodeKeyReference("PulsarAgent-production", NodeKeyScope.Machine,
            NodeKeyStorage.Software), [PanelKey], null, DateTimeOffset.UtcNow, Development: false);
        configs.Save(production);
        var panel = new FakePanel(this, busyAnswers: 0, retryAfter: null);

        var refused = await Assert.ThrowsAsync<AgentException>(() => RunAsync(Enrollment(panel).EnrollAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("--data-dir", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, panel.Enrollments);
        Assert.Empty(_keys.Names);
        Assert.Empty(_folders.ServersRoots);
        Assert.Equal(production.NodeId, configs.Load()!.NodeId);

        // A production enrollment may replace another one (re-enrollment), and a development one a development one.
        var enrolled = await RunAsync(Enrollment(panel).EnrollAsync(Request() with { Development = false }, TestContext.Current.CancellationToken));
        Assert.False(enrolled.Development);
        configs.Save(enrolled with { Development = true });
        Assert.True((await RunAsync(Enrollment(panel).EnrollAsync(Request(), TestContext.Current.CancellationToken))).Development);
    }

    [Fact]
    public async Task AnUnreadablePreviousConfigurationIsReplacedWithAWarning()
    {
        var paths = new AgentPaths(_root);
        Directory.CreateDirectory(paths.ConfigDirectory);
        await File.WriteAllTextAsync(paths.ConfigFile, "{ not json", TestContext.Current.CancellationToken);
        var enrollment = Enrollment(new FakePanel(this, busyAnswers: 0, retryAfter: null));
        List<string> warnings = [];
        enrollment.Warning = warnings.Add;

        var config = await RunAsync(enrollment.EnrollAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(config.NodeId, new AgentConfigStore(paths).Load()!.NodeId);
        Assert.Contains(paths.ConfigFile, Assert.Single(warnings), StringComparison.Ordinal);
    }

    private AgentEnrollment Enrollment(FakePanel panel) =>
        new(new AgentConfigStore(new AgentPaths(_root)), _keys, new FixedSystem(), _folders, panel, _clock);

    private EnrollmentRequest Request() => new(Panel, "pulsar_enroll_token", PanelKey.KeyId, null, Development: true);

    /// <summary>Moves the fake clock until the enrollment ends (its waits are on that clock).</summary>
    private async Task<T> RunAsync<T>(Task<T> running)
    {
        while (!running.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return await running;
    }

    /// <summary>The panel's identity and enrollment endpoints: busy (429) a few times, then enrolled.</summary>
    private sealed class FakePanel(AgentEnrollmentTests test, int busyAnswers, TimeSpan? retryAfter) : PanelTransport
    {
        private int _enrollments;

        public Guid NodeId { get; } = Guid.NewGuid();

        public bool Refuse { get; init; }

        public int Enrollments => _enrollments;

        public override HttpMessageHandler CreateHttpHandler() => new Handler(this);

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(new PanelIdentityResponse([test.PanelKey]));
            }

            var attempt = Interlocked.Increment(ref _enrollments);
            if (Refuse)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = JsonContent.Create(new AgentError("agent.enrollment_rejected", "The enrollment token is invalid."), options: ProtocolJson.Options),
                };
            }

            if (attempt <= busyAnswers)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
                busy.Headers.RetryAfter = retryAfter is { } wait ? new RetryConditionHeaderValue(wait) : null;
                return busy;
            }

            var result = new EnrollmentResult(NodeId, Guid.NewGuid(), "https://panel.example.com", [test.PanelKey]);
            return Json(ProtocolCrypto.SignMessage(test._panelKey, test.PanelKey.KeyId, AgentProtocol.Purposes.EnrollmentResult, ProtocolJson.Serialize(result)));
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ProtocolJson.Options) };

        private sealed class Handler(FakePanel panel) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(panel.Answer(request));
        }
    }

    private sealed class MemoryKeys : INodeKeyStore
    {
        private readonly ConcurrentDictionary<string, ECParameters> _keys = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Names => [.. _keys.Keys];

        public INodeKey Create(string name, NodeKeyScope scope)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keys[name] = key.ExportParameters(includePrivateParameters: true);
            return Open(new NodeKeyReference(name, scope, NodeKeyStorage.Software));
        }

        public INodeKey Open(NodeKeyReference reference) => new Key(reference, ECDsa.Create(_keys[reference.Name]));

        public void Delete(NodeKeyReference reference) => _keys.TryRemove(reference.Name, out _);

        private sealed class Key(NodeKeyReference reference, ECDsa key) : INodeKey
        {
            public NodeKeyReference Reference { get; } = reference;

            public byte[] PublicKey => key.ExportSubjectPublicKeyInfo();

            public byte[] Sign(ReadOnlySpan<byte> data) => ProtocolCrypto.Sign(key, data);

            public void Dispose() => key.Dispose();
        }
    }

    private sealed class FixedSystem : INodeSystem
    {
        public NodeInventory GetInventory(string? serversRoot) =>
            new("FLEET-01", "Windows 11 Pro", "26200", "Test CPU", 8, 16L * 1024 * 1024 * 1024, serversRoot);

        public NodeMetrics SampleMetrics() => throw new NotSupportedException();
    }

    private sealed class PlainFolders : IAgentDirectorySecurity
    {
        public List<string> ServersRoots { get; } = [];

        public void SecureState(string directory, IReadOnlyCollection<string> ownFolders, bool development) => Directory.CreateDirectory(directory);

        public void SecureFolder(string directory, bool development) => Directory.CreateDirectory(directory);

        public bool Refuse { get; set; }

        public void SecureServersRoot(string directory, bool development)
        {
            if (Refuse)
            {
                throw new AgentException($"{directory} is a junction or link.");
            }

            ServersRoots.Add(directory);
            Directory.CreateDirectory(directory);
        }
    }
}
