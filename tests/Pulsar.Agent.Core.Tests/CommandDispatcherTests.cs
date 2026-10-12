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
        Assert.Equal([AgentCapabilities.Ping, AgentCapabilities.RotateCredential, AgentCapabilities.UpdatePanelKeys, AgentCapabilities.UseAgentsAddress],
            _dispatcher.SupportedCommands.Order(StringComparer.Ordinal).ToArray().Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(70000, null)]
    [InlineData(8444, "http://panel.example.com:8444")]
    [InlineData(8444, "http://localhost:8444")]
    [InlineData(8444, "https://panel.example.com:8444/agent")]
    [InlineData(8444, "https://user@panel.example.com:8444")]
    [InlineData(8444, "not an address")]
    public void AnAgentsAddressWithoutAPortOrWithAnAddressThatIsNotAnHttpsOriginIsRefused(int port, string? url)
    {
        var ack = Execute(new UseAgentsAddressCommand(port, url));

        Assert.Equal(AckStatus.Rejected, ack.Status);
        Assert.Equal("command.invalid_agents_address", ack.ErrorCode);
        Assert.Empty(_control.AgentsAddresses);
    }

    [Fact]
    public void ThePortAndTheOperatorsAddressGoToTheConnection()
    {
        Assert.Equal(AckStatus.Accepted, Execute(new UseAgentsAddressCommand(8444)).Status);
        Assert.Equal(AckStatus.Accepted, Execute(new UseAgentsAddressCommand(8444, "https://Agents.Example.com:9444")).Status);

        Assert.Equal([(8444, null), (8444, new Uri("https://agents.example.com:9444/"))], _control.AgentsAddresses);
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

    [Fact]
    public async Task ACommandArrivingBeforeThePanelsClockIsKnownWaitsForIt()
    {
        // A session's verifier: the panel's first command can arrive between Hello's answer and the agent measuring the
        // panel's clock from it. This node's clock is two minutes behind: judged without the offset, it would be expired.
        var verifier = new CommandVerifier(_nodeId, _sessionId, () => _control.Keys, _dispatcher.SupportedCommands, _clock) { WaitsForClock = true };
        var message = Sign(new PingCommand(null), _panelKey, _pinned.KeyId, _clock.GetUtcNow() + TimeSpan.FromMinutes(2));

        var execution = _dispatcher.ExecuteAsync(verifier, message, _control);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(execution.IsCompleted);
        verifier.PanelClockOffset = TimeSpan.FromMinutes(2);

        Assert.Equal(AckStatus.Accepted, (await execution).Status);
    }

    [Fact]
    public async Task CommandsWaitingForASessionThatNeverSaidHelloAreDropped()
    {
        var verifier = new CommandVerifier(_nodeId, _sessionId, () => _control.Keys, _dispatcher.SupportedCommands, _clock) { WaitsForClock = true };
        var execution = _dispatcher.ExecuteAsync(verifier, Sign(new PingCommand(null), _panelKey, _pinned.KeyId, _clock.GetUtcNow()), _control);

        verifier.Abandon();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    private CommandAck Execute(AgentCommand command) => Execute(command, _panelKey, _pinned.KeyId);

    private CommandAck Execute(AgentCommand command, ECDsa signer, string keyId) =>
        // Every command this agent handles without servers completes synchronously.
        _dispatcher.ExecuteAsync(_verifier, Sign(command, signer, keyId, _clock.GetUtcNow()), _control).GetAwaiter().GetResult();

    private SignedMessage Sign(AgentCommand command, ECDsa signer, string keyId, DateTimeOffset issuedAt)
    {
        var envelope = new CommandEnvelope(AgentProtocol.CurrentVersion, Guid.CreateVersion7(), _nodeId, _sessionId, ++_sequence,
            issuedAt, issuedAt.AddSeconds(60), null, null, command);
        return ProtocolCrypto.SignMessage(signer, keyId, AgentProtocol.Purposes.Command, ProtocolJson.Serialize(envelope));
    }

    private static PanelKey Key(ECDsa key)
    {
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return new PanelKey(ProtocolCrypto.Fingerprint(publicKey), publicKey);
    }

    private sealed class Control : IAgentControl
    {
        public IReadOnlyList<PanelKey> Keys { get; set; } = [];

        public List<(int Port, Uri? Url)> AgentsAddresses { get; } = [];

        public Task<CommandAck> UseAgentsAddressAsync(Guid commandId, int port, Uri? url)
        {
            AgentsAddresses.Add((port, url));
            return Task.FromResult(new CommandAck(commandId, AckStatus.Accepted, null, null));
        }

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
