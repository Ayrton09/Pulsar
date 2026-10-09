using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Pulsar.Application.Databases;

namespace Pulsar.Infrastructure.Databases;

/// <summary>Database host and game database passwords encrypted with the panel's Data Protection key ring (ADR-0011).</summary>
internal sealed class DataProtectionDatabaseSecretProtector(IDataProtectionProvider provider) : IDatabaseSecretProtector
{
    private const string Purpose = "Pulsar.Databases.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public byte[] Protect(string value) => _protector.Protect(Encoding.UTF8.GetBytes(value));

    public string Unprotect(byte[] protectedValue) => Encoding.UTF8.GetString(_protector.Unprotect(protectedValue));
}
