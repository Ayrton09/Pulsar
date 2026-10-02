using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Commands;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

public sealed class CommandDispatcherTests : IDisposable
{
    private readonly ECDsa _panelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _nextPanelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly Guid _nodeId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly CommandDispatcher _dispatcher = new(NullLogger<CommandDispatcher>.Instance);
    private readonly Control _control = new();
    private readonly PanelKey _pinned;
    private readonly PanelKey _next;
    private readonly CommandVerifier _verifier;
    private long _sequence;

    public CommandDispatcherTests()
    {
        _pinned = Key(_panelKey);
        _next = Key(_nextPanelKey);
        _verifier = new CommandVerifier(_nodeId, _sessionId, () => _control.Keys, _dispatcher.SupportedCommands, _clock);
        _control.Keys = [_pinned];
    }

    public void Dispose()
    {
        _panelKey.Dispose();
        _nextPanelKey.Dispose();
    }

    [Fact]
    public void TheAgentAnnouncesWhatItHandles()
    {
        Assert.Equal([AgentCapabilities.Ping, AgentCapabilities.RotateCredential, AgentCapabilities.UpdatePanelKeys],
            _dispatcher.SupportedCommands.Order(StringComparer.Ordinal).ToArray().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ARotationRequestStartsARotationOnce()
    {
        Assert.Equal(AckStatus.Accepted, Execute(new RotateCredentialCommand()).Status);
        Assert.Equal(AckStatus.AlreadyInState, Execute(new RotateCredentialCommand()).Status);
        Assert.Equal(2, _control.RotationRequests); // the second one found a rotation under way
    }

    [Fact]
    public void NewPanelKeysApplyToTheNextCommand()
    {
        Assert.Equal(AckStatus.Accepted, Execute(new UpdatePanelKeysCommand([_next, _pinned])).Status);

        // The next command, signed with the new key, verifies against the updated pins.
        Assert.Equal(AckStatus.Accepted, Execute(new PingCommand(null), _nextPanelKey, _next.KeyId).Status);
        Assert.Equal(AckStatus.AlreadyInState, Execute(new UpdatePanelKeysCommand([_pinned, _next])).Status);
    }

    [Fact]
    public void AKeyListWithoutTheSigningKeyIsRefused()
    {
        var ack = Execute(new UpdatePanelKeysCommand([_next]));

        Assert.Equal(AckStatus.Rejected, ack.Status);
        Assert.Equal("command.invalid_panel_keys", ack.ErrorCode);
        Assert.Equal([_pinned], _control.Keys);
    }

    [Fact]
    public void KeysWhoseIdIsNotTheirFingerprintAreRefused()
    {
        var mislabeled = _next with { KeyId = _pinned.KeyId };

        Assert.Equal(AckStatus.Rejected, Execute(new UpdatePanelKeysCommand([_pinned, mislabeled])).Status);
        Assert.Equal(AckStatus.Rejected, Execute(new UpdatePanelKeysCommand([])).Status);
    }

    [Fact]
    public void KeysThatAreNotP256AreRefused()
    {
        using var rsa = RSA.Create(2048);
        var spki = rsa.ExportSubjectPublicKeyInfo();

        Assert.False(CommandDispatcher.AreValid([_pinned, new PanelKey(ProtocolCrypto.Fingerprint(spki), spki)], _pinned.KeyId));
        Assert.True(CommandDispatcher.AreValid([_pinned, _next], _pinned.KeyId));
    }

    private CommandAck Execute(AgentCommand command) => Execute(command, _panelKey, _pinned.KeyId);

    private CommandAck Execute(AgentCommand command, ECDsa signer, string keyId)
    {
        var now = _clock.GetUtcNow();
        var envelope = new CommandEnvelope(AgentProtocol.CurrentVersion, Guid.CreateVersion7(), _nodeId, _sessionId, ++_sequence,
            now, now.AddSeconds(60), null, null, command);
        var message = ProtocolCrypto.SignMessage(signer, keyId, AgentProtocol.Purposes.Command, ProtocolJson.Serialize(envelope));
        // Every command this agent handles without servers completes synchronously.
        return _dispatcher.ExecuteAsync(_verifier, message, _control).GetAwaiter().GetResult();
    }

    private static PanelKey Key(ECDsa key)
    {
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return new PanelKey(ProtocolCrypto.Fingerprint(publicKey), publicKey);
    }

    private sealed class Control : IAgentControl
    {
        public IReadOnlyList<PanelKey> Keys { get; set; } = [];

        public int RotationRequests { get; private set; }

        public bool RequestCredentialRotation() => ++RotationRequests == 1;

        public bool ReplacePanelKeys(IReadOnlyList<PanelKey> keys)
        {
            if (Keys.Select(k => k.KeyId).Order(StringComparer.Ordinal).SequenceEqual(keys.Select(k => k.KeyId).Order(StringComparer.Ordinal)))
            {
                return false;
            }

            Keys = keys;
            return true;
        }
    }
}
