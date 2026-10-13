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
