using System.Security.AccessControl;
using System.Security.Cryptography;
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

    public INodeKey Create(string name, NodeKeyScope scope)
    {
        try
        {
            return Create(s_platformProvider, NodeKeyStorage.Tpm, name, scope);
        }
        catch (CryptographicException ex)
        {
            // No TPM, TPM disabled or not ready, or a TPM without ECDSA P-256: the software provider is the fallback.
            LogNoTpm(ex.Message);
        }

        return Create(CngProvider.MicrosoftSoftwareKeyStorageProvider, NodeKeyStorage.Software, name, scope);
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "No usable TPM for the node key ({Reason}); using the software key storage provider")]
    private partial void LogNoTpm(string reason);

    private sealed class CngNodeKey(NodeKeyReference reference, CngKey key) : INodeKey
    {
        private readonly ECDsaCng _ecdsa = new(key);

        public NodeKeyReference Reference { get; } = reference;

        // Exporting the public half is always allowed, even for non-exportable keys.
        public byte[] PublicKey => _ecdsa.ExportSubjectPublicKeyInfo();

        public byte[] Sign(ReadOnlySpan<byte> data) => _ecdsa.SignData(data, HashAlgorithmName.SHA256, ProtocolCrypto.SignatureFormat);

        public void Dispose()
        {
            _ecdsa.Dispose();
            key.Dispose();
        }
    }
}
