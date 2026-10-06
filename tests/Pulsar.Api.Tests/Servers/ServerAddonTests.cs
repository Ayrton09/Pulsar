using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Addons through the panel and the real agent core, with the bundled Counter-Strike 2 and Rust templates and a fake
/// GitHub: what needs what, what excludes what, the files the agent unpacks, the line Metamod needs at every start, and
/// removing them again.
/// </summary>
public sealed class ServerAddonTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string GameInfo = "\"GameInfo\"\n{\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tGame_LowViolence\tcsgo_lv // Perfect World content override\n\t\t\tGame\tcsgo\n\t\t}\n\t}\n}\n";

    [DatabaseFact]
    public async Task AddonsAreInstalledInOrderKeptAtEveryStartAndRemoved()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var github = new FakeGitHub();
        github.Release("alliedmodders/metamod-source/releases?per_page=10", "2.0.0.1473", "mmsource-2.0.0-git1473-windows.zip", list: true,
            ("addons/metamod.vdf", "\"Plugin\" {}"), ("addons/metamod/bin/win64/metamod.2.cs2.dll", "metamod"));
        github.Release("roflmuffin/CounterStrikeSharp/releases/latest", "v1.0.376", "counterstrikesharp-with-runtime-windows-1.0.376.zip", list: false,
            ("addons/counterstrikesharp/bin/win64/counterstrikesharp.dll", "cssharp"), ("addons/metamod/counterstrikesharp.vdf", "\"Metamod Plugin\" {}"));
        await using var agent = await ConnectedAgentAsync(admin, github);
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27030");
        var server = await InstalledAsync(admin, agent, "cs2");
        var root = Path.Combine(agent.ServersRoot, (await ServerApi.GetAsync(admin, server))!.Server.ShortId);
        var gameInfo = Path.Combine(root, "game", "csgo", "gameinfo.gi");
        Directory.CreateDirectory(Path.GetDirectoryName(gameInfo)!);
        await File.WriteAllTextAsync(gameInfo, GameInfo, Ct);

        Assert.True((await ServerApi.GetAsync(admin, server))!.HasAddons);
        // Whoever imports a template sees where its addons come from.
        Assert.Equal(["github.com/alliedmodders/metamod-source", "github.com/roflmuffin/CounterStrikeSharp"],
            (await GameServerTests.TemplateAsync(admin, "cs2")).Capabilities.AddonSources);
        var listed = await ListAsync(admin, server);
        Assert.Equal(["metamod", "counterstrikesharp"], listed.Select(a => a.Key));
        Assert.All(listed, a => Assert.Null(a.InstalledRelease));
        Assert.Equal(["metamod"], listed[1].Requires);

        // CounterStrikeSharp runs on Metamod.
        Assert.Equal("addon.requires", await ProblemAsync(admin.PostAsync($"/api/v1/servers/{server}/addons/counterstrikesharp"), HttpStatusCode.Conflict));
        await InstallAsync(admin, server, "metamod", "2.0.0.1473");
        await InstallAsync(admin, server, "counterstrikesharp", "v1.0.376");
        Assert.Equal("cssharp", await File.ReadAllTextAsync(Path.Combine(root, "game", "csgo", "addons", "counterstrikesharp", "bin", "win64", "counterstrikesharp.dll"), Ct));
        Assert.Equal("addon.required_by", await ProblemAsync(admin.DeleteAsync($"/api/v1/servers/{server}/addons/metamod"), HttpStatusCode.Conflict));
        Assert.Equal("addon.not_found", await ProblemAsync(admin.PostAsync($"/api/v1/servers/{server}/addons/sourcemod"), HttpStatusCode.NotFound));

        // The game's update would take Metamod's line out of gameinfo.gi: it goes back in before every start.
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        Assert.Contains("\t\t\tGame_LowViolence\tcsgo_lv // Perfect World content override\n\t\t\tGame\tcsgo/addons/metamod\n\t\t\tGame\tcsgo\n",
            await File.ReadAllTextAsync(gameInfo, Ct), StringComparison.Ordinal);
        Assert.Equal("server.must_be_stopped", await ProblemAsync(admin.DeleteAsync($"/api/v1/servers/{server}/addons/counterstrikesharp"), HttpStatusCode.Conflict));
        await ServerApi.CommandAsync(admin, server, "stop");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped");

        // An update brought the game's own gameinfo.gi back: the next start puts the line in again.
        await File.WriteAllTextAsync(gameInfo, GameInfo, Ct);
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running again");
        Assert.Contains("\tGame\tcsgo/addons/metamod\n", await File.ReadAllTextAsync(gameInfo, Ct), StringComparison.Ordinal);
        await ServerApi.CommandAsync(admin, server, "stop");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped again");

        await RemoveAsync(admin, server, "counterstrikesharp");
        await RemoveAsync(admin, server, "metamod");
        Assert.False(Directory.Exists(Path.Combine(root, "game", "csgo", "addons", "metamod")));
        Assert.Equal(GameInfo, await File.ReadAllTextAsync(gameInfo, Ct));
    }

    [DatabaseFact]
    public async Task AddonsThatExcludeEachOtherAreNotInstalledTogether()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var github = new FakeGitHub();
        github.Release("OxideMod/Oxide.Rust/releases/latest", "2.0.7801", "Oxide.Rust.zip", list: false,
            ("RustDedicated_Data/Managed/Oxide.Core.dll", "oxide"));
        await using var agent = await ConnectedAgentAsync(admin, github);
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "28015-28030");
        var server = await InstalledAsync(admin, agent, "rust");

        await InstallAsync(admin, server, "oxide", "2.0.7801");
        Assert.Equal("addon.conflicts", await ProblemAsync(admin.PostAsync($"/api/v1/servers/{server}/addons/carbon"), HttpStatusCode.Conflict));
        Assert.Single(github.Requested, u => u.EndsWith("/Oxide.Rust.zip", StringComparison.Ordinal));

        // A game update overwrites Oxide's files: the same operation installs its latest release again.
        github.Release("OxideMod/Oxide.Rust/releases/latest", "2.0.7900", "Oxide.Rust.zip", list: false,
            ("RustDedicated_Data/Managed/Oxide.Core.dll", "oxide, newer"));
        using (var update = await admin.PostAsync($"/api/v1/servers/{server}/update", new UpdateGameRequest(Validate: false)))
        {
            await AccessScenario.EnsureSuccessAsync(update);
        }

        var updated = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null && s.RecentOperations[0].Type == OperationType.ServerUpdate,
            "updated");
        Assert.Equal(OperationStatus.Succeeded, updated.RecentOperations[0].Status);
        Assert.Equal(2, github.Requested.Count(u => u.EndsWith("/Oxide.Rust.zip", StringComparison.Ordinal)));
        Assert.Equal("2.0.7900", (await ListAsync(admin, server)).Single(a => a.Key == "oxide").InstalledRelease);
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, FakeGitHub github)
    {
        var created = await NodeApi.CreateAsync(admin, $"addons-{Guid.NewGuid():N}"[..20]);
        var agent = new InProcessAgent(factory) { Http = github };
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    private static async Task<Guid> InstalledAsync(PanelClient admin, InProcessAgent agent, string game)
    {
        var template = await GameServerTests.TemplateAsync(admin, game);
        using var response = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(agent.Config!.NodeId, template.Template.Id, game, null));
        await AccessScenario.EnsureSuccessAsync(response);
        var created = (await response.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!;
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }

    private static async Task<List<AddonView>> ListAsync(PanelClient admin, Guid server) =>
        (await admin.Http.GetFromJsonAsync<List<AddonView>>(new Uri($"/api/v1/servers/{server}/addons", UriKind.Relative), AccessScenario.Json, Ct))!;

    private static async Task InstallAsync(PanelClient admin, Guid server, string key, string release)
    {
        using (var response = await admin.PostAsync($"/api/v1/servers/{server}/addons/{key}"))
        {
            await AccessScenario.EnsureSuccessAsync(response);
        }

        await WaitForAddonAsync(admin, server, key, release);
    }

    private static async Task RemoveAsync(PanelClient admin, Guid server, string key)
    {
        using (var response = await admin.DeleteAsync($"/api/v1/servers/{server}/addons/{key}"))
        {
            await AccessScenario.EnsureSuccessAsync(response);
        }

        await WaitForAddonAsync(admin, server, key, release: null);
    }

    private static async Task WaitForAddonAsync(PanelClient admin, Guid server, string key, string? release)
    {
        var done = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, $"{key} operation done");
        Assert.Equal(OperationStatus.Succeeded, done.RecentOperations[0].Status);
        Assert.Equal(release, (await ListAsync(admin, server)).Single(a => a.Key == key).InstalledRelease);
    }

    private static async Task<string?> ProblemAsync(Task<HttpResponseMessage> request, HttpStatusCode status)
    {
        using var response = await request;
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return problem.RootElement.GetProperty("code").GetString();
    }

    /// <summary>GitHub's API, its download links and the asset host they redirect to.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

        public List<string> Requested { get; } = [];

        public void Release(string api, string tag, string asset, bool list, params (string Path, string Content)[] files)
        {
            var zip = Zip(files);
            var download = $"https://github.com/{api.Split('/')[0]}/{api.Split('/')[1]}/releases/download/{tag}/{asset}";
            var stored = $"https://release-assets.githubusercontent.com/{Guid.NewGuid():N}";
            object release = new
            {
                tag_name = tag,
                draft = false,
                assets = new[] { new { name = asset, browser_download_url = download, digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(zip))}" } },
            };
            var body = JsonSerializer.SerializeToUtf8Bytes(list ? new[] { release } : release);
            _routes[$"https://api.github.com/repos/{api}"] = () => Ok(body);
            _routes[download] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(stored) } };
            _routes[stored] = () => Ok(zip);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.OriginalString;
            lock (Requested)
            {
                Requested.Add(url);
            }

            return Task.FromResult(_routes.TryGetValue(url, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        private static byte[] Zip((string Path, string Content)[] files)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in files)
                {
                    using var entry = zip.CreateEntry(path).Open();
                    entry.Write(Encoding.UTF8.GetBytes(content));
                }
            }

            return buffer.ToArray();
        }
    }
}
