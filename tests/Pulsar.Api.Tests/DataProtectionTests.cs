using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests;

/// <summary>The Data Protection key ring can be encrypted with a certificate (Linux panels, moving it between machines).</summary>
public sealed class DataProtectionTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    [Fact]
    public void ACertificateEncryptsTheKeyRing()
    {
        var keys = TestKeys.NewDirectory();
        var pfx = Path.Combine(Path.GetTempPath(), "pulsar-tests", $"dp-{Guid.CreateVersion7():N}.pfx");
        Directory.CreateDirectory(Path.GetDirectoryName(pfx)!);
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=Pulsar test key ring", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, "test password"));
        }

        try
        {
            using var app = factory.WithWebHostBuilder(b => b
                .UseSetting("DataProtection:KeysPath", keys)
                .UseSetting("DataProtection:CertificatePath", pfx)
                .UseSetting("DataProtection:CertificatePassword", "test password"));

            var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
            Assert.Equal("secret", protector.Unprotect(protector.Protect("secret")));

            var key = File.ReadAllText(Assert.Single(Directory.GetFiles(keys, "key-*.xml")));
            Assert.Contains("EncryptedXmlDecryptor", key, StringComparison.Ordinal);
            Assert.DoesNotContain("DpapiXmlDecryptor", key, StringComparison.Ordinal);
        }
        finally
        {
            TestKeys.Delete(keys);
            File.Delete(pfx);
        }
    }
}
