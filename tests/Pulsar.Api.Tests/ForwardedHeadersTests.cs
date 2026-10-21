using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests;

/// <summary>A panel on real Kestrel whose anonymous routes take two requests a minute from one address.</summary>
public sealed class TightKestrelPanel : KestrelPanelFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:Auth:AnonymousPermitLimit", "2");
    }
}

/// <summary>A panel on real Kestrel behind a proxy on its own machine, named in its settings.</summary>
public sealed class ProxiedKestrelPanel : KestrelPanelFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Security:TrustedProxies:0", "127.0.0.1");
    }
}

/// <summary>
/// The client's address is the connection's unless a proxy the settings name says otherwise (docs/architecture/08):
/// a process on the panel's own machine (a game server's account) is no proxy, and cannot choose the address its
/// requests are limited and audited by.
/// </summary>
public sealed class ForwardedHeadersTests(TightKestrelPanel factory) : IClassFixture<TightKestrelPanel>
{
    [DatabaseFact]
    public async Task ALocalProcessCannotChooseTheAddressItIsLimitedBy()
    {
        factory.StartServer();
        using var http = new HttpClient(factory.Handler()) { BaseAddress = factory.MainUrl };
        var answers = new List<HttpStatusCode>();
        for (var i = 1; i <= 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
            request.Headers.Add("X-Forwarded-For", $"192.0.2.{i}");
            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            answers.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], answers);
    }
}

public sealed class TrustedProxyTests(ProxiedKestrelPanel factory) : IClassFixture<ProxiedKestrelPanel>
{
    [DatabaseFact]
    public async Task ANamedProxySaysWhoTheClientIs()
    {
        factory.StartServer();
        using var client = await PanelClient.CreateAsync(factory.Handler(), factory.MainUrl);
        client.Http.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.77");

        using (var login = await client.LoginAsync("proxied-nobody", "whatever password"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var failed = await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog
            .Where(e => e.Action == AuditActions.Auth.LoginFailed).Select(e => e.IpAddress).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(IPAddress.Parse("192.0.2.77"), failed);
    }
}
