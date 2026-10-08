using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The panel's answer to a node key rotation is bound to the key the agent registered (docs/architecture/03, "Rotation").</summary>
public sealed class CredentialRotationClientTests : IDisposable
{
    private readonly ECDsa _panelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly TestNodeKey _newKey = new();
    private readonly AgentConfig _config;

    public CredentialRotationClientTests()
    {
        var panelPublic = _panelKey.ExportSubjectPublicKeyInfo();
        _config = new AgentConfig(new Uri("https://panel.example.com/"), Guid.NewGuid(), Guid.NewGuid(),
            new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Software),
            [new PanelKey(ProtocolCrypto.Fingerprint(panelPublic), panelPublic)], null, DateTimeOffset.UnixEpoch, Development: false);
    }

    public void Dispose()
    {
        _panelKey.Dispose();
        _newKey.Dispose();
    }

    [Fact]
    public async Task AnAnswerForTheRegisteredKeyIsAccepted()
    {
        var credentialId = Guid.NewGuid();
        var transport = Answering(new CredentialRotation(_config.NodeId, credentialId, ProtocolCrypto.Fingerprint(_newKey.PublicKey)));

        Assert.Equal(credentialId, await new CredentialRotationClient(transport).RegisterAsync(_config, "token", Guid.NewGuid(), _newKey,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAnswerForAnotherKeyIsRefused()
    {
        // A signed answer to another rotation of this node (replayed, or mixed up): not the key just registered.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var transport = Answering(new CredentialRotation(_config.NodeId, Guid.NewGuid(), ProtocolCrypto.Fingerprint(other.ExportSubjectPublicKeyInfo())));

        var refused = await Assert.ThrowsAsync<AgentException>(() => new CredentialRotationClient(transport).RegisterAsync(_config, "token",
            Guid.NewGuid(), _newKey, TestContext.Current.CancellationToken));

        Assert.Contains("another key", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerFromAnOlderPanelWithoutTheKeyIsStillAccepted()
    {
        var credentialId = Guid.NewGuid();
        var transport = Answering(new CredentialRotation(_config.NodeId, credentialId));

        Assert.Equal(credentialId, await new CredentialRotationClient(transport).RegisterAsync(_config, "token", Guid.NewGuid(), _newKey,
            TestContext.Current.CancellationToken));
    }

    private AnsweringTransport Answering(CredentialRotation rotation) =>
        new(ProtocolCrypto.SignMessage(_panelKey, _config.PanelKeys[0].KeyId, AgentProtocol.Purposes.RotationResult, ProtocolJson.Serialize(rotation)));

    private sealed class AnsweringTransport(SignedMessage answer) : PanelTransport
    {
        public override HttpMessageHandler CreateHttpHandler() => new Handler(answer);

        private sealed class Handler(SignedMessage answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(answer, options: ProtocolJson.Options) });
        }
    }

    private sealed class TestNodeKey : INodeKey
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public NodeKeyReference Reference { get; } = new("PulsarAgent-2", NodeKeyScope.Machine, NodeKeyStorage.Software);

        public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

        public byte[] Sign(ReadOnlySpan<byte> data) => ProtocolCrypto.Sign(_key, data);

        public void Dispose() => _key.Dispose();
    }
}
