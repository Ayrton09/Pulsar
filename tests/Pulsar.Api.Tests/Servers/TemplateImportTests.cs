using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;

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

        var next = Manifest(m =>
        {
            m["key"] = "css-community";
            m["version"] = "1.1.0";
            m["runtime"]!["windows-x64"]!["executable"] = "bin/srcds_x64.exe";
        });
        var nextPreview = await PreviewAsync(admin, next);
        Assert.Equal("1.0.0", nextPreview.PreviousVersion);
        Assert.Equal(["runtime"], nextPreview.ChangedSections);

        var templates = await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct);
        Assert.Contains(templates!, t => t.Key == "css-community" && t.Version == "1.0.0");
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
    public async Task OnlyTemplateManagersCanImport()
    {
        var access = await AccessScenario.CreateAsync(factory);
        using var creator = await access.SignedInWithAsync("creator", Permissions.Server.Create);

        using var preview = await creator.PostAsync("/api/v1/templates/preview", new TemplateContentRequest(Manifest(_ => { })));
        using var import = await creator.PostAsync("/api/v1/templates", new TemplateContentRequest(Manifest(_ => { })));

        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, import.StatusCode);
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
