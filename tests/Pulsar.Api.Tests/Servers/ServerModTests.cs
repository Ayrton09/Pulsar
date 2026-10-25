using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Mods;

namespace Pulsar.Api.Tests.Servers;

/// <summary>Modrinth faked: its API (search, versions, projects, updates by hash) and its CDN's files.</summary>
public sealed class FakeModrinth : HttpMessageHandler
{
    public List<object> Hits { get; } = [];

    /// <summary>Each project's versions, newest first, as Modrinth's JSON.</summary>
    public Dictionary<string, List<JsonObject>> Versions { get; } = [];

    public Dictionary<string, string> Titles { get; } = [];

    /// <summary>The newest version for a file's SHA-512 (what version_files/update answers).</summary>
    public Dictionary<string, JsonObject> Latest { get; } = [];

    public Dictionary<string, byte[]> Files { get; } = [];

    public HttpStatusCode? Failure { get; set; }

    public List<string> Requests { get; } = [];

    /// <summary>A version of a project with one file (served by the CDN), for one game version and loader.</summary>
    public JsonObject Version(string project, string id, string number, byte[] file, string? requires = null, string loader = "fabric",
        string gameVersion = "26.3", string extension = "jar")
    {
        var url = $"https://cdn.modrinth.com/data/{project}/versions/{id}/{project}-{number}.{extension}";
        Files[url] = file;
        var version = new JsonObject
        {
            ["id"] = id,
            ["project_id"] = project,
            ["name"] = $"{project} {number}",
            ["version_number"] = number,
            ["version_type"] = "release",
            ["loaders"] = new JsonArray(loader),
            ["game_versions"] = new JsonArray(gameVersion),
            ["date_published"] = "2026-10-20T10:00:00Z",
            ["files"] = new JsonArray(new JsonObject
            {
                ["url"] = url,
                ["filename"] = $"{project}-{number}.{extension}",
                ["hashes"] = new JsonObject { ["sha1"] = "0", ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(file)) },
                ["size"] = file.Length,
                ["primary"] = true,
            }),
            ["dependencies"] = requires is null
                ? new JsonArray()
                : new JsonArray(new JsonObject { ["project_id"] = requires, ["version_id"] = null, ["dependency_type"] = "required" }),
        };
        if (!Versions.TryGetValue(project, out var versions))
        {
            Versions[project] = versions = [];
        }

        versions.Insert(0, version);
        return version;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add($"{request.Method} {uri} {body}");
        }

        if (uri.Host == "cdn.modrinth.com")
        {
            return Files.TryGetValue(uri.OriginalString, out var file)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (Failure is { } failure)
        {
            return new HttpResponseMessage(failure);
        }

        var path = uri.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        object? answer = path switch
        {
            "/v2/search" => new { hits = Hits, offset = 0, limit = 20, total_hits = Hits.Count },
            "/v2/projects" => JsonSerializer.Deserialize<string[]>(query["ids"]!)!.Where(Titles.ContainsKey)
                .Select(id => new { id, slug = id, title = Titles[id] }),
            "/v2/version_files/update" => JsonNode.Parse(body)!["hashes"]!.AsArray().Select(h => h!.GetValue<string>()).Where(Latest.ContainsKey)
                .ToDictionary(h => h, h => Latest[h]),
            _ when path.StartsWith("/v2/project/", StringComparison.Ordinal) && path.EndsWith("/version", StringComparison.Ordinal) =>
                Versions.GetValueOrDefault(path.Split('/')[3]) is { } versions
                    ? versions.Where(v => v["loaders"]!.AsArray().Any(l => JsonSerializer.Deserialize<string[]>(query["loaders"]!)!.Contains(l!.GetValue<string>()))
                        && (query["game_versions"] is not { } wanted
                            || v["game_versions"]!.AsArray().Any(g => JsonSerializer.Deserialize<string[]>(wanted)!.Contains(g!.GetValue<string>()))))
                    : null,
            _ when path.StartsWith("/v2/version/", StringComparison.Ordinal) =>
                Versions.Values.SelectMany(v => v).FirstOrDefault(v => v["id"]!.GetValue<string>() == path.Split('/')[3]),
            _ => null,
        };
        return answer is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json") };
    }
}

public sealed class ModrinthApiFactory : PulsarApiFactory
{
    public FakeModrinth Modrinth { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(ModrinthClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => Modrinth));
    }
}

