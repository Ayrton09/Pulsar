using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Pulsar.Application.Servers;

namespace Pulsar.Infrastructure.Servers;

/// <summary>Secret server variables encrypted with the panel's Data Protection key ring (ADR-0011).</summary>
internal sealed class DataProtectionVariableProtector(IDataProtectionProvider provider) : IVariableProtector
{
    private const string Purpose = "Pulsar.ServerVariables.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public byte[] Protect(string value) => _protector.Protect(Encoding.UTF8.GetBytes(value));

    public string Unprotect(byte[] protectedValue) => Encoding.UTF8.GetString(_protector.Unprotect(protectedValue));
}

/// <summary>Backup storage secret keys encrypted with the panel's Data Protection key ring (ADR-0011).</summary>
internal sealed class DataProtectionBackupStorageSecretProtector(IDataProtectionProvider provider) : IBackupStorageSecretProtector
{
    private const string Purpose = "Pulsar.BackupStorage.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public byte[] Protect(string value) => _protector.Protect(Encoding.UTF8.GetBytes(value));

    public string Unprotect(byte[] protectedValue) => Encoding.UTF8.GetString(_protector.Unprotect(protectedValue));
}
