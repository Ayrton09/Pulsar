using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Api.Nodes;
using Pulsar.Api.Notifications;
using Pulsar.Api.Tests.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Notifications;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Notifications;

/// <summary>Records messages instead of sending them; channels named "retry-…" or "broken-…" fail as Discord or SMTP would.</summary>
public sealed class FakeNotificationTransport(NotificationKind kind) : INotificationTransport
{
    public NotificationKind Kind => kind;

    public ConcurrentQueue<(string Channel, NotificationMessage Message)> Sent { get; } = [];

    public Task SendAsync(NotificationChannel channel, NotificationSettings settings, NotificationMessage message, CancellationToken cancellationToken)
    {
        if (channel.Name.StartsWith("retry-", StringComparison.Ordinal))
        {
            throw new NotificationSendException("Discord failed (503).", retryable: true);
        }

        if (channel.Name.StartsWith("broken-", StringComparison.Ordinal))
        {
            throw new NotificationSendException("The webhook does not exist any more (404).", retryable: false);
        }

        Sent.Enqueue((channel.Name, message));
        return Task.CompletedTask;
    }
}

public sealed class NotificationApiFactory : PulsarApiFactory
{
    public FakeNotificationTransport Discord { get; } = new(NotificationKind.Discord);

    public FakeNotificationTransport Email { get; } = new(NotificationKind.Email);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<INotificationTransport>();
            services.AddSingleton<INotificationTransport>(Discord);
            services.AddSingleton<INotificationTransport>(Email);
        });
    }
}

/// <summary>Notification channels, settings, and messages queued by events and sent with retries (Phase 10).</summary>
public sealed class NotificationTests(NotificationApiFactory factory) : IClassFixture<NotificationApiFactory>
{
    private const string Webhook = "https://discord.com/api/webhooks/123456789/secret-token_ABC";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ChannelsAreForSettingsManagersAndNeverShowTheirWebhook()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;

        var created = await CreateAsync(admin, Channel("ops", NotificationKind.Discord) with { WebhookUrl = Webhook });
        Assert.Equal("123456789", created.Webhook);
        Assert.Equal([NotificationEvent.ServerCrashed, NotificationEvent.NodeOffline], created.Events);
        Assert.Null(created.ServerIds);

        using (var list = await admin.GetAsync("/api/v1/notifications/channels"))
        {
            var body = await list.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("secret-token", body, StringComparison.Ordinal);
            Assert.Contains("123456789", body, StringComparison.Ordinal);
        }

        // Changed without a webhook: it keeps the one it has.
        using (var renamed = await admin.PutAsync($"/api/v1/notifications/channels/{created.Id}", Channel("ops renamed", NotificationKind.Discord)))
        {
            await AccessScenario.EnsureSuccessAsync(renamed);
            Assert.Equal("123456789", (await renamed.Content.ReadFromJsonAsync<NotificationChannelView>(AccessScenario.Json, Ct))!.Webhook);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().NotificationChannels.AsNoTracking()
                .SingleAsync(c => c.Id == new NotificationChannelId(created.Id), Ct);
            Assert.DoesNotContain("secret-token", Encoding.UTF8.GetString(stored.WebhookUrlProtected!), StringComparison.Ordinal); // encrypted
            Assert.Equal(Webhook, scope.ServiceProvider.GetRequiredService<INotificationSecretProtector>().Unprotect(stored.WebhookUrlProtected!));
        }

        using (var internalAddress = await admin.PostAsync("/api/v1/notifications/channels",
            Channel("bad", NotificationKind.Discord) with { WebhookUrl = "https://10.0.0.5/api/webhooks/1/x" }))
        {
            await SetupTests.AssertProblemAsync(internalAddress, HttpStatusCode.BadRequest, "notifications.webhook_invalid");
        }

        using (var unknownServer = await admin.PostAsync("/api/v1/notifications/channels",
            Channel("bad", NotificationKind.Email) with { Recipients = ["ops@example.com"], ServerIds = [Guid.NewGuid()] }))
        {
            await SetupTests.AssertProblemAsync(unknownServer, HttpStatusCode.BadRequest, "notifications.server_unknown");
        }

        using (var noRecipients = await admin.PostAsync("/api/v1/notifications/channels", Channel("bad", NotificationKind.Email)))
        {
            await SetupTests.AssertProblemAsync(noRecipients, HttpStatusCode.BadRequest, "notifications.recipients_invalid");
        }

