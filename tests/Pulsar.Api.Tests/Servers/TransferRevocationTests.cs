using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Agent.Core.Panel;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Security;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// A file transfer is a request that may last as long as bytes move: what ends a user's sessions, or revokes an API key,
/// ends their transfers too (real Kestrel, uploads that trickle in), and nothing they sent after that lands on the node.
/// </summary>
public sealed class TransferRevocationTests(KestrelPanelFactory factory) : IClassFixture<KestrelPanelFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task DisablingAUserOrRevokingAKeyEndsTheirTransfers()
    {
        factory.StartServer();
        using var admin = await AdminAsync();
        var created = await factory.CreateNodeAsync("revoked-transfers");
        await using var agent = new InProcessAgent(factory, transport: new TrustingTransport(factory));
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint, factory.MainUrl.ToString());
        await agent.StartAsync();
        var node = new NodeId(config.NodeId);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == factory.AgentsUrl
            && factory.Services.GetRequiredService<Pulsar.Application.Nodes.IAgentConnections>().Find(node) is not null), "the agent connected");
        var server = await InstalledServerAsync(admin, config.NodeId);
        var transfers = factory.Services.GetRequiredService<FileTransfers>();

        using var user = await UserAsync(admin, "trickling-user");
        using var keyCreated = await admin.Http.PostAsJsonAsync("api/v1/account/api-keys", new CreateApiKeyRequest("trickling key", null, null), AccessScenario.Json, Ct);
        keyCreated.EnsureSuccessStatusCode();
        var key = (await keyCreated.Content.ReadFromJsonAsync<CreatedApiKey>(AccessScenario.Json, Ct))!;
        using var script = new HttpClient(factory.Handler()) { BaseAddress = factory.MainUrl, Timeout = TimeSpan.FromMinutes(5) };
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Value);

        using var stop = new CancellationTokenSource();
        var byUser = TrickleAsync(user.Http, server, "user.bin", stop.Token);
        var byKey = TrickleAsync(script, server, "key.bin", stop.Token);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(transfers.Running(node) == 2), () => $"two uploads running ({transfers.Running(node)})");

        // Disabled: the user's upload ends, the key's (another user's) goes on.
        var users = await admin.Http.GetFromJsonAsync<List<UserResponse>>("api/v1/users", AccessScenario.Json, Ct);
        using (var disabled = await admin.Http.PutAsJsonAsync("api/v1/users/" + users!.Single(u => u.UserName == "trickling-user").Id,
            new UpdateUserRequest(null, null, true), AccessScenario.Json, Ct))
        {
            disabled.EnsureSuccessStatusCode();
        }

        Assert.NotEqual(HttpStatusCode.OK, await byUser.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(transfers.Running(node) == 1), () => $"the user's upload ended ({transfers.Running(node)})");
        Assert.False(byKey.IsCompleted);

        // The key revoked: its upload ends.
        using (var revoked = await admin.Http.DeleteAsync("api/v1/account/api-keys/" + key.Key.Id, Ct))
        {
            revoked.EnsureSuccessStatusCode();
        }

        Assert.NotEqual(HttpStatusCode.OK, await byKey.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(transfers.Running(node) == 0), () => $"the key's upload ended ({transfers.Running(node)})");
        await stop.CancelAsync();
        var listing = await admin.Http.GetStringAsync($"api/v1/servers/{server}/files", Ct);
        Assert.DoesNotContain("user.bin", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("key.bin", listing, StringComparison.Ordinal);
    }

    /// <summary>An upload that sends a kilobyte every 100 ms (well above Kestrel's least data rate): its answer, or 0 when the connection broke.</summary>
    private static Task<HttpStatusCode> TrickleAsync(HttpClient http, Guid server, string name, CancellationToken stop) => Task.Run(async () =>
    {
        try
        {
            using var content = new DripContent(50_000_000, stop);
            using var response = await http.PutAsync($"api/v1/servers/{server}/files/content?path={name}", content, stop);
            return response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return (HttpStatusCode)0;
        }
    }, Ct);

    private static async Task<Guid> InstalledServerAsync(Client admin, Guid node)
    {
        var templates = await admin.Http.GetFromJsonAsync<List<TemplateView>>("api/v1/templates", AccessScenario.Json, Ct);
        using var made = await admin.Http.PostAsJsonAsync("api/v1/servers",
            new CreateServerRequest(node, templates!.Single(t => t.IsTestGame).Id, "revoked-transfers", null), AccessScenario.Json, Ct);
        made.EnsureSuccessStatusCode();
        var server = (await made.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            var details = await admin.Http.GetFromJsonAsync<ServerDetails>("api/v1/servers/" + server, AccessScenario.Json, Ct);
            return details!.Server.InstallState == InstallState.Installed && details.Server.ActiveOperation is null;
        }, "the server installed");
        return server;
    }

    private async Task<Client> AdminAsync()
    {
        var admin = new Client(factory);
        await admin.SessionAsync();
        var token = factory.Services.GetRequiredService<InitialSetup>().IssueToken();
        using (var setup = await admin.Http.PostAsJsonAsync("api/v1/auth/setup",
            new SetupRequest(token, PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword, "Administrator", null), Ct))
        {
            setup.EnsureSuccessStatusCode();
        }

        await admin.SignInAsync(PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword);
        return admin;
    }

    private async Task<Client> UserAsync(Client admin, string name)
    {
        using var role = await admin.Http.PostAsJsonAsync("api/v1/roles", new CreateRoleRequest(name + "-role", null, PermissionScope.Global,
            [Permissions.Server.View, Permissions.Server.FilesView, Permissions.Server.FilesEdit]), AccessScenario.Json, Ct);
        role.EnsureSuccessStatusCode();
        var summary = (await role.Content.ReadFromJsonAsync<RoleSummary>(AccessScenario.Json, Ct))!;
        using (var made = await admin.Http.PostAsJsonAsync("api/v1/users", new CreateUserRequest(name, AccessScenario.Password, null, null, [summary.Id]),
            AccessScenario.Json, Ct))
        {
            made.EnsureSuccessStatusCode();
        }

        var client = new Client(factory);
        await client.SessionAsync();
        await client.SignInAsync(name, AccessScenario.Password);
        return client;
    }

    /// <summary>The agent's real transport (sockets, TLS, WebSockets), trusting the test panel's certificate.</summary>
    private sealed class TrustingTransport(KestrelPanelFactory panel) : PanelTransport
    {
        public override HttpMessageHandler CreateHttpHandler() => panel.Handler();
    }

    /// <summary>A browser over real TLS: cookies and the antiforgery header.</summary>
    private sealed class Client : IDisposable
    {
        private static readonly PanelCookies s_cookies = new(secure: true);
        private readonly CookieContainer _jar = new();

        public Client(KestrelPanelFactory panel)
        {
            var handler = panel.Handler();
            handler.UseCookies = true;
            handler.CookieContainer = _jar;
            Http = new HttpClient(new Antiforgery(_jar, handler), disposeHandler: true) { BaseAddress = panel.MainUrl, Timeout = TimeSpan.FromMinutes(5) };
        }

        public HttpClient Http { get; }

        public async Task SessionAsync()
        {
            using var session = await Http.GetAsync("api/v1/auth/session", Ct);
        }

        public async Task SignInAsync(string name, string password)
        {
            using (var login = await Http.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(name, password), Ct))
            {
                login.EnsureSuccessStatusCode();
            }

            await SessionAsync();
        }

        public void Dispose() => Http.Dispose();

        private sealed class Antiforgery(CookieContainer jar, HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method != HttpMethod.Get && request.RequestUri is not null
                    && jar.GetCookies(request.RequestUri)[s_cookies.AntiforgeryRequestToken]?.Value is { } token)
                {
                    request.Headers.Add(AntiforgeryProtection.HeaderName, token);
                }

                return base.SendAsync(request, cancellationToken);
            }
        }
    }

    /// <summary>A request body that moves a kilobyte every 100 ms for as long as it wants.</summary>
    private sealed class DripContent(long length, CancellationToken stop) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[1024];
            long sent = 0;
            while (sent < length)
            {
                await stream.WriteAsync(chunk, stop);
                await stream.FlushAsync(stop);
                sent += chunk.Length;
                await Task.Delay(100, stop);
            }
        }

        protected override bool TryComputeLength(out long length1)
        {
            length1 = length;
            return true;
        }
    }
}
