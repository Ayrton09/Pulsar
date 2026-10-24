using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Realtime;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Phase 5 through the panel and the real agent core: a browser watches a server's console and usage over the live
/// channel, types commands, and loses nothing when it, or the agent, reconnects.
/// </summary>
public sealed class LiveConsoleTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ABrowserSeesTheConsoleAndItsCommandsAreRunAndAudited()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "console");
        var server = await RunningServerAsync(admin, agent, "Console");
        await using var browser = await BrowserHub.ConnectAsync(factory, admin);

        var snapshot = (await browser.Connection.InvokeAsync<ConsoleSnapshot?>("WatchConsole", server, null, Ct))!;
        Assert.True(snapshot.NodeConnected);
        // Output from before anyone watched (the agent's backlog) arrives too.
        await EventuallyAsync(() => BrowserHub.Text([.. snapshot.Entries, .. browser.Console]).Contains("Test server ready", StringComparison.Ordinal),
            () => BrowserHub.Text(browser.Console));

        var sent = await SendAsync(admin, server, "say hello there");
        Assert.Equal(AckStatus.Accepted, sent.Status);
        await EventuallyAsync(() => BrowserHub.Text(browser.Console).Contains("Test server: hello there", StringComparison.Ordinal),
            () => BrowserHub.Text(browser.Console));

        await using var scope = factory.Services.CreateAsyncScope();
        var audited = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.Action == AuditActions.Server.ConsoleCommand && e.ServerId == new ServerId(server)).ToListAsync(Ct);
        using var metadata = JsonDocument.Parse(Assert.Single(audited).Metadata);
        Assert.Equal("say hello there", metadata.RootElement.GetProperty("command").GetString());
    }

    [DatabaseFact]
    public async Task ABrowserWatchingFromTheCreateDialogSeesTheInstall()
    {
        // The create dialog opens the console at once: maybe before the node knows the server, which it refuses then.
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "install-console");
        await using var browser = await BrowserHub.ConnectAsync(factory, admin);
        using var hold = new ManualResetEventSlim(false);
        agent.Provisioner.HoldProvision = hold;
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, "Install console");

        var snapshot = (await browser.Connection.InvokeAsync<ConsoleSnapshot?>("WatchConsole", created.Server.Id, null, Ct))!;
        await Task.Delay(200, Ct); // the subscription is refused meanwhile
        hold.Set();

        await EventuallyAsync(
            () => BrowserHub.Text([.. snapshot.Entries, .. browser.Console]).Contains("[Pulsar] Installing the game: done", StringComparison.Ordinal),
            () => BrowserHub.Text([.. snapshot.Entries, .. browser.Console]));
        Assert.Contains("[Pulsar] Installing the game…", BrowserHub.Text([.. snapshot.Entries, .. browser.Console]), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task ABrowserThatReconnectsGetsWhatItMissedAndNothingTwice()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "reconnect");
        var server = await RunningServerAsync(admin, agent, "Reconnect");
        long last;
        await using (var first = await BrowserHub.ConnectAsync(factory, admin))
        {
            var snapshot = (await first.Connection.InvokeAsync<ConsoleSnapshot?>("WatchConsole", server, null, Ct))!;
            await SendAsync(admin, server, "say before");
            await EventuallyAsync(() => BrowserHub.Text([.. snapshot.Entries, .. first.Console]).Contains("before", StringComparison.Ordinal),
                () => BrowserHub.Text(first.Console));
            last = snapshot.Entries.Concat(first.Console).Max(e => e.Sequence);
        }

        await SendAsync(admin, server, "say while away");
        await Task.Delay(300, Ct);
        await using var second = await BrowserHub.ConnectAsync(factory, admin);
        var missed = (await second.Connection.InvokeAsync<ConsoleSnapshot?>("WatchConsole", server, last, Ct))!;

        var text = BrowserHub.Text(missed.Entries);
        Assert.Contains("Test server: while away", text, StringComparison.Ordinal);
        Assert.DoesNotContain("before", text, StringComparison.Ordinal);
        Assert.All(missed.Entries, e => Assert.True(e.Sequence > last));
    }

    [DatabaseFact]
    public async Task OutputWhileTheAgentWasDisconnectedArrivesAfterItsReconnectWithoutGaps()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "agent-reconnect");
        var server = await RunningServerAsync(admin, agent, "Agent reconnect");
        await using var browser = await BrowserHub.ConnectAsync(factory, admin);
        var snapshot = (await browser.Connection.InvokeAsync<ConsoleSnapshot?>("WatchConsole", server, null, Ct))!;
        await EventuallyAsync(() => BrowserHub.Text([.. snapshot.Entries, .. browser.Console]).Contains("ready", StringComparison.Ordinal), () => "ready");

        factory.Services.GetRequiredService<IAgentConnections>().Disconnect(new NodeId(agent.Config!.NodeId));
        var game = agent.Processes.Started.Last(p => !p.IsSteamCmd);
        game.Print("printed while the agent was away\r\n");
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, agent.Config.NodeId)).Node.Connected, "agent connected again");
        game.Print("printed after\r\n");

        await EventuallyAsync(() => BrowserHub.Text(browser.Console).Contains("printed after", StringComparison.Ordinal), () => BrowserHub.Text(browser.Console));
        var all = snapshot.Entries.Concat(browser.Console).ToList();
        Assert.Single(all, e => e.Text.Contains("printed while the agent was away", StringComparison.Ordinal));
        Assert.DoesNotContain(all, e => e.IsGap);
        Assert.Equal(all.Count, all.DistinctBy(e => e.Sequence).Count()); // the panel dropped the repeats
    }

    [DatabaseFact]
    public async Task UsageAndChangesArePushedWithoutPolling()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "usage");
        var server = await InstalledServerAsync(admin, agent, "Usage");
        await using var browser = await BrowserHub.ConnectAsync(factory, admin);
        Assert.True(await browser.Connection.InvokeAsync<bool>("WatchServers", Ct));
        Assert.Null((await browser.Connection.InvokeAsync<ServerWatch?>("WatchServer", server, Ct))!.Metrics); // not running

        await ServerApi.CommandAsync(admin, server, "start");

        await EventuallyAsync(() => browser.ServersChanged.Contains(server), () => "server change pushed");
        await EventuallyAsync(() => browser.Metrics.Any(m => m.ServerId == server), () => "usage pushed");
        var metrics = browser.Metrics.Last(m => m.ServerId == server);
        Assert.Equal(64 * 1024 * 1024, metrics.MemoryBytes);
        Assert.Equal(1, metrics.ProcessCount);
        Assert.NotNull((await browser.Connection.InvokeAsync<ServerWatch?>("WatchServer", server, Ct))!.Metrics); // what a page opened now gets
    }

    [DatabaseFact]
    public async Task CommandsMustBeSingleLinesForARunningServer()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "command-rules");
        var server = await InstalledServerAsync(admin, agent, "Rules");

        using var stopped = await admin.PostAsync($"/api/v1/servers/{server}/console", new ConsoleCommandRequest("status"));
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        Assert.Contains("server.not_running", await stopped.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        using var twoLines = await admin.PostAsync($"/api/v1/servers/{server}/console", new ConsoleCommandRequest("say a\nquit"));
        Assert.Equal(HttpStatusCode.BadRequest, twoLines.StatusCode);
        Assert.Contains("console.invalid_input", await twoLines.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task WatchingAndTypingNeedTheirPermissions()
    {
        var access = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(access.Admin, "console-permissions");
        var server = await RunningServerAsync(access.Admin, agent, "Permissions");
        using var viewer = await access.SignedInWithAsync("console-viewer", Permissions.Server.View);
        await using var browser = await BrowserHub.ConnectAsync(factory, viewer);

        Assert.Null(await browser.Connection.InvokeAsync<ConsoleSnapshot?>("WatchConsole", server, null, Ct));
        Assert.False(await browser.Connection.InvokeAsync<bool>("WatchNodes", Ct));
        Assert.NotNull(await browser.Connection.InvokeAsync<ServerWatch?>("WatchServer", server, Ct)); // seeing the server is allowed
        Assert.Null(await browser.Connection.InvokeAsync<ServerWatch?>("WatchServer", Guid.NewGuid(), Ct));
        using var typed = await viewer.PostAsync($"/api/v1/servers/{server}/console", new ConsoleCommandRequest("status"));
        Assert.Equal(HttpStatusCode.Forbidden, typed.StatusCode);
    }

    [DatabaseFact]
    public async Task AnotherSiteCannotOpenTheLiveChannel()
    {
        using var admin = await factory.CreateAdminClientAsync();

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => BrowserHub.ConnectAsync(factory, admin, origin: "https://evil.example"));
        Assert.True(BrowserHub.IsRefusal(refused), refused.ToString());
    }

    [DatabaseFact]
    public async Task TheLiveChannelNeedsASignedInUser()
    {
        using var anonymous = await PanelClient.CreateAsync(factory);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => BrowserHub.ConnectAsync(factory, anonymous));
        Assert.True(BrowserHub.IsRefusal(refused), refused.ToString());
    }

    [Theory]
    [InlineData("rcon_password hunter2", "rcon_password ***")]
    [InlineData("  RCON_PASSWORD \"hunter2\"", "rcon_password ***")]
    [InlineData("\"sv_password\" secret", "sv_password ***")]
    [InlineData("say rcon_password is not typed here", "say rcon_password is not typed here")]
    [InlineData("status", "status")]
    [InlineData("say hi; rcon_password hunter2", "say hi; rcon_password ***")]
    [InlineData("say hi;SV_PASSWORD x;status", "say hi;sv_password ***;status")]
    public void SensitiveCommandsAreAuditedWithoutTheirArguments(string line, string audited) =>
        Assert.Equal(audited, ConsoleCommands.Redact(line, ["rcon_password", "sv_password"]));

    private static async Task<AgentCommandResult> SendAsync(PanelClient client, Guid server, string command)
    {
        using var response = await client.PostAsync($"/api/v1/servers/{server}/console", new ConsoleCommandRequest(command));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AgentCommandResult>(AccessScenario.Json, Ct))!;
    }

    private static async Task EventuallyAsync(Func<bool> condition, Func<string> what) =>
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(condition()), what);

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"live-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    private static async Task<Guid> InstalledServerAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }

    private static async Task<Guid> RunningServerAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var server = await InstalledServerAsync(admin, agent, name);
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        return server;
    }

}