        using var viewer = await scenario.SignedInWithAsync("notification-viewer", Permissions.Server.View, Permissions.Node.View);
        using (var forbidden = await viewer.GetAsync("/api/v1/notifications/channels"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var deleted = await admin.DeleteAsync($"/api/v1/notifications/channels/{created.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await using var audit = factory.Services.CreateAsyncScope();
        var actions = await audit.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.TargetId == created.Id.ToString()).Select(e => e.Action).ToListAsync(Ct);
        Assert.Equal(["notification.channel_created", "notification.channel_deleted", "notification.channel_updated"], actions.Order());
    }

    [DatabaseFact]
    public async Task TheSmtpPasswordGoesInButNeverComesOut()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var defaults = await GetJsonAsync<NotificationSettingsView>(admin, "/api/v1/notifications/settings");
        Assert.Equal(10, defaults.DiskLowPercent);
        Assert.Equal(3, defaults.NodeOfflineMinutes);
        Assert.Null(defaults.Smtp);

        var request = new NotificationSettingsRequest("https://panel.example.com:8443/", 15, 5, "smtp.example.com", 587, SmtpSecurity.StartTls,
            "pulsar", "smtp password 123", false, "pulsar@example.com", "Pulsar");
        using (var saved = await admin.PutAsync("/api/v1/notifications/settings", request))
        {
            await AccessScenario.EnsureSuccessAsync(saved);
            var body = await saved.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("smtp password 123", body, StringComparison.Ordinal);
        }

        var settings = await GetJsonAsync<NotificationSettingsView>(admin, "/api/v1/notifications/settings");
        Assert.Equal("https://panel.example.com:8443", settings.PanelUrl);
        Assert.True(settings.Smtp!.HasPassword);

        // Saving again without a password keeps it.
        using (var again = await admin.PutAsync("/api/v1/notifications/settings", request with { SmtpPassword = null }))
        {
            await AccessScenario.EnsureSuccessAsync(again);
        }

        Assert.True((await GetJsonAsync<NotificationSettingsView>(admin, "/api/v1/notifications/settings")).Smtp!.HasPassword);

        using var badFrom = await admin.PutAsync("/api/v1/notifications/settings", request with { FromAddress = "nobody" });
        await SetupTests.AssertProblemAsync(badFrom, HttpStatusCode.BadRequest, "notifications.from_invalid");

        // Back to the defaults for the other tests (no email, a 3-minute grace).
        using var off = await admin.PutAsync("/api/v1/notifications/settings",
            request with { PanelUrl = null, SmtpHost = null, DiskLowPercent = 10, NodeOfflineMinutes = 3 });
        await AccessScenario.EnsureSuccessAsync(off);
    }

