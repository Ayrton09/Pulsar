using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Pulsar.Application.Access;

namespace Pulsar.Infrastructure.Access;

/// <summary>Sign-in providers' client secrets encrypted with the panel's Data Protection key ring (ADR-0011).</summary>
internal sealed class DataProtectionSsoSecretProtector(IDataProtectionProvider provider) : ISsoSecretProtector
{
    private const string Purpose = "Pulsar.Sso.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public byte[] Protect(string secret) => _protector.Protect(Encoding.UTF8.GetBytes(secret));

    public string Unprotect(byte[] protectedSecret) => Encoding.UTF8.GetString(_protector.Unprotect(protectedSecret));
}
