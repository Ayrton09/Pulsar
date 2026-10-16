using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Sftp;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Servers;
using Renci.SshNet;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>The panel with its SFTP server listening on a free loopback port.</summary>
public class SftpApiFactory : PulsarApiFactory
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    protected virtual void ConfigureSftp(SftpOptions options)
    {
    }

    /// <summary>The port it listens on now (it changes when the settings restart it).</summary>
    public async Task<int> PortAsync()
    {
        var host = Services.GetRequiredService<SftpHost>();
        int? port = null;
        await InProcessAgent.EventuallyAsync(() => Task.FromResult((port = host.ListeningPort) is not null), "SFTP listening");
        return port!.Value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.PostConfigure<SftpOptions>(options =>
        {
            options.Listen = true;
            options.BindAddress = "127.0.0.1";
            options.PortOverride = 0;
            ConfigureSftp(options);
        }));
    }
}

/// <summary>Clients, users and servers for the SFTP tests.</summary>
internal static class SftpTesting
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An SSH.NET client signing in by keyboard-interactive: <paramref name="password"/> to the password prompt,
    /// <paramref name="code"/> to the code prompt; every prompt is added to <paramref name="prompts"/>.
    /// </summary>
    public static SftpClient Client(int port, string login, string password, string? code = null, List<string>? prompts = null,
        Action<string>? fingerprint = null)
    {
        var interactive = new KeyboardInteractiveAuthenticationMethod(login);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompts?.Add(prompt.Request);
                prompt.Response = prompt.Request.StartsWith("Password", StringComparison.Ordinal) ? password : code ?? string.Empty;
            }
        };
        return Client(new ConnectionInfo("127.0.0.1", port, login, interactive) { Timeout = SftpApiFactory.Timeout }, fingerprint);
    }

    /// <summary>An SSH.NET client signing in with the "password" method only.</summary>
    public static SftpClient PasswordClient(int port, string login, string password) =>
        Client(new ConnectionInfo("127.0.0.1", port, login, new PasswordAuthenticationMethod(login, password)) { Timeout = SftpApiFactory.Timeout }, null);

    public static async Task<SftpClient> ConnectedAsync(int port, string login, string password = AccessScenario.Password)
    {
        var client = Client(port, login, password);
        try
        {
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The sign-in fails (wrong credentials, a refused user, a limit).</summary>
    public static async Task<Exception> FailsAsync(SftpClient client)
    {
        using (client)
        {
            var failure = await Record.ExceptionAsync(() => client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct));
            Assert.NotNull(failure);
            Assert.IsNotType<TimeoutException>(failure);
            return failure;
        }
    }

    public static Task<T> Run<T>(Func<T> action) => Task.Run(action, Ct).WaitAsync(SftpApiFactory.Timeout, Ct);

    public static Task Run(Action action) => Task.Run(action, Ct).WaitAsync(SftpApiFactory.Timeout, Ct);

    public static async Task<RoleSummary> ServerRoleAsync(AccessScenario scenario, string name, params string[] permissions)
    {
        using var response = await scenario.Admin.PostAsync("/api/v1/roles", new CreateRoleRequest(name, null, PermissionScope.Server, permissions));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<RoleSummary>(AccessScenario.Json, Ct))!;
    }

    public static async Task GrantAsync(PanelClient admin, Guid user, Guid server, Guid role)
    {
        using var response = await admin.PutAsync($"/api/v1/users/{user}/servers/{server}/roles/{role}", new { });
        await AccessScenario.EnsureSuccessAsync(response);
    }

    public static async Task<InProcessAgent> ConnectedAgentAsync(PulsarApiFactory factory, PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, name);
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    /// <summary>An installed server: its id, short id, folder in the SFTP root and directory on the node.</summary>
    public static async Task<(Guid Id, string ShortId, string Folder, string Directory)> InstalledAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return (created.Server.Id, created.Server.ShortId, SftpFileSystem.FolderName(name, created.Server.ShortId),
            Path.Combine(agent.ServersRoot, created.Server.ShortId));
    }

    /// <summary>A raw SSH session (Microsoft.DevTunnels.Ssh's client), signed in with the "password" method.</summary>
    public static async Task<(SshClientSession Session, System.Net.Sockets.TcpClient Tcp)> RawSessionAsync(int port, string login, string password)
    {
        var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, Ct);
        var session = new SshClientSession(SshSessionConfiguration.Default, new System.Diagnostics.TraceSource("sftp-tests", System.Diagnostics.SourceLevels.Off));
        session.Authenticating += (_, e) =>
        {
            if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey)
            {
                e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
            }
        };
        await session.ConnectAsync(tcp.GetStream(), Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
        Assert.True(await session.AuthenticateAsync(new SshClientCredentials(login, password)).WaitAsync(SftpApiFactory.Timeout, Ct));
        return (session, tcp);
    }

    private static SftpClient Client(ConnectionInfo info, Action<string>? fingerprint)
    {
        var client = new SftpClient(info) { OperationTimeout = SftpApiFactory.Timeout };
        client.HostKeyReceived += (_, e) =>
        {
            fingerprint?.Invoke("SHA256:" + e.FingerPrintSHA256);
            e.CanTrust = true;
        };
        return client;
    }
}
