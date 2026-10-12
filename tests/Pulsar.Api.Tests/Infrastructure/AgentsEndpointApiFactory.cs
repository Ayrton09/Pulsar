using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Agents;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// The panel with an agents' endpoint (Phase 16) on port <see cref="AgentsPort"/>. TestServer has no TLS, so a request
/// says it came through that endpoint, and with which client certificate, in test headers that only this factory
/// reads; the real endpoint is Kestrel's (AgentsEndpointKestrelTests).
/// </summary>
public class AgentsEndpointApiFactory : PulsarApiFactory
{
    public const int AgentsPort = 8444;
    private const string EndpointHeader = "X-Test-Agents-Endpoint";
    private const string CertificateHeader = "X-Test-Client-Certificate";

    public static void AddTransportHeaders(HttpRequestHeaders headers, bool agentsEndpoint, X509Certificate2? certificate)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (agentsEndpoint)
        {
            headers.Add(EndpointHeader, "1");
        }

        if (certificate is not null)
        {
            headers.Add(CertificateHeader, Convert.ToBase64String(certificate.RawData));
        }
    }

    public static void AddTransportHeaders(IHeaderDictionary headers, bool agentsEndpoint, X509Certificate2? certificate)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (agentsEndpoint)
        {
            headers[EndpointHeader] = "1";
        }

        if (certificate is not null)
        {
            headers[CertificateHeader] = Convert.ToBase64String(certificate.RawData);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // As a real panel declares it; TestServer never binds it.
        builder.UseSetting("Kestrel:Endpoints:Agents:Url", "https://localhost:" + AgentsPort);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, TestTransport>();
        });
    }

    /// <summary>First in the pipeline: what Kestrel's agents' endpoint and its TLS handshake would have set.</summary>
    private sealed class TestTransport : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Kestrel tells the endpoint its port when it binds it.
            app.ApplicationServices.GetRequiredService<AgentsEndpoint>().Listening(AgentsPort);
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.ContainsKey(EndpointHeader))
                {
                    context.Features.Set(AgentsEndpointFeature.Instance);
                }

                if (context.Request.Headers.TryGetValue(CertificateHeader, out var der))
                {
                    context.Features.Set<ITlsConnectionFeature>(new TlsConnectionFeature
                    {
                        ClientCertificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(der.ToString())),
                    });
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
