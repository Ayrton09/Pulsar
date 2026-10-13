using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Windows;

/// <summary>
/// Node keys in CNG (docs/architecture/03): ECDSA P-256, non-exportable, in the TPM through the Microsoft Platform
/// Crypto Provider when the machine has one, otherwise in the software key storage provider. Machine keys are readable
/// only by SYSTEM (the service) and Administrators (enrollment, status).
/// </summary>
public sealed partial class CngNodeKeyStore(ILogger<CngNodeKeyStore> logger) : INodeKeyStore
{
    private static readonly CngProvider s_platformProvider = new("Microsoft Platform Crypto Provider");

    // DACL_SECURITY_INFORMATION: the property sets only the key's DACL.
    private const CngPropertyOptions DaclSecurityInformation = (CngPropertyOptions)0x4;
    private const string SecurityDescriptorProperty = "Security Descr";

    // A TPM that is not ready yet (the service starting at boot, a busy TPM) answers a moment later: tried again a few
    // times over a few seconds before a key that never leaves the machine's hardware is given up for a software one.
    private static readonly TimeSpan[] s_tpmRetryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    /// <summary>Tests: creates the key in a provider (the real one by default).</summary>
    internal Func<CngProvider, string, string, NodeKeyScope, INodeKey>? CreateKey { get; init; }

    /// <summary>Tests: waits between the TPM's attempts.</summary>
    internal Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>
    /// Whether there may be a TPM (the TPM Base Services' answer), asked only when an attempt failed with a "not now" code.
    /// Tests replace it.
    /// </summary>
    internal Func<bool> TpmPresent { get; init; } = AskTbs;

    /// <summary>
    /// What <c>Tbsi_GetDeviceInfo</c> answered, as to whether a TPM may be there: TBS_E_TPM_NOT_FOUND and TBS_E_SERVICE_DISABLED
    /// say there is none to wait for; success, or a service that is not running or starting, says there may be.
    /// </summary>
    internal static bool TpmMayBePresent(int tbsResult) => unchecked((uint)tbsResult) is not (0x8028400F or 0x80284010);

    private static bool AskTbs()
    {
        try
        {
            var info = default(TpmDeviceInfo);
            return TpmMayBePresent(unchecked((int)Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), ref info)));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false; // no TPM Base Services
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [LibraryImport("tbs.dll")]
    private static partial uint Tbsi_GetDeviceInfo(uint size, ref TpmDeviceInfo info);

    public INodeKey Create(string name, NodeKeyScope scope)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return CreateIn(s_platformProvider, NodeKeyStorage.Tpm, name, scope);
            }
            catch (CryptographicException ex) when (IsTransient(ex) && attempt < s_tpmRetryDelays.Length && TpmPresent())
            {
                LogTpmNotReady(ex.HResult, ex.Message, attempt + 1);
                Sleep(s_tpmRetryDelays[attempt]);
            }
            catch (CryptographicException ex)
            {
                // No TPM, TPM disabled, or a TPM without ECDSA P-256 (or one that stays not ready): the software provider
                // is the fallback, and the reason is in the log (the key's storage is also shown by 'status').
                LogNoTpm(ex.HResult, ex.Message);
                break;
            }
        }

        return CreateIn(CngProvider.MicrosoftSoftwareKeyStorageProvider, NodeKeyStorage.Software, name, scope);
    }

    private INodeKey CreateIn(CngProvider provider, string storage, string name, NodeKeyScope scope) =>
        CreateKey is { } create ? create(provider, storage, name, scope) : Create(provider, storage, name, scope);

    /// <summary>
    /// Errors that say "not now" rather than "not here": the device is not ready (NTE_DEVICE_NOT_READY), or the TPM base
    /// services are starting, busy or out of contexts (TBS_E_SERVICE_NOT_RUNNING, _TOO_MANY_TBS_CONTEXTS,
    /// _TOO_MANY_RESOURCES, _SERVICE_START_PENDING, _COMMAND_CANCELED). Retried only when a TPM may be there (see TpmPresent).
    /// </summary>
    private static bool IsTransient(CryptographicException ex) => unchecked((uint)ex.HResult) is
        0x80090030 or 0x80284008 or 0x80284009 or 0x8028400A or 0x8028400B or 0x8028400D;

    public INodeKey Open(NodeKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var key = CngKey.Open(reference.Name, Provider(reference.Storage), OpenOptions(reference.Scope));
        return new CngNodeKey(reference, key);
    }

    public void Delete(NodeKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var provider = Provider(reference.Storage);
        var options = OpenOptions(reference.Scope);
        if (CngKey.Exists(reference.Name, provider, options))
        {
            using var key = CngKey.Open(reference.Name, provider, options);
            key.Delete();
        }
    }

    private static CngNodeKey Create(CngProvider provider, string storage, string name, NodeKeyScope scope)
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = provider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            KeyCreationOptions = scope == NodeKeyScope.Machine ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
        };

        if (scope == NodeKeyScope.Machine)
        {
            // Protected DACL: SYSTEM and Administrators only (the default would also include the creating user).
            var descriptor = new RawSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)");
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            parameters.Parameters.Add(new CngProperty(SecurityDescriptorProperty, bytes, DaclSecurityInformation));
        }

        var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, parameters);
        return new CngNodeKey(new NodeKeyReference(name, scope, storage), key);
    }

    private static CngProvider Provider(string storage) => storage switch
    {
        NodeKeyStorage.Tpm => s_platformProvider,
        NodeKeyStorage.Software => CngProvider.MicrosoftSoftwareKeyStorageProvider,
        _ => throw new CryptographicException($"Unknown key storage '{storage}'."),
    };

    private static CngKeyOpenOptions OpenOptions(NodeKeyScope scope) =>
        scope == NodeKeyScope.Machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No usable TPM for the node key (error 0x{HResult:X8}: {Reason}); using the software key storage provider")]
    private partial void LogNoTpm(int hResult, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The TPM is not ready for the node key (error 0x{HResult:X8}: {Reason}); trying again (attempt {Attempt})")]
    private partial void LogTpmNotReady(int hResult, string reason, int attempt);

    private sealed class CngNodeKey(NodeKeyReference reference, CngKey key) : INodeKey
    {
        private readonly ECDsaCng _ecdsa = new(key);

        public NodeKeyReference Reference { get; } = reference;

        // Exporting the public half is always allowed, even for non-exportable keys.
        public byte[] PublicKey => _ecdsa.ExportSubjectPublicKeyInfo();

        public byte[] Sign(ReadOnlySpan<byte> data) => _ecdsa.SignData(data, HashAlgorithmName.SHA256, ProtocolCrypto.SignatureFormat);

        // A persisted key: the certificate names its container, so SChannel signs the handshake with it (TPM included).
        public X509Certificate2 CreateCertificate() => NodeCertificate.Create(_ecdsa);

        public void Dispose()
        {
            _ecdsa.Dispose();
            key.Dispose();
        }
    }
}
