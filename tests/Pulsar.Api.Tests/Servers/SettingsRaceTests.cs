using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Servers;

/// <summary>The agent can report a state change of the server while its settings are being saved: they are saved on the fresh row.</summary>
public sealed class SettingsRaceTests(SettingsRaceTests.RacingFactory factory) : IClassFixture<SettingsRaceTests.RacingFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task SettingsSavedWhileTheServerRowChangesAreKept()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "settings-race");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        var created = await ServerApi.CreateAsync(admin, node.Node.Id, "Settings race");
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");

        factory.Racing.Armed = true;
        using var saved = await admin.PutAsync($"/api/v1/servers/{created.Server.Id}/settings",
            new UpdateServerSettingsRequest(new Dictionary<string, string?>(), [], AutoStart: true, RestartOnCrash: false));
        await AccessScenario.EnsureSuccessAsync(saved);

        Assert.False(factory.Racing.Armed); // the race did happen
        var settings = (await saved.Content.ReadFromJsonAsync<ServerSettings>(AccessScenario.Json, Ct))!;
        Assert.True(settings.AutoStart);
        Assert.False(settings.RestartOnCrash);
        Assert.True((await ServerApi.GetAsync(admin, created.Server.Id))!.AutoStart);
    }

    /// <summary>The panel, with a change to the server's row slipped in just before its settings are saved.</summary>
    public sealed class RacingFactory : PulsarApiFactory
    {
        public RacingInterceptor Racing { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.ConfigureDbContext<PulsarDbContext>(options => options.AddInterceptors(Racing)));
        }
    }

    public sealed class RacingInterceptor : SaveChangesInterceptor
    {
        private int _armed;

        public bool Armed
        {
            get => Volatile.Read(ref _armed) == 1;
            set => Volatile.Write(ref _armed, value ? 1 : 0);
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);
            if (eventData.Context is { } context
                && context.ChangeTracker.Entries<AuditEntry>().Any(e => e.Entity.Action == AuditActions.Server.SettingsChanged && e.State == EntityState.Added)
                && context.ChangeTracker.Entries<Server>().FirstOrDefault(e => e.State == EntityState.Modified) is { } saving
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                // As if the agent had just reported a state change of the server.
                await context.Database.ExecuteSqlAsync($"UPDATE servers SET updated_at = updated_at WHERE id = {saving.Entity.Id.Value}", cancellationToken);
            }

            return result;
        }
    }
}
