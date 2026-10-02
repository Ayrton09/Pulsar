using System.Security.Cryptography;
using System.Text;

namespace Pulsar.Agent.Windows.Tests;

public sealed class DpapiStateProtectorTests
{
    [WindowsFact]
    public void ProtectsWithTheMachineKeyAndDetectsTampering()
    {
        var protector = new DpapiStateProtector();
        var secret = Encoding.UTF8.GetBytes("""{"rconPassword":"Rc0nPassw0rd"}""");

        var protectedData = protector.Protect(secret);

        Assert.DoesNotContain("Rc0nPassw0rd", Encoding.UTF8.GetString(protectedData), StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(protectedData));
        protectedData[^1] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedData));
    }
}
