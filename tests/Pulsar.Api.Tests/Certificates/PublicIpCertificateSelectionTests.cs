using System.Net;
using System.Security.Cryptography;
using Pulsar.Api.Certificates;

namespace Pulsar.Api.Tests.Certificates;

/// <summary>
/// Which connections get the public IP certificate (browsers send no server name for an IP address): from the internet,
/// to the certificate's address or through the router's port forward, naming no host, while it is valid.
/// </summary>
public sealed class PublicIpCertificateSelectionTests : IDisposable
{
    private readonly CertificateAuthority _authority = CertificateAuthority.Create();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ServedCertificate _v4;
    private readonly ServedCertificate _v6;

    public PublicIpCertificateSelectionTests()
    {
        _v4 = Served("1.2.3.4");
        _v6 = Served("2001:4860:4860::8888");
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [Theory]
    // From the internet through the router's port forward (the panel sees its LAN address), or to the address itself.
    [InlineData("8.8.8.8", "192.168.1.10", null, true)]
    [InlineData("::ffff:8.8.8.8", "::ffff:192.168.1.10", null, true)]
    [InlineData("8.8.8.8", "10.0.0.5", null, true)]
    [InlineData("8.8.8.8", "1.2.3.4", null, true)]
    [InlineData("8.8.8.8", "192.168.1.10", "1.2.3.4", true)]
    // From the local network, the panel's own machine, carrier-grade NAT or a link: the installer's certificate.
    [InlineData("192.168.1.20", "192.168.1.10", null, false)]
    [InlineData("10.1.2.3", "10.0.0.5", null, false)]
    [InlineData("172.20.0.4", "192.168.1.10", null, false)]
    [InlineData("100.64.1.1", "192.168.1.10", null, false)]
    [InlineData("127.0.0.1", "127.0.0.1", null, false)]
    [InlineData("169.254.3.3", "192.168.1.10", null, false)]
    [InlineData("::ffff:192.168.1.20", "::ffff:192.168.1.10", null, false)]
    // A host name was asked for: the certificate cannot be for it.
    [InlineData("8.8.8.8", "192.168.1.10", "panel.example.com", false)]
    [InlineData("8.8.8.8", "192.168.1.10", "5.6.7.8", false)]
    // Another public address of this machine, or the other family: not what the certificate names.
    [InlineData("8.8.8.8", "5.6.7.8", null, false)]
    [InlineData("2a00:1450:4001::1", "2001:4860:1234::1", null, false)]
    [InlineData("8.8.8.8", "8.8.8.8", null, false)]
    public void ByTheConnectionsAddresses(string remote, string local, string? serverName, bool served) =>
        Assert.Equal(served, PublicIpCertificateSelection.ShouldServe(_v4, IPAddress.Parse(remote), IPAddress.Parse(local), serverName, Now));

    [Theory]
    [InlineData("2a00:1450:4001::1", "2001:4860:4860::8888", true)]
    [InlineData("2a00:1450:4001::1", "fd00::5", true)]
    [InlineData("fd00::1", "2001:4860:4860::8888", false)]
    [InlineData("fe80::1", "2001:4860:4860::8888", false)]
    [InlineData("::1", "::1", false)]
    [InlineData("2a00:1450:4001::1", "2001:4860:4860::8844", false)]
    [InlineData("8.8.8.8", "192.168.1.10", false)]
    public void ForAnIpv6Address(string remote, string local, bool served) =>
        Assert.Equal(served, PublicIpCertificateSelection.ShouldServe(_v6, IPAddress.Parse(remote), IPAddress.Parse(local), null, Now));

    [Fact]
    public void OnlyWhileItIsValid()
    {
        var remote = IPAddress.Parse("8.8.8.8");
        var local = IPAddress.Parse("192.168.1.10");

        Assert.False(PublicIpCertificateSelection.ShouldServe(_v4, remote, local, null, _v4.NotAfter));
        Assert.False(PublicIpCertificateSelection.ShouldServe(_v4, remote, local, null, _v4.NotBefore.AddSeconds(-1)));
        Assert.False(PublicIpCertificateSelection.ShouldServe(_v4, null, local, null, Now));
        Assert.False(PublicIpCertificateSelection.ShouldServe(_v4, remote, null, null, Now));
    }

    [Fact]
    public void TheLoopbackCountsAsTheInternetOnlyForTests()
    {
        Assert.False(PublicIpCertificateSelection.ShouldServe(_v4, IPAddress.Loopback, IPAddress.Loopback, null, Now));
        Assert.True(PublicIpCertificateSelection.ShouldServe(_v4, IPAddress.Loopback, IPAddress.Loopback, null, Now, loopbackIsInternet: true));
    }

    [Fact]
    public void TheServedCertificateCarriesItsChain()
    {
        Assert.Single(_v4.Intermediates);
        Assert.Equal(_authority.Intermediate.RawData, _v4.Intermediates[0].RawData);
        Assert.True(_v4.Leaf.HasPrivateKey);
        Assert.Equal(64, _v4.Sha256.Length);
    }

    [Fact]
    public void TheStoreKeepsThePreviousCertificateOneSwapLonger()
    {
        using var store = new PublicIpCertificateStore();
        var first = Served("1.2.3.4");
        var second = Served("1.2.3.4");
        var third = Served("1.2.3.4");

        store.Serve(first);
        store.Serve(second);
        Assert.NotEqual(IntPtr.Zero, first.Leaf.Handle); // still alive: a handshake may have just picked it
        store.Serve(third);
        Assert.Same(third, store.Current);
        Assert.Equal(IntPtr.Zero, first.Leaf.Handle);
        store.Serve(null);
        Assert.Null(store.Current);
    }

    public void Dispose()
    {
        _v4.Dispose();
        _v6.Dispose();
        _key.Dispose();
        _authority.Dispose();
    }

    private ServedCertificate Served(string address)
    {
        var ip = IPAddress.Parse(address);
        var chain = _authority.IssueChain(_key, ip, TimeSpan.FromHours(160));
        try
        {
            return ServedCertificate.Create(chain, _key, ip);
        }
        finally
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }
        }
    }
}