/// <summary>
/// Mods from Modrinth (Pulsar 1.0.9) through the panel and the real agent core, on the test game set to take Fabric mods:
/// searched for the server's type and game version, installed with what they require, updated and removed, each file
/// checked against Modrinth's SHA-512 by the node.
/// </summary>
public sealed class ServerModTests(ModrinthApiFactory factory) : IClassFixture<ModrinthApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ModsAreFoundInstalledWithWhatTheyRequireUpdatedAndRemoved()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "flow");
        var server = await InstalledAsync(admin, agent, "Mods");

        // The test game takes nothing from Modrinth until its type says what.
        var view = await ModsAsync(admin, server);
        Assert.Equal((true, ModErrors.TypeUnsupported.Code), (view.Supported, view.Unavailable));
        await SetAsync(admin, server, "fabric");
        view = await ModsAsync(admin, server);
        Assert.Equal(("mod", "mods", "26.3", null), (view.ProjectType, view.Folder, view.GameVersion, view.Unavailable));
        Assert.Equal(["fabric"], view.Loaders);

        var lithium = Encoding.UTF8.GetBytes("lithium 0.18.1");
        var fabricApi = Encoding.UTF8.GetBytes("fabric api");
        factory.Modrinth.Version("fabric-api", "fapi1", "0.130.0", fabricApi);
        factory.Modrinth.Version("lithium", "lith1", "0.18.1", lithium, requires: "fabric-api");
        factory.Modrinth.Titles["lithium"] = "Lithium";
        factory.Modrinth.Titles["fabric-api"] = "Fabric API";
        factory.Modrinth.Hits.Add(new
        {
            project_id = "lithium",
            slug = "lithium",
            title = "Lithium",
            description = "Optimizes‮ everything",
            author = "jellysquid",
            downloads = 1000,
            date_modified = "2026-10-20T10:00:00Z"
        });

        var found = await SearchAsync(admin, server, "lith");
        var hit = Assert.Single(found.Hits);
        Assert.Equal(("Lithium", "Optimizes everything", false), (hit.Title, hit.Description, hit.Installed)); // no direction override from its author
        var search = factory.Modrinth.Requests.Last(r => r.Contains("/v2/search", StringComparison.Ordinal));
        foreach (var facet in new[] { "project_type:mod", "categories:fabric", "versions:26.3", "server_side:required" })
        {
            Assert.Contains(Uri.EscapeDataString(facet), search, StringComparison.Ordinal);
        }

        // Installed with Fabric API, which it requires; both checked by the node against Modrinth's SHA-512.
        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("lithium", null)));
        view = await SettledAsync(admin, server, mods => mods.Count == 2);
        var lithiumMod = view.Installed.Single(m => m.ProjectId == "lithium");
        var apiMod = view.Installed.Single(m => m.ProjectId == "fabric-api");
        Assert.Equal(("Lithium", "0.18.1", "mods/lithium-0.18.1.jar", false), (lithiumMod.Name, lithiumMod.VersionNumber, lithiumMod.Path, lithiumMod.Dependency));
        Assert.Equal(("Fabric API", true), (apiMod.Name, apiMod.Dependency));
        Assert.Equal(["Lithium"], apiMod.RequiredBy);
        var mods = Path.Combine(agent.ServersRoot, (await ServerApi.GetAsync(admin, server))!.Server.ShortId, "mods");
        Assert.Equal(lithium, await File.ReadAllBytesAsync(Path.Combine(mods, "lithium-0.18.1.jar"), Ct));
        Assert.Equal(fabricApi, await File.ReadAllBytesAsync(Path.Combine(mods, "fabric-api-0.130.0.jar"), Ct));
        Assert.True((await SearchAsync(admin, server, "lith")).Hits[0].Installed);

        await ProblemAsync(await admin.DeleteAsync($"/api/v1/servers/{server}/mods/{apiMod.Id}"), HttpStatusCode.Conflict, "mods.required_by");

        // A newer Lithium: found by its file's SHA-512, put in place of the old one.
        var newer = Encoding.UTF8.GetBytes("lithium 0.18.2");
        factory.Modrinth.Latest[Convert.ToHexStringLower(SHA512.HashData(lithium))] = factory.Modrinth.Version("lithium", "lith2", "0.18.2", newer, requires: "fabric-api");
        var updates = await GetAsync<ModUpdateList>(admin, $"/api/v1/servers/{server}/mods/updates");
        Assert.Equal(("Lithium", "0.18.1", "0.18.2"), (Assert.Single(updates.Updates).Name, updates.Updates[0].VersionNumber, updates.Updates[0].NewVersionNumber));
        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/mods/update", new UpdateModsRequest(null)));
        view = await SettledAsync(admin, server, list => list.Any(m => m.VersionNumber == "0.18.2"));
        Assert.Equal(2, view.Installed.Count);
        Assert.Equal(newer, await File.ReadAllBytesAsync(Path.Combine(mods, "lithium-0.18.2.jar"), Ct));
        Assert.False(File.Exists(Path.Combine(mods, "lithium-0.18.1.jar")));

        // Removed: Lithium, then what it needed.
        await ChangeAsync(admin.DeleteAsync($"/api/v1/servers/{server}/mods/{view.Installed.Single(m => m.ProjectId == "lithium").Id}"));
        view = await SettledAsync(admin, server, list => list.Count == 1);
        await ChangeAsync(admin.DeleteAsync($"/api/v1/servers/{server}/mods/{view.Installed.Single().Id}"));
        await SettledAsync(admin, server, list => list.Count == 0);
        Assert.Empty(Directory.EnumerateFiles(mods));

        var audit = await AuditAsync(server);
        Assert.Contains(audit, a => a.Action == AuditActions.Server.ModsInstallRequested && a.ActorType == AuditActorType.User
            && a.Metadata.Contains("lithium-0.18.1.jar", StringComparison.Ordinal));
        Assert.Contains(audit, a => a.Action == AuditActions.Server.ModsUpdateRequested);
        Assert.Equal(2, audit.Count(a => a.Action == AuditActions.Server.ModsRemoveRequested));
    }

    [DatabaseFact]
    public async Task AFileThatIsNotModrinthsChangesNothing()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "tampered");
        var server = await InstalledAsync(admin, agent, "Tampered");
        await SetAsync(admin, server, "paper");
        var version = factory.Modrinth.Version("luckperms", "lp1", "5.5.17", Encoding.UTF8.GetBytes("luckperms"), loader: "paper");
        factory.Modrinth.Files[version["files"]![0]!["url"]!.GetValue<string>()] = Encoding.UTF8.GetBytes("tampered");

        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("luckperms", "lp1")));

        await SettledAsync(admin, server, list => list.Count == 0);
        var details = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, "the change ended");
        Assert.Contains(details.RecentOperations, o => o.Type == OperationType.ModsChange && o.Status == OperationStatus.Failed
            && o.ErrorCode == "mods.checksum_mismatch");
        Assert.False(Directory.Exists(Path.Combine(agent.ServersRoot, details.Server.ShortId, "plugins"))
            && Directory.EnumerateFiles(Path.Combine(agent.ServersRoot, details.Server.ShortId, "plugins")).Any());
    }

    [DatabaseFact]
    public async Task ChangingModsNeedsTheRightToInstallAndAStoppedServer()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "rights");
        var server = await InstalledAsync(admin, agent, "Rights");
        await SetAsync(admin, server, "fabric");
        factory.Modrinth.Version("sodium", "sod1", "0.6.0", Encoding.UTF8.GetBytes("sodium"));

        using var viewer = await scenario.SignedInWithAsync("viewer", Permissions.Server.View);
        Assert.Equal("mod", (await ModsAsync(viewer, server)).ProjectType);
        using (var refused = await viewer.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("sodium", null)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("sodium", null)), HttpStatusCode.Conflict,
            ServerErrors.MustBeStopped.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("nothing-for-it", null)), HttpStatusCode.BadRequest,
            ModErrors.NoVersion.Code);
    }

    [DatabaseFact]
    public async Task ACatalogThatIsBusyOrDownSaysSo()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "down");
        var server = await InstalledAsync(admin, agent, "Down");
        await SetAsync(admin, server, "fabric");
        try
        {
            factory.Modrinth.Failure = HttpStatusCode.TooManyRequests;
            await ProblemAsync(await admin.GetAsync($"/api/v1/servers/{server}/mods/search?query=x"), HttpStatusCode.ServiceUnavailable, ModErrors.CatalogBusy.Code);
            factory.Modrinth.Failure = HttpStatusCode.InternalServerError;
            await ProblemAsync(await admin.GetAsync($"/api/v1/servers/{server}/mods/search?query=x"), HttpStatusCode.BadGateway, "mods.catalog_unavailable");
        }
        finally
        {
            factory.Modrinth.Failure = null;
        }
    }

    [DatabaseFact]
    public async Task AModpackSetsTheServersTypeAndVersionsAndPutsItsFilesOnIt()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "pack");
        var server = await InstalledAsync(admin, agent, "Pack");

        // Whatever the server's type: a pack sets it.
        var view = await ModsAsync(admin, server);
        Assert.Equal((true, (ModpackView?)null, ModErrors.TypeUnsupported.Code), (view.Modpacks, view.Modpack, view.Unavailable));

        var lithium = Encoding.UTF8.GetBytes("lithium in a pack");
        const string LithiumUrl = "https://cdn.modrinth.com/data/lithium/versions/inpack/lithium.jar";
        factory.Modrinth.Files[LithiumUrl] = lithium;
        factory.Modrinth.Version("test-pack", "tp1", "1.0", Mrpack("26.3", "0.17.2", ("mods/lithium.jar", LithiumUrl, lithium)), extension: "mrpack");
        factory.Modrinth.Titles["test-pack"] = "Test Pack";
        factory.Modrinth.Hits.Add(new
        {
            project_id = "test-pack",
            slug = "test-pack",
            title = "Test Pack",
            description = "A pack",
            author = "someone",
            downloads = 10,
            date_modified = "2026-10-20T10:00:00Z"
        });

        var found = await GetAsync<ModSearchView>(admin, $"/api/v1/servers/{server}/modpacks/search?query=pack");
        Assert.Equal("Test Pack", found.Hits.Single(h => h.ProjectId == "test-pack").Title);
        var search = factory.Modrinth.Requests.Last(r => r.Contains("/v2/search", StringComparison.Ordinal));
        Assert.Contains(Uri.EscapeDataString("project_type:modpack"), search, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("categories:fabric"), search, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString("versions:"), search, StringComparison.Ordinal); // any game version: the pack sets it

        var versions = await GetAsync<ModpackVersionList>(admin, $"/api/v1/servers/{server}/modpacks/projects/test-pack/versions");
        Assert.Equal(("fabric", "26.3"), (Assert.Single(versions.Versions).ServerType, versions.Versions[0].GameVersion));

        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/modpacks", new InstallModRequest("test-pack", null)));
        view = await PackSettledAsync(admin, server);
        var pack = view.Modpack!;
        Assert.Equal(("Test Pack", "1.0", "fabric", "fabric", "26.3", "0.17.2"),
            (pack.Name, pack.VersionNumber, pack.ServerType, pack.Loader, pack.GameVersion, pack.LoaderVersion));
        Assert.Equal(("mod", "26.3", (string?)null), (view.ProjectType, view.GameVersion, view.Unavailable)); // the server is the pack's type now

        var folder = Path.Combine(agent.ServersRoot, (await ServerApi.GetAsync(admin, server))!.Server.ShortId);
        Assert.Equal(lithium, await File.ReadAllBytesAsync(Path.Combine(folder, "mods", "lithium.jar"), Ct));
        Assert.Equal("from the pack", await File.ReadAllTextAsync(Path.Combine(folder, "config", "pack.txt"), Ct));
        Assert.Equal(new Dictionary<string, string?> { ["modType"] = "fabric", ["gameVersion"] = "26.3", ["loaderVersion"] = "0.17.2" },
            await VariablesAsync(server, "modType", "gameVersion", "loaderVersion"));
        Assert.Contains(await AuditAsync(server), a => a.Action == AuditActions.Server.ModpackInstallRequested
            && a.Metadata.Contains("test-pack", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task APackThatIsNotWhatTheCatalogSaysChangesNothing()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "packfail");
        var server = await InstalledAsync(admin, agent, "Pack fail");

        // The catalog says 26.3; the pack's index, another version.
        factory.Modrinth.Version("odd-pack", "op1", "1.0", Mrpack("26.2", "0.17.2"), extension: "mrpack");
        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/modpacks", new InstallModRequest("odd-pack", null)));

        var view = await PackSettledAsync(admin, server, installed: false);
        Assert.Null(view.Modpack);
        var details = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, "the install ended");
        Assert.Contains(details.RecentOperations, o => o.Type == OperationType.ModpackInstall && o.Status == OperationStatus.Failed
            && o.ErrorCode == "modpack.mismatch");
        Assert.Equal(new Dictionary<string, string?> { ["modType"] = null, ["gameVersion"] = null, ["loaderVersion"] = null },
            await VariablesAsync(server, "modType", "gameVersion", "loaderVersion"));

        // A version is installed only as its own project's.
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/modpacks", new InstallModRequest("test-pack", "op1")), HttpStatusCode.BadRequest,
            ModpackErrors.NoVersion.Code);

        // Packs for a loader none of the game's types runs are not offered at all.
        factory.Modrinth.Version("quilt-pack", "qp1", "1.0", Mrpack("26.3", "0.29.0"), loader: "quilt", extension: "mrpack");
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/modpacks", new InstallModRequest("quilt-pack", "qp1")), HttpStatusCode.BadRequest,
            ModpackErrors.LoaderUnsupported.Code);
    }

    /// <summary>A .mrpack for Fabric on <paramref name="minecraft"/>, its files on Modrinth's CDN, with an override.</summary>
    private static byte[] Mrpack(string minecraft, string loader, params (string Path, string Url, byte[] Content)[] files)
    {
        var index = new JsonObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = "1.0",
            ["name"] = "Test Pack",
            ["files"] = new JsonArray([.. files.Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.Path,
                ["hashes"] = new JsonObject { ["sha1"] = new string('0', 40), ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(f.Content)) },
                ["env"] = new JsonObject { ["client"] = "required", ["server"] = "required" },
                ["downloads"] = new JsonArray(f.Url),
                ["fileSize"] = f.Content.Length,
            })]),
            ["dependencies"] = new JsonObject { ["minecraft"] = minecraft, ["fabric-loader"] = loader },
        };
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
            {
                writer.Write(index.ToJsonString());
            }

            using (var writer = new StreamWriter(zip.CreateEntry("overrides/config/pack.txt").Open()))
            {
                writer.Write("from the pack");
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Waits until no pack is being installed, and one is (or is not) installed.</summary>
    private static async Task<ModsView> PackSettledAsync(PanelClient client, Guid server, bool installed = true)
    {
        ModsView? view = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            view = await ModsAsync(client, server);
            var details = await ServerApi.GetAsync(client, server);
            return view.ModpackInstalling is null && (view.Modpack is not null) == installed && details!.Server.ActiveOperation is null;
        }, () => $"modpack settled (last: {view?.Modpack?.Name ?? "none"}, installing {view?.ModpackInstalling?.Name ?? "none"})");
        return view!;
    }

    private async Task<Dictionary<string, string?>> VariablesAsync(Guid server, params string[] keys)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Servers.AsNoTracking().Include(s => s.Variables)
            .SingleAsync(s => s.Id == new ServerId(server), Ct);
        return keys.ToDictionary(k => k, k => stored.Variables.FirstOrDefault(v => v.Key == k)?.Value);
    }

    private static async Task ChangeAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        await AccessScenario.EnsureSuccessAsync(response);
    }

    /// <summary>Waits until no change is running and the installed mods match.</summary>
    private static async Task<ModsView> SettledAsync(PanelClient client, Guid server, Func<IReadOnlyList<ServerModView>, bool> expected)
    {
        ModsView? view = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            view = await ModsAsync(client, server);
            return view.Installed.All(m => m.State == ServerModState.Installed) && expected(view.Installed);
        }, () => $"mods settled (last: {string.Join(", ", view?.Installed.Select(m => $"{m.ProjectId} {m.VersionNumber} {m.State}") ?? [])})");
        return view!;
    }

    private static Task<ModsView> ModsAsync(PanelClient client, Guid server) => GetAsync<ModsView>(client, $"/api/v1/servers/{server}/mods");

    private static Task<ModSearchView> SearchAsync(PanelClient client, Guid server, string query) =>
        GetAsync<ModSearchView>(client, $"/api/v1/servers/{server}/mods/search?query={Uri.EscapeDataString(query)}");

    private static async Task<T> GetAsync<T>(PanelClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(AccessScenario.Json, Ct))!;
    }

    private static async Task SetAsync(PanelClient client, Guid server, string modType)
    {
        using var response = await client.PutAsync($"/api/v1/servers/{server}/settings",
            new UpdateServerSettingsRequest(new Dictionary<string, string?> { ["modType"] = modType }, [], AutoStart: false, RestartOnCrash: false));
        await AccessScenario.EnsureSuccessAsync(response);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        }
    }

    private async Task<List<AuditEntry>> AuditAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.ServerId == new ServerId(server)).ToListAsync(Ct);
    }

    private static async Task<Guid> InstalledAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"mods-{name}");
        var agent = new InProcessAgent(factory) { Http = factory.Modrinth };
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }
}
