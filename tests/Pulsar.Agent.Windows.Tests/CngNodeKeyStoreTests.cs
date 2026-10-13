using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Security;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>Where the node key goes (docs/architecture/03): the TPM when it works, and never silently the software provider.</summary>
public sealed class CngNodeKeyStoreTests
{
    private const int DeviceNotReady = unchecked((int)0x80090030); // NTE_DEVICE_NOT_READY
    private const int NotSupported = unchecked((int)0x80090029); // NTE_NOT_SUPPORTED

    private readonly List<(LogLevel Level, string Message)> _log = [];
    private readonly List<TimeSpan> _slept = [];
    private bool _tpmPresent = true;

    [Fact]
    public void ATpmThatIsNotReadyYetIsRetriedBeforeTheSoftwareKeyIsUsed()
    {
        var attempts = 0;
        var store = Store((provider, storage) =>
        {
            attempts++;
            return storage == NodeKeyStorage.Tpm && attempts < 3 ? throw new CryptographicException(DeviceNotReady) : new FakeKey(storage);
        });

        var key = store.Create("pulsar-node", NodeKeyScope.Machine);

        Assert.Equal(NodeKeyStorage.Tpm, key.Reference.Storage);
        Assert.Equal(3, attempts);
        Assert.Equal(2, _slept.Count);
        Assert.DoesNotContain(_log, l => l.Level >= LogLevel.Warning);
    }

    [Fact]
    public void ATpmThatStaysNotReadyFallsBackToSoftwareAfterAWarningWithTheReason()
    {
        var tpmAttempts = 0;
        var store = Store((provider, storage) =>
        {
            if (storage == NodeKeyStorage.Tpm)
            {
                tpmAttempts++;
                throw new CryptographicException(DeviceNotReady);
            }

            return new FakeKey(storage);
        });

        var key = store.Create("pulsar-node", NodeKeyScope.Machine);

        Assert.Equal(NodeKeyStorage.Software, key.Reference.Storage);
        Assert.Equal(4, tpmAttempts); // the first and three more, over a few seconds
        Assert.InRange(_slept.Sum(s => s.TotalSeconds), 1, 15);
        var warning = Assert.Single(_log, l => l.Level == LogLevel.Warning);
        Assert.Contains("80090030", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMachineWithoutATpmDoesNotWaitForOneEvenWhenTheProviderSaysNotReady()
    {
        // The Platform Crypto Provider often answers NTE_DEVICE_NOT_READY where there is no usable TPM at all (VMs, CI, PCs
        // without one): enrollment and rotation must not wait 3.5 s for a device that is not there.
        _tpmPresent = false;
        var tpmAttempts = 0;
        var store = Store((provider, storage) =>
        {
            if (storage == NodeKeyStorage.Tpm)
            {
                tpmAttempts++;
                throw new CryptographicException(DeviceNotReady);
            }

            return new FakeKey(storage);
        });

        var key = store.Create("pulsar-node", NodeKeyScope.Machine);

        Assert.Equal(NodeKeyStorage.Software, key.Reference.Storage);
        Assert.Equal(1, tpmAttempts);
        Assert.Empty(_slept);
        Assert.Single(_log, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public void ATbsCodeThatMeansThereIsNoTpmIsRecognizedAndAnythingElseIsTreatedAsAPossibleTpm()
    {
        Assert.False(CngNodeKeyStore.TpmMayBePresent(unchecked((int)0x8028400F))); // TBS_E_TPM_NOT_FOUND
        Assert.False(CngNodeKeyStore.TpmMayBePresent(unchecked((int)0x80284010))); // TBS_E_SERVICE_DISABLED
        Assert.True(CngNodeKeyStore.TpmMayBePresent(0)); // a device
        Assert.True(CngNodeKeyStore.TpmMayBePresent(unchecked((int)0x80284008))); // the service is not running (yet)
        Assert.True(CngNodeKeyStore.TpmMayBePresent(unchecked((int)0x8028400B))); // starting
    }

    [WindowsFact]
    public void TheRealTbsQuestionAnswersOnAnyMachine()
    {
        // P/Invoke into tbs.dll with the right struct: no exception whether or not this machine has a TPM.
        var answer = new CngNodeKeyStore(new ListLogger(_log)).TpmPresent();

        _log.Add((LogLevel.Information, $"TBS says a TPM may be present: {answer}"));
    }

    [Fact]
    public void AnErrorThatMeansThereIsNoTpmFallsBackAtOnceAndSaysSo()
    {
        var tpmAttempts = 0;
        var store = Store((provider, storage) =>
        {
            if (storage == NodeKeyStorage.Tpm)
            {
                tpmAttempts++;
                throw new CryptographicException(NotSupported);
            }

            return new FakeKey(storage);
        });

        var key = store.Create("pulsar-node", NodeKeyScope.Machine);

        Assert.Equal(NodeKeyStorage.Software, key.Reference.Storage);
        Assert.Equal(1, tpmAttempts);
        Assert.Empty(_slept);
        Assert.Single(_log, l => l.Level == LogLevel.Warning);
    }

    private CngNodeKeyStore Store(Func<CngProvider, string, INodeKey> create) =>
        new(new ListLogger(_log))
        {
            CreateKey = (provider, storage, _, _) => create(provider, storage),
            Sleep = _slept.Add,
            TpmPresent = () => _tpmPresent,
        };

    private sealed class FakeKey(string storage) : INodeKey
    {
        public NodeKeyReference Reference { get; } = new("pulsar-node", NodeKeyScope.Machine, storage);

        public byte[] PublicKey => [];

        public byte[] Sign(ReadOnlySpan<byte> data) => [];

        public X509Certificate2 CreateCertificate() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class ListLogger(List<(LogLevel, string)> log) : ILogger<CngNodeKeyStore>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            log.Add((logLevel, formatter(state, exception)));
    }
}
