using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>Importing a manifest is installing software on every node (docs/architecture/04): reviewed first, privileged.</summary>
public sealed class TemplateImportTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AManifestIsPreviewedImportedAndNeverReplaced()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var manifest = Manifest(m => m["key"] = "css-community");

        var preview = await PreviewAsync(admin, manifest);
        Assert.True(preview.Valid);
        Assert.False(preview.AlreadyImported);
        Assert.Null(preview.PreviousVersion);
        Assert.Equal("srcds.exe", preview.Details!.Capabilities.Executable);
        Assert.Equal([232330], preview.Details.Capabilities.SteamAppIds);
        Assert.Equal(["cstrike/cfg/server.cfg"], preview.Details.Capabilities.ConfigFiles);

        using (var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(manifest)))
        {
            Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
            var details = (await imported.Content.ReadFromJsonAsync<TemplateDetails>(AccessScenario.Json, Ct))!;
            Assert.Equal(Pulsar.Domain.Servers.TemplateSource.Imported, details.Template.Source);
        }

        using (var again = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(manifest.Replace("Pulsar CS:S", "Changed", StringComparison.Ordinal))))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // same key and version: templates never change
        }

        var bundled = (string)JsonNode.Parse(File.ReadAllText(GamePath("css")))!["version"]!;
        var next = Manifest(m =>
        {
            m["key"] = "css-community";
            m["version"] = GameServerTests.NextVersion(bundled);
            m["runtime"]!["windows-x64"]!["executable"] = "bin/srcds_x64.exe";
        });
        var nextPreview = await PreviewAsync(admin, next);
        Assert.Equal(bundled, nextPreview.PreviousVersion);
        Assert.Equal(["runtime"], nextPreview.ChangedSections);

        var templates = await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct);
        Assert.Contains(templates!, t => t.Key == "css-community" && t.Version == bundled);
    }

    [DatabaseFact]
    public async Task InvalidOrReservedManifestsAreRefused()
    {
        using var admin = await factory.CreateAdminClientAsync();

        var invalid = await PreviewAsync(admin, Manifest(m => m["install"] = JsonNode.Parse("""{"steps": [{"type": "run", "command": "cmd"}]}""")));
        Assert.False(invalid.Valid);
        Assert.NotEmpty(invalid.Errors);

        using var reserved = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(File.ReadAllText(GamePath("_test"))));
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        Assert.Contains("template.invalid", await reserved.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task ATemplateThisPanelNoLongerAcceptsIsRefusedNotAFailure()
    {
        using var admin = await factory.CreateAdminClientAsync();
        Guid template;
        using (var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(Manifest(m => m["key"] = "css-stricter"))))
        {
            Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
            template = (await imported.Content.ReadFromJsonAsync<TemplateDetails>(AccessScenario.Json, Ct))!.Template.Id;
        }

        var node = await NodeApi.CreateAsync(admin, "stricter-checks");
        Guid server;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            // Valid when imported; this panel's checks refuse it now (as a later, stricter validator would).
            var refused = Manifest(m =>
            {
                m["key"] = "css-stricter";
                m["install"] = JsonNode.Parse("""{"steps": [{"type": "run", "command": "cmd"}]}""");
            });
            await db.GameTemplates.Where(t => t.Id == new TemplateId(template)).ExecuteUpdateAsync(s => s.SetProperty(t => t.Content, refused), Ct);
            var row = Server.Create(new NodeId(node.Node.Id), new TemplateId(template), "Stricter", null, Server.NewShortId(), DateTimeOffset.UtcNow).Value;
            db.Servers.Add(row);
            await db.SaveChangesAsync(Ct);
            server = row.Id.Value;
        }

        foreach (var path in new[] { $"/api/v1/templates/{template}", $"/api/v1/servers/{server}/settings" })
        {
            using var response = await admin.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{path}: {(int)response.StatusCode} {body}");
            Assert.Contains("server.template_invalid", body, StringComparison.Ordinal);
        }
    }

    [DatabaseFact]
    public async Task OnlyTemplateManagersCanImport()
    {
        var access = await AccessScenario.CreateAsync(factory);
        using var creator = await access.SignedInWithAsync("creator", Permissions.Server.Create);

        using var preview = await creator.PostAsync("/api/v1/templates/preview", new TemplateContentRequest(Manifest(_ => { })));
        using var import = await creator.PostAsync("/api/v1/templates", new TemplateContentRequest(Manifest(_ => { })));

        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, import.StatusCode);
    }

    [DatabaseFact]
    public async Task AGameWithJsonSettingsGoesOnlyToAgentsThatWriteThem()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var manifest = Manifest(m =>
        {
            m["key"] = "css-json";
            m["configFiles"] = JsonNode.Parse("""
                [{ "path": "cstrike/cfg/pulsar.json", "format": "json", "mode": "patch", "set": { "/server/name": "{{var.hostname}}", "/server/slots": "{{var.maxPlayers}}" } }]
                """);
        });
        using (var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(manifest)))
        {
            Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        }

        var template = (await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct))!
            .Single(t => t.Key == "css-json");

        // An agent of 1.0.5 or 1.0.6 reads the game settings but not JSON files: it would refuse the manifest at the start.
        foreach (var (name, capabilities, refused) in new[]
        {
            ("json-old", new[] { AgentCapabilities.ProvisionServer, AgentCapabilities.GameSettings }, true),
            ("json-new", new[] { AgentCapabilities.ProvisionServer, AgentCapabilities.GameSettings, AgentCapabilities.MoreConfigFormats }, false),
        })
        {
            var node = await NodeApi.CreateAsync(admin, name);
            await using var agent = new TestAgent(factory);
            await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
            await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken, capabilities);

            using var response = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(node.Node.Id, template.Id, "Json", null));

            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.Equal(refused, body.Contains("server.template_unsupported", StringComparison.Ordinal));
        }
    }

    private static async Task<TemplateImportPreview> PreviewAsync(PanelClient client, string content)
    {
        using var response = await client.PostAsync("/api/v1/templates/preview", new TemplateContentRequest(content));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<TemplateImportPreview>(AccessScenario.Json, Ct))!;
    }

    private static string Manifest(Action<JsonObject> change)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(GamePath("css")))!.AsObject();
        change(manifest);
        return manifest.ToJsonString();
    }

    private static string GamePath(string game)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "games", game, "manifest.json");
    }
}
