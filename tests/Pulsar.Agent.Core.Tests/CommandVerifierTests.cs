using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Commands;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

public sealed class CommandVerifierTests : IDisposable
{
    private static readonly HashSet<string> s_supported = [AgentCapabilities.Ping];
    private readonly ECDsa _panelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _nodeId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly PanelKey _pinned;
    private readonly CommandVerifier _verifier;

    public CommandVerifierTests()
    {
        var publicKey = _panelKey.ExportSubjectPublicKeyInfo();
        _pinned = new PanelKey(ProtocolCrypto.Fingerprint(publicKey), publicKey);
        _verifier = new CommandVerifier(_nodeId, _sessionId, [_pinned], s_supported, _clock);
    }

    public void Dispose() => _panelKey.Dispose();

    [Fact]
    public void ASignedCommandForThisSessionIsAccepted()
    {
        var envelope = Envelope(sequence: 1);

        var result = _verifier.Verify(Sign(envelope));

        Assert.Null(result.Rejection);
        Assert.Equal(envelope.CommandId, result.Envelope!.CommandId);
        Assert.IsType<PingCommand>(result.Envelope.Command);
    }

    [Fact]
    public void AKeyThatWasNotPinnedIsRefused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = ProtocolJson.Serialize(Envelope(sequence: 1));
        var keyId = ProtocolCrypto.Fingerprint(other.ExportSubjectPublicKeyInfo());

        var result = _verifier.Verify(ProtocolCrypto.SignMessage(other, keyId, AgentProtocol.Purposes.Command, payload));

        AssertRefused(result, AckStatus.InvalidSignature, "command.unknown_key");
    }

    [Fact]
    public void AForgedSignatureUnderThePinnedKeyIdIsRefused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = ProtocolJson.Serialize(Envelope(sequence: 1));

        var result = _verifier.Verify(ProtocolCrypto.SignMessage(other, _pinned.KeyId, AgentProtocol.Purposes.Command, payload));

        AssertRefused(result, AckStatus.InvalidSignature, "command.invalid_signature");
    }

    [Fact]
    public void ATamperedPayloadIsRefused()
    {
        var signed = Sign(Envelope(sequence: 1));
        var tampered = signed.Payload.ToArray();
        tampered[^2] ^= 0x01;

        var result = _verifier.Verify(signed with { Payload = tampered });

        AssertRefused(result, AckStatus.InvalidSignature, "command.invalid_signature");
    }

    [Fact]
    public void ASignatureForAnotherPurposeIsRefused()
    {
        var payload = ProtocolJson.Serialize(Envelope(sequence: 1));

        var result = _verifier.Verify(ProtocolCrypto.SignMessage(_panelKey, _pinned.KeyId, AgentProtocol.Purposes.EnrollmentResult, payload));

        AssertRefused(result, AckStatus.InvalidSignature, "command.invalid_signature");
    }

    [Fact]
    public void CommandsForAnotherNodeOrSessionAreRefused()
    {
        AssertRefused(_verifier.Verify(Sign(Envelope(sequence: 1) with { NodeId = Guid.NewGuid() })), AckStatus.Rejected, "command.wrong_recipient");
        AssertRefused(_verifier.Verify(Sign(Envelope(sequence: 2) with { SessionId = Guid.NewGuid() })), AckStatus.Rejected, "command.wrong_recipient");
    }

    [Fact]
    public void AReplayedCommandIsRefused()
    {
        var signed = Sign(Envelope(sequence: 1));

        Assert.Null(_verifier.Verify(signed).Rejection);
        AssertRefused(_verifier.Verify(signed), AckStatus.Rejected, "command.replayed");
    }

    [Fact]
    public void ExpiredCommandsAreRefusedAfterTheTolerance()
    {
        var envelope = Envelope(sequence: 1);
        _clock.Advance(envelope.ExpiresAt - _clock.GetUtcNow() + CommandVerifier.ClockTolerance + TimeSpan.FromSeconds(1));

        AssertRefused(_verifier.Verify(Sign(envelope)), AckStatus.Rejected, "command.expired");
    }

    [Fact]
    public void ExpiryIsJudgedOnThePanelClock()
    {
        // The panel's clock is 10 minutes behind ours: a command it just issued is still valid.
        _verifier.PanelClockOffset = TimeSpan.FromMinutes(-10);
        var issued = _clock.GetUtcNow() - TimeSpan.FromMinutes(10);
        var envelope = Envelope(sequence: 1) with { IssuedAt = issued, ExpiresAt = issued.AddSeconds(60) };

        Assert.Null(_verifier.Verify(Sign(envelope)).Rejection);
    }

    [Fact]
    public void CommandsIssuedInTheFutureAreRefused()
    {
        var issued = _clock.GetUtcNow().AddMinutes(5);
        var envelope = Envelope(sequence: 1) with { IssuedAt = issued, ExpiresAt = issued.AddSeconds(60) };

        AssertRefused(_verifier.Verify(Sign(envelope)), AckStatus.Rejected, "command.expired");
    }

    [Fact]
    public void ANewerProtocolVersionIsRefused()
    {
        var envelope = Envelope(sequence: 1) with { ProtocolVersion = AgentProtocol.CurrentVersion + 1 };

        AssertRefused(_verifier.Verify(Sign(envelope)), AckStatus.Rejected, "command.unsupported_version");
    }

    [Fact]
    public void UnknownCommandTypesAreUnsupportedNotMalformed()
    {
        var json = JsonNode.Parse(ProtocolJson.Serialize(Envelope(sequence: 1)))!;
        json["command"] = new JsonObject { ["$type"] = "server.format_disk" };
        var payload = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());

        var result = _verifier.Verify(ProtocolCrypto.SignMessage(_panelKey, _pinned.KeyId, AgentProtocol.Purposes.Command, payload));

        AssertRefused(result, AckStatus.UnsupportedCommand, "command.unsupported");
    }

    [Fact]
    public void KnownCommandsThisAgentDoesNotHandleAreUnsupported()
    {
        var verifier = new CommandVerifier(_nodeId, _sessionId, [_pinned], new HashSet<string>(), _clock);

        AssertRefused(verifier.Verify(Sign(Envelope(sequence: 1))), AckStatus.UnsupportedCommand, "command.unsupported");
    }

    [Fact]
    public void AValidSignatureOverGarbageIsMalformed()
    {
        var payload = "not json"u8.ToArray();

        var result = _verifier.Verify(ProtocolCrypto.SignMessage(_panelKey, _pinned.KeyId, AgentProtocol.Purposes.Command, payload));

        AssertRefused(result, AckStatus.Rejected, "command.malformed");
    }

    private CommandEnvelope Envelope(long sequence)
    {
        var now = _clock.GetUtcNow();
        return new CommandEnvelope(AgentProtocol.CurrentVersion, Guid.CreateVersion7(), _nodeId, _sessionId, sequence,
            now, now.AddSeconds(60), null, Guid.NewGuid(), new PingCommand(null));
    }

    private SignedMessage Sign(CommandEnvelope envelope) =>
        ProtocolCrypto.SignMessage(_panelKey, _pinned.KeyId, AgentProtocol.Purposes.Command, ProtocolJson.Serialize(envelope));

    private static void AssertRefused(CommandVerification result, AckStatus status, string code)
    {
        Assert.Null(result.Envelope);
        Assert.Equal(status, result.Rejection!.Status);
        Assert.Equal(code, result.Rejection.ErrorCode);
    }
}
