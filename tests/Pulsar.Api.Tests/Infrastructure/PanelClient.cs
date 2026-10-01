using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Pulsar.Api.Auth;
using Pulsar.Api.Security;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// An HTTP client that behaves like the SPA: keeps cookies, loads the session first and echoes the antiforgery
/// token in the header on state-changing requests. HTTPS base address because every cookie is Secure.
/// </summary>
public sealed class PanelClient : IDisposable
{
    private static readonly Uri s_baseAddress = new("https://localhost");
    private readonly CookieContainer _cookies = new();

    private PanelClient(WebApplicationFactory<Program> factory, bool sendAntiforgeryHeader)
    {
        Http = factory.CreateDefaultClient(s_baseAddress,
            new AntiforgeryHeaderHandler(_cookies, sendAntiforgeryHeader), new CookieContainerHandler(_cookies));
    }

    public HttpClient Http { get; }

    /// <summary>Creates a client and loads the session (which issues the antiforgery token).</summary>
    public static async Task<PanelClient> CreateAsync(WebApplicationFactory<Program> factory, bool sendAntiforgeryHeader = true)
    {
        var client = new PanelClient(factory, sendAntiforgeryHeader);
        await client.GetSessionAsync();
        return client;
    }

    public async Task<SessionResponse> GetSessionAsync()
    {
        using var response = await Http.GetAsync(new Uri("/api/v1/auth/session", UriKind.Relative), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(TestContext.Current.CancellationToken))!;
    }

    public Task<HttpResponseMessage> PostAsync<T>(string path, T body) =>
        Http.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> PostAsync(string path) =>
        Http.PostAsync(new Uri(path, UriKind.Relative), content: null, TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> GetAsync(string path) =>
        Http.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    public async Task<HttpResponseMessage> LoginAsync(string userName, string password) =>
        await PostAsync("/api/v1/auth/login", new LoginRequest(userName, password));

    public void Dispose() => Http.Dispose();

    private sealed class AntiforgeryHeaderHandler(CookieContainer cookies, bool enabled) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (enabled && request.Method != HttpMethod.Get && request.RequestUri is not null)
            {
                var token = cookies.GetCookies(request.RequestUri)[AntiforgeryProtection.RequestTokenCookie]?.Value;
                if (token is not null)
                {
                    request.Headers.Add(AntiforgeryProtection.HeaderName, token);
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
