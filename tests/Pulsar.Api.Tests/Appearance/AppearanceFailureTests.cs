using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pulsar.Api.Appearance;
using Pulsar.Api.Security;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Appearance;

namespace Pulsar.Api.Tests.Appearance;

/// <summary>
/// The Steam artwork setting when reading or saving it fails (docs/architecture/08, T41): the policy in memory never
/// allows Steam on a value the database does not hold, and is read again by the next <c>GET /api/v1/appearance</c>.
/// Each test runs on another panel on the same database, whose database work goes through a <see cref="DatabaseHook"/>.
/// </summary>
public sealed class AppearanceFailureTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private const string Table = "appearance_settings";

    private static readonly Uri s_appearance = new("/api/v1/appearance", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AFailedFirstReadIsMadeAgainByTheNextRead()
    {
        await StoreAsync(steamArtwork: true);
        var hook = new DatabaseHook();
        hook.AfterQuery(Table, () => throw new TimeoutException());
        await using var app = factory.WithDatabaseHook(hook);

        // The panel starts all the same, without Steam until it has read the setting.
        using var visitor = Visitor(app);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithoutSteamArtwork, await PolicyAsync(visitor));

        using var client = await SignInAsync(app);
        using (var read = await client.Http.GetAsync(s_appearance, Ct))
        {
            Assert.True((await ReadAsync(read)).SteamArtwork);
            Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithSteamArtwork, PolicyOf(read));
        }

        Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithSteamArtwork, await PolicyAsync(visitor));
    }

    [DatabaseFact]
    public async Task AChangeThatCommitsAndThenFailsIsTheOneInEffect()
    {
        await StoreAsync(steamArtwork: true);
        var hook = new DatabaseHook();
        await using var app = factory.WithDatabaseHook(hook);
        using var client = await SignInAsync(app);
        await using var page = await BrowserHub.ConnectAsync(app, client);
        Assert.True(await SteamArtworkAsync(client));

        // The change commits, then its call fails (a request given up as the answer came back).
        hook.AfterSave(c => c.ChangeTracker.Entries<AppearanceSettings>().Any(), () => throw new OperationCanceledException());
        using (var failed = await client.PutAsync("/api/v1/appearance", new UpdateAppearanceSettingsRequest(false)))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithoutSteamArtwork, PolicyOf(failed));
        }

        // What is stored is in effect, and open pages are told to read it again.
        Assert.False(await SteamArtworkAsync(client));
        using var visitor = Visitor(app);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithoutSteamArtwork, await PolicyAsync(visitor));
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(page.AppearanceChanged == 1), "the open page hears of the change");
    }

    [DatabaseFact]
    public async Task AReadMadeBeforeAChangeNeverOverridesIt()
    {
        await StoreAsync(steamArtwork: true);
        var hook = new DatabaseHook();
        hook.AfterQuery(Table, () => throw new TimeoutException());
        await using var app = factory.WithDatabaseHook(hook);
        using var client = await SignInAsync(app);

        // Not read yet (it failed at startup). A read finds it on; before that read is applied, a change to off commits, its
        // call fails, and reading it again fails too: the outcome is unknown.
        hook.AfterQuery(Table, async () =>
        {
            hook.AfterSave(c => c.ChangeTracker.Entries<AppearanceSettings>().Any(), () =>
            {
                hook.AfterQuery(Table, () => throw new TimeoutException());
                throw new OperationCanceledException();
            });
            using var change = await client.PutAsync("/api/v1/appearance", new UpdateAppearanceSettingsRequest(false));
            Assert.Equal(HttpStatusCode.InternalServerError, change.StatusCode);
        });
        using (var read = await client.Http.GetAsync(s_appearance, Ct))
        {
            // The read from before the change is dropped: still unknown, so off.
            Assert.False((await ReadAsync(read)).SteamArtwork);
            Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithoutSteamArtwork, PolicyOf(read));
        }

        // The next read finds what the change stored.
        Assert.False(await SteamArtworkAsync(client));
        using var visitor = Visitor(app);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithoutSteamArtwork, await PolicyAsync(visitor));
    }

    /// <summary>Stores the setting through the fixture's own panel (the database is shared).</summary>
    private async Task StoreAsync(bool steamArtwork)
    {
        using var admin = await factory.CreateAdminClientAsync();
        using var response = await admin.PutAsync("/api/v1/appearance", new UpdateAppearanceSettingsRequest(steamArtwork));
        await AccessScenario.EnsureSuccessAsync(response);
    }

    private static async Task<PanelClient> SignInAsync(WebApplicationFactory<Program> app)
    {
        var client = await PanelClient.CreateAsync(app);
        using var login = await client.LoginAsync(PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword);
        await AccessScenario.EnsureSuccessAsync(login);
        await client.GetSessionAsync();
        return client;
    }

    private static HttpClient Visitor(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task<bool> SteamArtworkAsync(PanelClient client)
    {
        using var response = await client.Http.GetAsync(s_appearance, Ct);
        return (await ReadAsync(response)).SteamArtwork;
    }

    private static async Task<AppearanceSettingsResponse> ReadAsync(HttpResponseMessage response)
    {
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AppearanceSettingsResponse>(AccessScenario.Json, Ct))!;
    }

    private static async Task<string> PolicyAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
        return PolicyOf(response);
    }

    private static string PolicyOf(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
}