    [DatabaseFact]
    public async Task ACrashIsToldOnceToTheChannelsThatWantIt()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "crash");
        var server = await InstalledServerAsync(admin, agent, "Crashy notified");
        var other = await InstalledServerAsync(admin, agent, "Someone else's");
        await CreateAsync(admin, Channel("crashes", NotificationKind.Discord) with { WebhookUrl = Webhook, Events = [NotificationEvent.ServerCrashed] });
        await CreateAsync(admin, Channel("only the other server", NotificationKind.Email) with
        {
            Recipients = ["ops@example.com"],
            Events = [NotificationEvent.ServerCrashed],
            ServerIds = [other],
        });

        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        agent.Processes.Started[^1].Exit(3);

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Discord.Sent.Any(m => m.Channel == "crashes")), "crash message sent");
        var (_, message) = factory.Discord.Sent.Single(m => m.Channel == "crashes");
        Assert.Equal("Crashy notified crashed", message.Title);
        Assert.Equal(NotificationSeverity.Error, message.Severity);
        Assert.Contains(message.Fields, f => f.Name == "Exit code" && f.Value == "3");
        Assert.Contains(message.Fields, f => f.Name == "Node" && f.Value == "notifications-crash");
        Assert.DoesNotContain(factory.Email.Sent, m => m.Channel == "only the other server");

        // It restarts and crashes again within the window: still one message.
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "restarted");
        agent.Processes.Started[^1].Exit(4);
        await ServerApi.WaitForAsync(admin, server, s => s.Server.LastExitCode == 4, "crashed again");
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Assert.Single(factory.Discord.Sent, m => m.Channel == "crashes");
    }

    [DatabaseFact]
    public async Task FailedDeliveriesAreRetriedOrGivenUpAndTheTestButtonSaysWhy()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var retry = await CreateAsync(admin, Channel("retry-discord", NotificationKind.Discord) with { WebhookUrl = Webhook });
        var broken = await CreateAsync(admin, Channel("broken-discord", NotificationKind.Discord) with { WebhookUrl = Webhook });
        var node = await NodeApi.CreateAsync(admin, "notifications-retry");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var row = await db.Nodes.SingleAsync(n => n.Id == new NodeId(node.Node.Id), Ct);
            await scope.ServiceProvider.GetRequiredService<Notifier>().NodeOfflineAsync(row, DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
        }

        var subject = $"node:{node.Node.Id}:offline";
        var deliveries = await WaitForDeliveriesAsync(d => d.Subject == subject
            && ((d.ChannelId.Value == retry.Id && d.Attempts == 1 && d.LastError != null) || (d.ChannelId.Value == broken.Id && d.Status == DeliveryStatus.Failed)), 2);
        var retried = deliveries.Single(d => d.ChannelId.Value == retry.Id);
        Assert.Equal(DeliveryStatus.Pending, retried.Status);
        Assert.Equal("Discord failed (503).", retried.LastError);
        Assert.InRange(retried.NextAttemptAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(31));
        var failed = deliveries.Single(d => d.ChannelId.Value == broken.Id);
        Assert.Equal(1, failed.Attempts);
        Assert.Null(failed.NextAttemptAt);

        var listed = await GetJsonAsync<NotificationDeliveryList>(admin, "/api/v1/notifications/deliveries");
        Assert.Contains(listed.Deliveries, d => d.ChannelName == "broken-discord" && d.Status == DeliveryStatus.Failed && d.Title == "Node notifications-retry is offline");

        using var test = await admin.PostAsync($"/api/v1/notifications/channels/{broken.Id}/test");
        var result = (await test.Content.ReadFromJsonAsync<NotificationTestResult>(AccessScenario.Json, Ct))!;
        Assert.False(result.Sent);
        Assert.Equal("The webhook does not exist any more (404).", result.Error);

        var es = await CreateAsync(admin, Channel("spanish", NotificationKind.Email) with { Recipients = ["ops@example.com"], Language = "es" });
        using var testEs = await admin.PostAsync($"/api/v1/notifications/channels/{es.Id}/test");
        Assert.True((await testEs.Content.ReadFromJsonAsync<NotificationTestResult>(AccessScenario.Json, Ct))!.Sent);
        Assert.Contains(factory.Email.Sent, m => m.Channel == "spanish" && m.Message.Title == "Notificación de prueba de Pulsar");
    }

    [DatabaseFact]
    public async Task NodesAreToldOfflineAfterTheGraceAndBackOnlineAndLowOnDisk()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var channel = await CreateAsync(admin, Channel("nodes", NotificationKind.Email) with
        {
            Recipients = ["ops@example.com"],
            Events = [NotificationEvent.NodeOffline, NotificationEvent.NodeDiskLow],
        });
        var node = await NodeApi.CreateAsync(admin, "notifications-watch");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var watch = new NodeWatch(factory.Services.GetRequiredService<IServiceScopeFactory>(), clock);

        // Enrolled but not connected: offline once the grace has passed, and only once.
        var grace = (await GetJsonAsync<NotificationSettingsView>(admin, "/api/v1/notifications/settings")).NodeOfflineMinutes;
        await watch.SweepAsync(Ct);
        Assert.Empty(await DeliveriesForAsync(channel.Id, node.Node.Id));
        clock.Advance(TimeSpan.FromMinutes(grace + 1));
        await watch.SweepAsync(Ct);
        await watch.SweepAsync(Ct);
        Assert.Equal(["node:" + node.Node.Id + ":offline"], (await DeliveriesForAsync(channel.Id, node.Node.Id)).Select(d => d.Subject));

        // Connected again, with a drive at 4 % free.
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken);
        await agent.Connection!.InvokeAsync(nameof(IAgentHub.NodeMetrics), new NodeMetrics(DateTimeOffset.UtcNow, 5, 1, 2, TimeSpan.FromHours(1),
            [new DiskUsage(@"D:\", 100L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024, [DiskUses.Servers, DiskUses.Backups])]), Ct);
        await watch.SweepAsync(Ct);
        await watch.SweepAsync(Ct);

        var deliveries = await DeliveriesForAsync(channel.Id, node.Node.Id);
        Assert.Equal(3, deliveries.Count);
        var online = deliveries.Single(d => d.Subject.EndsWith(":online", StringComparison.Ordinal));
        Assert.Equal(NotificationSeverity.Recovered, online.Message.Severity);
        var disk = deliveries.Single(d => d.Event == NotificationEvent.NodeDiskLow);
        Assert.Contains(disk.Message.Fields, f => f.Name == @"D:\" && f.Value == "4 GB free of 100 GB (4 %)");
    }

    [DatabaseFact]
    public async Task ANodeWhoseSessionEndedIsOfflineThoughItsConnectionIsStillOpen()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var channel = await CreateAsync(admin, Channel("ended", NotificationKind.Email) with
        {
            Recipients = ["ops@example.com"],
            Events = [NotificationEvent.NodeOffline],
        });
        var node = await NodeApi.CreateAsync(admin, "notifications-session-ended");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var watch = new NodeWatch(factory.Services.GetRequiredService<IServiceScopeFactory>(), clock);
        var grace = (await GetJsonAsync<NotificationSettingsView>(admin, "/api/v1/notifications/settings")).NodeOfflineMinutes;

        // Its session ended (its key revoked, say) and nothing has closed the connection yet: it is not connected.
        factory.Services.GetRequiredService<AgentSessionStore>().EndAll(new NodeId(agent.NodeId));
        clock.Advance(TimeSpan.FromMinutes(grace + 1));
        await watch.SweepAsync(Ct);

        Assert.Equal(["node:" + node.Node.Id + ":offline"], (await DeliveriesForAsync(channel.Id, node.Node.Id)).Select(d => d.Subject));
    }

    [DatabaseFact]
    public async Task ANodeApprovedAfterAWaitGetsTheWholeGraceToConnect()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var channel = await CreateAsync(admin, Channel("approved", NotificationKind.Email) with { Recipients = ["ops@example.com"] });
        using var created = await admin.PostAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("Notified", 1, 24, true));
        await AccessScenario.EnsureSuccessAsync(created);
        var token = (await created.Content.ReadFromJsonAsync<CreatedEnrollmentToken>(AccessScenario.Json, Ct))!;
        await using var agent = new TestAgent(factory) { Hostname = "APPROVED-LATE" };
        await agent.EnrollAsync(token.Value, token.PanelKeyFingerprint);
        var grace = TimeSpan.FromMinutes((await GetJsonAsync<NotificationSettingsView>(admin, "/api/v1/notifications/settings")).NodeOfflineMinutes);

        // It waited two days for approval (its agent last asked then), with the panel up all along.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-2));
        var watch = new NodeWatch(factory.Services.GetRequiredService<IServiceScopeFactory>(), clock);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Nodes.Where(n => n.Id == new NodeId(agent.NodeId))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastSeenAt, DateTimeOffset.UtcNow.AddDays(-2)), Ct);
        }

        clock.Advance(TimeSpan.FromDays(2));
        using (var approved = await admin.PostAsync($"/api/v1/nodes/{agent.NodeId}/approve"))
        {
            await AccessScenario.EnsureSuccessAsync(approved);
        }

        // Its agent has the grace period from the approval to connect, as a new node would.
        await watch.SweepAsync(Ct);
        Assert.Empty(await DeliveriesForAsync(channel.Id, agent.NodeId));
        clock.Advance(grace + TimeSpan.FromMinutes(1));
        await watch.SweepAsync(Ct);
        Assert.Equal(["node:" + agent.NodeId + ":offline"], (await DeliveriesForAsync(channel.Id, agent.NodeId)).Select(d => d.Subject));
    }

    /// <summary>The channel's deliveries about <paramref name="nodeId"/> (other tests' nodes go offline too).</summary>
    private async Task<List<NotificationDelivery>> DeliveriesForAsync(Guid channelId, Guid nodeId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var prefix = $"node:{nodeId}:";
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().NotificationDeliveries.AsNoTracking()
            .Where(d => d.ChannelId == new NotificationChannelId(channelId) && d.Subject.StartsWith(prefix)).OrderBy(d => d.CreatedAt).ToListAsync(Ct);
    }

    private async Task<List<NotificationDelivery>> WaitForDeliveriesAsync(Func<NotificationDelivery, bool> wanted, int count)
    {
        List<NotificationDelivery> found = [];
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var all = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().NotificationDeliveries.AsNoTracking().ToListAsync(Ct);
            found = [.. all.Where(wanted)];
            return found.Count == count;
        }, "deliveries attempted");
        return found;
    }

    private static NotificationChannelRequest Channel(string name, NotificationKind kind) =>
        new(name, kind, true, [NotificationEvent.ServerCrashed, NotificationEvent.NodeOffline], null, "en", null, null);

    private static async Task<NotificationChannelView> CreateAsync(PanelClient admin, NotificationChannelRequest request)
    {
        using var response = await admin.PostAsync("/api/v1/notifications/channels", request);
        await AccessScenario.EnsureSuccessAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NotificationChannelView>(AccessScenario.Json, Ct))!;
    }

    private static async Task<T> GetJsonAsync<T>(PanelClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(AccessScenario.Json, Ct))!;
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"notifications-{name}");
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
}
