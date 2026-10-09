using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>Steam accounts for SteamCMD, Steam Guard, and the login the agent keeps for each account (Phase 13).</summary>
public sealed class SteamAccountTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private const string Password = "hunter2-Steam!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AccountsAreForSettingsManagersAndNeverShowTheirPassword()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;

        var account = await CreateAccountAsync(admin, "Servers account", "pulsar_servers");
        var listed = await admin.Http.GetStringAsync("/api/v1/steam-accounts", Ct);
        Assert.Contains("pulsar_servers", listed, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, listed, StringComparison.Ordinal);

        // A password SteamCMD's command line would split is refused; leaving it out keeps the current one.
        using (var spaced = await admin.PutAsync($"/api/v1/steam-accounts/{account.Id}", new SteamAccountRequest("Servers account", "pulsar_servers", "two words")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, spaced.StatusCode);
        }

        // SteamCMD would read a password starting with + or - as a command or an option.
        foreach (var password in new[] { "+quit", "-beta" })
        {
            using var option = await admin.PutAsync($"/api/v1/steam-accounts/{account.Id}", new SteamAccountRequest("Servers account", "pulsar_servers", password));
            Assert.Equal(HttpStatusCode.BadRequest, option.StatusCode);
        }

        var renamed = await PutAsync<SteamAccountView>(admin, $"/api/v1/steam-accounts/{account.Id}", new SteamAccountRequest("Renamed", "pulsar_servers", null));
        Assert.Equal("Renamed", renamed.Name);

        // Names are unique: a name another account has is refused, with a code the page can show.
        var other = await CreateAccountAsync(admin, "Other account", "pulsar_other");
        using (var taken = await admin.PostAsync("/api/v1/steam-accounts", new SteamAccountRequest(" Renamed ", "pulsar_third", Password)))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Contains("steam_account.name_taken", await taken.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var takenByUpdate = await admin.PutAsync($"/api/v1/steam-accounts/{other.Id}", new SteamAccountRequest("Renamed", "pulsar_other", null)))
        {
            Assert.Equal(HttpStatusCode.Conflict, takenByUpdate.StatusCode);
            Assert.Contains("steam_account.name_taken", await takenByUpdate.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Equal("Other", (await PutAsync<SteamAccountView>(admin, $"/api/v1/steam-accounts/{other.Id}", new SteamAccountRequest("Other", "pulsar_other", null))).Name);
        Assert.Equal("Renamed", (await PutAsync<SteamAccountView>(admin, $"/api/v1/steam-accounts/{account.Id}", new SteamAccountRequest("Renamed", "pulsar_servers", null))).Name);
        using (var otherDeleted = await admin.DeleteAsync($"/api/v1/steam-accounts/{other.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, otherDeleted.StatusCode);
        }

        using var operatorClient = await scenario.SignedInWithAsync("steam-operator", Permissions.Server.View, Permissions.Server.Install);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.Http.GetAsync("/api/v1/steam-accounts", Ct)).StatusCode);
        // Whoever chooses a server's account sees the accounts, and changes none.
        using var host = await scenario.SignedInWithAsync("steam-host", Permissions.Server.View, Permissions.Server.LimitsEdit);
        Assert.Equal(HttpStatusCode.OK, (await host.Http.GetAsync("/api/v1/steam-accounts", Ct)).StatusCode);
        using (var create = await host.PostAsync("/api/v1/steam-accounts", new SteamAccountRequest("Mine", "my_account", Password)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var deleted = await admin.DeleteAsync($"/api/v1/steam-accounts/{account.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task AServerSignsInAskingForSteamGuardOnceThenWithItsCachedLogin()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "guard");
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await GameServerTests.InstalledAsync(admin, agent, "css");
        var account = await CreateAccountAsync(admin, "Guarded", "pulsar_guarded");

        // Choosing whose account downloads the game is for whoever may change the server's caps.
        using var operatorClient = await scenario.SignedInWithAsync("steam-server-operator", Permissions.Server.View, Permissions.Server.Install);
        using (var refused = await operatorClient.PutAsync($"/api/v1/servers/{server}/steam-account", new ServerSteamAccountRequest(account.Id)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var chosen = await PutAsync<ServerDetails>(admin, $"/api/v1/servers/{server}/steam-account", new ServerSteamAccountRequest(account.Id));
        Assert.Equal(new ServerSteamView(false, account.Id, "Guarded"), chosen.Steam);

        // Steam asks for a code: the install fails saying so, with nothing left behind.
        agent.Processes.SteamGuardCode = "F7K2Q";
        await PostAsync(admin, $"/api/v1/servers/{server}/install");
        var failed = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null && s.RecentOperations.Any(o =>
            o.Type == OperationType.ServerInstall && o.Status == OperationStatus.Failed), "install refused");
        Assert.Equal("steamcmd.guard_required", failed.RecentOperations.First(o => o.Type == OperationType.ServerInstall).ErrorCode);
        Assert.Null(agent.SteamLogins.Load(new Pulsar.Contracts.SteamLogin(account.Id, "pulsar_guarded", Password)));
        Assert.False(File.Exists(CachedLogin(agent, failed)));

        // With the code it signs in, and the agent keeps the login SteamCMD cached, out of the server's folder.
        using (var invalid = await admin.PostAsync($"/api/v1/servers/{server}/install/steam-guard", new InstallRequest("not a code")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        await PostAsync(admin, $"/api/v1/servers/{server}/install/steam-guard", new InstallRequest("f7k2q"));
        var installed = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null && s.RecentOperations.First(o =>
            o.Type == OperationType.ServerInstall).Status == OperationStatus.Succeeded, "installed with the code");
        Assert.NotNull(agent.SteamLogins.Load(new Pulsar.Contracts.SteamLogin(account.Id, "pulsar_guarded", Password)));
        Assert.False(File.Exists(CachedLogin(agent, installed)));

        // The next update needs neither the password nor a code.
        await PostAsync(admin, $"/api/v1/servers/{server}/update", new UpdateGameRequest(false));
        await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null && s.RecentOperations[0].Type == OperationType.ServerUpdate
            && s.RecentOperations[0].Status == OperationStatus.Succeeded, "updated");
        Assert.Equal(["anonymous", "user+password", "user+password+code", "user"], agent.Processes.SteamSignIns);

        // An account in use cannot go; back to anonymous, it can.
        using (var inUse = await admin.DeleteAsync($"/api/v1/steam-accounts/{account.Id}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        }

        var anonymous = await PutAsync<ServerDetails>(admin, $"/api/v1/servers/{server}/steam-account", new ServerSteamAccountRequest(null));
        Assert.Null(anonymous.Steam!.AccountId);
    }

    [DatabaseFact]
    public async Task AGameOnlyAnAccountMayDownloadIsNotInstalledWithoutOne()
    {
        var admin = (await AccessScenario.CreateAsync(factory)).Admin;
        await using var agent = await ConnectedAgentAsync(admin, "required");
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27020");
        var template = await ImportAsync(admin, Manifest(m =>
        {
            m["key"] = "css-owned";
            m["name"] = "CS:S (owned)";
            m["install"]!["steps"]![0]!["login"] = "account";
        }));

        // Created without an account: its install fails saying why, before anything reaches the node.
        using var created = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(agent.Config.NodeId, template, "owned", null));
        await AccessScenario.EnsureSuccessAsync(created);
        var server = (await created.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
        var failed = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null && s.RecentOperations.Any(o =>
            o.Type == OperationType.ServerInstall), "install refused");
        Assert.Equal("steamcmd.account_required", failed.RecentOperations.First(o => o.Type == OperationType.ServerInstall).ErrorCode);
        Assert.True(failed.Steam!.Required);
        Assert.DoesNotContain("anonymous", agent.Processes.SteamSignIns);
        using (var reinstall = await admin.PostAsync($"/api/v1/servers/{server}/install"))
        {
            Assert.Equal(HttpStatusCode.Conflict, reinstall.StatusCode);
        }

        // With an account it installs.
        var account = await CreateAccountAsync(admin, "Owner", "pulsar_owner");
        await PutAsync<ServerDetails>(admin, $"/api/v1/servers/{server}/steam-account", new ServerSteamAccountRequest(account.Id));
        await PostAsync(admin, $"/api/v1/servers/{server}/install");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        Assert.Equal(["user+password"], agent.Processes.SteamSignIns);
    }

    private static string CachedLogin(InProcessAgent agent, ServerDetails server) =>
        Path.Combine(agent.ServersRoot, server.Server.ShortId, ".pulsar", "steamcmd", "config", "config.vdf");

    private static string Manifest(Action<JsonObject> change)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "games", "css", "manifest.json")))!.AsObject();
        change(manifest);
        return manifest.ToJsonString();
    }

    private static async Task<Guid> ImportAsync(PanelClient admin, string manifest)
    {
        using var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(manifest));
        await AccessScenario.EnsureSuccessAsync(imported);
        return (await imported.Content.ReadFromJsonAsync<TemplateDetails>(AccessScenario.Json, Ct))!.Template.Id;
    }

    private static async Task<SteamAccountView> CreateAccountAsync(PanelClient admin, string name, string userName)
    {
        using var response = await admin.PostAsync("/api/v1/steam-accounts", new SteamAccountRequest(name, userName, Password));
        await AccessScenario.EnsureSuccessAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SteamAccountView>(AccessScenario.Json, Ct))!;
    }

    private static async Task PostAsync(PanelClient client, string path, object? body = null)
    {
        using var response = body is null ? await client.PostAsync(path) : await client.PostAsync(path, body);
        await AccessScenario.EnsureSuccessAsync(response);
    }

    private static async Task<T> PutAsync<T>(PanelClient client, string path, object body)
    {
        using var response = await client.PutAsync(path, body);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(AccessScenario.Json, Ct))!;
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"steam-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }
}
