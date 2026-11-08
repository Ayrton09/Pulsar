using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// The HTTP Server API's URL reservations as the agent makes them for a game's web ports (Pulsar 1.0.35): changing them
/// needs an administrator, as the agent's service is.
/// </summary>
public sealed class HttpServiceUrlAclsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-urlacl-").FullName;
    private readonly int _port = Random.Shared.Next(42_000, 48_000);

    public void Dispose()
    {
        if (ElevatedFactAttribute.Enabled)
        {
            new HttpServiceUrlAcls().Delete(HttpUrlReservations.Url(_port));
        }

        Directory.Delete(_root, recursive: true);
    }

    [ElevatedFact]
    public void AReservationIsMadeReadAndReleasedAsNetshShowsIt()
    {
        var acls = new HttpServiceUrlAcls();
        var url = HttpUrlReservations.Url(_port);
        var descriptor = HttpUrlReservations.Descriptor(WindowsIdentity.GetCurrent().User!.Value);
        Assert.Null(acls.Find(url));

        acls.Add(url, descriptor);
        Assert.Equal(descriptor, acls.Find(url), ignoreCase: true);

        acls.Delete(url);
        Assert.Null(acls.Find(url));
        acls.Delete(url); // nothing there: nothing to do
    }

    [ElevatedFact]
    public void TheAgentReservesAndReleasesTheServersPorts()
    {
        var reservations = new HttpUrlReservations(new HttpServiceUrlAcls(), HttpServiceUrlAcls.SidOf, Path.Combine(_root, "http-urls.json"),
            NullLogger<HttpUrlReservations>.Instance);

        Assert.Empty(reservations.Reserve("abcd1234", [_port], ProcessIdentity.CurrentUser));
        Assert.NotNull(new HttpServiceUrlAcls().Find(HttpUrlReservations.Url(_port)));

        reservations.Remove("abcd1234");
        Assert.Null(new HttpServiceUrlAcls().Find(HttpUrlReservations.Url(_port)));
    }

    [WindowsFact]
    public void AReservationIsReadWithoutElevation()
    {
        // Windows reserves this one for everyone, as netsh http show urlacl lists it.
        Assert.Contains(";;;WD)", new HttpServiceUrlAcls().Find("http://+:80/Temporary_Listen_Addresses/"), StringComparison.OrdinalIgnoreCase);
        Assert.Null(new HttpServiceUrlAcls().Find(HttpUrlReservations.Url(_port)));
    }

    [WindowsFact]
    public void AnAccountsSecurityIdentifierIsItsOwn()
    {
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, HttpServiceUrlAcls.SidOf(ProcessIdentity.CurrentUser));
        Assert.Equal("S-1-5-18", HttpServiceUrlAcls.SidOf(ProcessIdentity.Account(@"NT AUTHORITY\SYSTEM")));
    }
}
