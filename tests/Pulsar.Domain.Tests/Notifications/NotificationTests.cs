using Pulsar.Domain.Nodes;
using Pulsar.Domain.Notifications;

namespace Pulsar.Domain.Tests.Notifications;

public sealed class NotificationTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] s_protected = [1, 2, 3];

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456789/abc-DEF_123", true)]
    [InlineData("https://discordapp.com/api/webhooks/1/x", true)]
    [InlineData("https://ptb.discord.com/api/v10/webhooks/1/x", true)]
    [InlineData(" https://discord.com/api/webhooks/1/x ", true)]
    [InlineData("http://discord.com/api/webhooks/1/x", false)] // not HTTPS
    [InlineData("https://discord.com:8443/api/webhooks/1/x", false)]
    [InlineData("https://user:pw@discord.com/api/webhooks/1/x", false)]
    [InlineData("https://discord.com.evil.example/api/webhooks/1/x", false)]
    [InlineData("https://evil.example/api/webhooks/1/x", false)]
    [InlineData("https://127.0.0.1/api/webhooks/1/x", false)]
    [InlineData("https://discord.com/api/webhooks/abc/x", false)] // the id is a number
    [InlineData("https://discord.com/api/webhooks/1", false)]
    [InlineData("https://discord.com/api/webhooks/1/x/slack", false)]
    [InlineData("https://discord.com/api/webhooks/1/x%2F..", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyDiscordWebhookAddressesAreAccepted(string? url, bool expected) =>
        Assert.Equal(expected, NotificationChannel.IsDiscordWebhook(url));

    [Theory]
    [InlineData("ops@example.com", true)]
    [InlineData("a.b+tag@mail.example.org", true)]
    [InlineData("Ops <ops@example.com>", false)]
    [InlineData("ops@example.com, other@example.com", false)]
    [InlineData("ops@localhost", false)]
    [InlineData("ops@example.com\r\nBcc: x@example.com", false)]
    [InlineData("not an address", false)]
    public void EmailAddressesArePlainAddresses(string address, bool expected) =>
        Assert.Equal(expected, NotificationChannel.IsEmailAddress(address));

    [Fact]
    public void AnEmailChannelNeedsValidRecipients()
    {
        var created = NotificationChannel.Create(NotificationKind.Email, "Admins", [NotificationEvent.ServerCrashed], null, "es", true, null,
            ["ops@example.com", "OPS@example.com", " other@example.com "], s_now);

        var channel = Assert.IsType<NotificationChannel>(created.Value);
        Assert.Equal(["ops@example.com", "other@example.com"], channel.Recipients);
        Assert.Null(channel.WebhookUrlProtected);

        Assert.Equal(NotificationErrors.RecipientsInvalid, Email(recipients: []).Error);
        Assert.Equal(NotificationErrors.RecipientsInvalid, Email(recipients: ["bad"]).Error);
        Assert.Equal(NotificationErrors.RecipientsInvalid, Email(recipients: [.. Enumerable.Range(0, 11).Select(i => $"u{i}@example.com")]).Error);
    }

    [Fact]
    public void ADiscordChannelNeedsItsWebhookAndKeepsItWhenNotChanged()
    {
        Assert.Equal(NotificationErrors.WebhookInvalid,
            NotificationChannel.Create(NotificationKind.Discord, "Discord", [NotificationEvent.ServerCrashed], null, "en", true, null, null, s_now).Error);

        var channel = Discord();
        Assert.True(channel.Change("Renamed", [NotificationEvent.NodeOffline], null, "en", true, null, null, s_now).IsSuccess);
        Assert.Equal(s_protected, channel.WebhookUrlProtected);
        Assert.Empty(channel.Recipients);

        byte[] replaced = [9];
        Assert.True(channel.Change("Renamed", [NotificationEvent.NodeOffline], null, "en", true, replaced, null, s_now).IsSuccess);
        Assert.Equal(replaced, channel.WebhookUrlProtected);
    }

    [Fact]
    public void SettingsAreChecked()
    {
        Assert.Equal(NotificationErrors.NameInvalid, Email(name: " ").Error);
        Assert.Equal(NotificationErrors.NameInvalid, Email(name: new string('x', 65)).Error);
        Assert.Equal(NotificationErrors.EventsRequired, Email(events: []).Error);
        Assert.Equal(NotificationErrors.EventsRequired, Email(events: [(NotificationEvent)99]).Error);
        // The panel's languages, as its codes: no other code, region or casing.
        foreach (var language in new[] { "xx", "pt-BR", "zh-CN", "ES", "" })
        {
            Assert.Equal(NotificationErrors.LanguageInvalid, Email(language: language).Error);
        }

        Assert.All(NotificationChannel.Languages, language => Assert.Null(Email(language: language).Error));
        Assert.Equal(["en", "es", "pt", "fr", "it", "de", "ja", "ko", "zh"], NotificationChannel.Languages);
        Assert.Equal(NotificationErrors.ServersInvalid, Email(servers: []).Error);
        Assert.Equal(NotificationErrors.ServersInvalid, Email(servers: [Guid.Empty]).Error);
    }

    [Fact]
    public void AChannelWantsItsEventsForItsServers()
    {
        var mine = Guid.NewGuid();
        var channel = Assert.IsType<NotificationChannel>(Email(events: [NotificationEvent.ServerCrashed, NotificationEvent.NodeOffline],
            servers: [mine, mine]).Value);

        Assert.Equal([mine], channel.ServerIds);
        Assert.True(channel.Wants(NotificationEvent.ServerCrashed, mine));
        Assert.False(channel.Wants(NotificationEvent.ServerCrashed, Guid.NewGuid()));
        Assert.False(channel.Wants(NotificationEvent.BackupFailed, mine));
        Assert.True(channel.Wants(NotificationEvent.NodeOffline, null)); // node events are not filtered by server

        channel.Change(channel.Name, channel.Events, null, "en", enabled: false, null, channel.Recipients, s_now);
        Assert.False(channel.Wants(NotificationEvent.ServerCrashed, mine));
    }

    [Fact]
    public void ADeliveryIsRetriedWithGrowingDelaysThenFails()
    {
        var delivery = NotificationDelivery.Create(Discord(), NotificationEvent.ServerCrashed, "server:1", Message(), s_now);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(s_now, delivery.NextAttemptAt);

        var now = s_now;
        foreach (var delay in NotificationDelivery.RetryDelays)
        {
            delivery.Taken(now);
            Assert.Equal(now + NotificationDelivery.Lease, delivery.NextAttemptAt);
            delivery.AttemptFailed("503 Service Unavailable", retryable: true, retryAfter: null, now);
            Assert.Equal(DeliveryStatus.Pending, delivery.Status);
            Assert.Equal(now + delay, delivery.NextAttemptAt);
            now += delay;
        }

        delivery.Taken(now);
        delivery.AttemptFailed("503 Service Unavailable", retryable: true, retryAfter: null, now);
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal(NotificationDelivery.RetryDelays.Count + 1, delivery.Attempts);
        Assert.Null(delivery.NextAttemptAt);
    }

    [Fact]
    public void ARateLimitWaitsAsLongAsAskedAndAPermanentErrorFailsAtOnce()
    {
        var limited = NotificationDelivery.Create(Discord(), NotificationEvent.ServerCrashed, "server:1", Message(), s_now);
        limited.Taken(s_now);
        limited.AttemptFailed("429", retryable: true, retryAfter: TimeSpan.FromMinutes(5), s_now);
        Assert.Equal(s_now + TimeSpan.FromMinutes(5), limited.NextAttemptAt);

        // A receiver cannot park a message for days (or past the end of time).
        var parked = NotificationDelivery.Create(Discord(), NotificationEvent.ServerCrashed, "server:1", Message(), s_now);
        parked.Taken(s_now);
        parked.AttemptFailed("429", retryable: true, retryAfter: TimeSpan.MaxValue, s_now);
        Assert.Equal(s_now + NotificationDelivery.MaxRetryAfter, parked.NextAttemptAt);

        var gone = NotificationDelivery.Create(Discord(), NotificationEvent.ServerCrashed, "server:1", Message(), s_now);
        gone.Taken(s_now);
        gone.AttemptFailed(new string('x', 600), retryable: false, retryAfter: null, s_now);
        Assert.Equal(DeliveryStatus.Failed, gone.Status);
        Assert.Equal(NotificationDelivery.MaxErrorLength, gone.LastError!.Length);

        var sent = NotificationDelivery.Create(Discord(), NotificationEvent.ServerCrashed, "server:1", Message(), s_now);
        sent.Taken(s_now);
        sent.Sent(s_now);
        Assert.Equal(DeliveryStatus.Sent, sent.Status);
        Assert.Null(sent.NextAttemptAt);
    }

    [Fact]
    public void ADeliveryThatEndedStaysAsItEnded()
    {
        // A sender whose lease ran out finishes after another one sent it.
        var sent = NotificationDelivery.Create(Discord(), NotificationEvent.ServerCrashed, "server:1", Message(), s_now);
        Assert.True(sent.Taken(s_now));
        Assert.True(sent.Sent(s_now));
        Assert.False(sent.AttemptFailed("timeout", retryable: false, retryAfter: null, s_now + TimeSpan.FromMinutes(3)));
        Assert.False(sent.Taken(s_now + TimeSpan.FromMinutes(3)));
        Assert.Equal((DeliveryStatus.Sent, 1, (string?)null, (DateTimeOffset?)s_now), (sent.Status, sent.Attempts, sent.LastError, sent.SentAt));

        var failed = NotificationDelivery.Create(Discord(), NotificationEvent.ServerCrashed, "server:1", Message(), s_now);
        failed.Taken(s_now);
        failed.AttemptFailed("410 Gone", retryable: false, retryAfter: null, s_now);
        Assert.False(failed.Sent(s_now));
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Null(failed.SentAt);
    }

    [Fact]
    public void SettingsCheckTheirValues()
    {
        var settings = NotificationSettings.Default(s_now);
        Assert.False(settings.EmailConfigured);
        Assert.Equal(TimeSpan.FromMinutes(3), settings.NodeOfflineAfter);

        Assert.True(settings.ChangeGeneral("https://panel.example.com:8443/", 15, 5, s_now).IsSuccess);
        Assert.Equal("https://panel.example.com:8443", settings.PanelUrl);
        Assert.Equal(NotificationErrors.PanelUrlInvalid, settings.ChangeGeneral("ftp://panel", 10, 3, s_now).Error);
        Assert.Equal(NotificationErrors.PanelUrlInvalid, settings.ChangeGeneral("https://panel/?x=1", 10, 3, s_now).Error);
        Assert.Equal(NotificationErrors.ThresholdsInvalid, settings.ChangeGeneral(null, 0, 3, s_now).Error);
        Assert.Equal(NotificationErrors.ThresholdsInvalid, settings.ChangeGeneral(null, 10, 0, s_now).Error);

        Assert.Equal((90, 90, TimeSpan.FromMinutes(10)), (settings.CpuHighPercent, settings.MemoryHighPercent, settings.ResourceHighAfter));
        Assert.True(settings.ChangeResourceThresholds(80, 95, 30, s_now).IsSuccess);
        Assert.Equal((80, 95, 30), (settings.CpuHighPercent, settings.MemoryHighPercent, settings.ResourceHighMinutes));
        Assert.Equal(NotificationErrors.ThresholdsInvalid, settings.ChangeResourceThresholds(49, 90, 10, s_now).Error);
        Assert.Equal(NotificationErrors.ThresholdsInvalid, settings.ChangeResourceThresholds(90, 101, 10, s_now).Error);
        Assert.Equal(NotificationErrors.ThresholdsInvalid, settings.ChangeResourceThresholds(90, 90, 0, s_now).Error);
        Assert.Equal(80, settings.CpuHighPercent);

        Assert.True(settings.ChangeSmtp("smtp.example.com", 587, SmtpSecurity.StartTls, "pulsar", [7], false, "pulsar@example.com", "Pulsar", s_now)
            .IsSuccess);
        Assert.True(settings.EmailConfigured);
        // No new password: the old one stays.
        Assert.True(settings.ChangeSmtp("smtp.example.com", 465, SmtpSecurity.Tls, "pulsar", null, false, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.Equal([7], settings.SmtpPasswordProtected);
        Assert.True(settings.ChangeSmtp("smtp.example.com", 465, SmtpSecurity.Tls, "pulsar", null, true, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.Null(settings.SmtpPasswordProtected);

        Assert.Equal(NotificationErrors.SmtpHostInvalid, settings.ChangeSmtp("bad host", 25, SmtpSecurity.None, null, null, false, "a@example.com", null, s_now).Error);
        Assert.Equal(NotificationErrors.SmtpPortInvalid, settings.ChangeSmtp("smtp.example.com", 0, SmtpSecurity.None, null, null, false, "a@example.com", null, s_now).Error);
        Assert.Equal(NotificationErrors.FromInvalid, settings.ChangeSmtp("smtp.example.com", 25, SmtpSecurity.None, null, null, false, "nobody", null, s_now).Error);

        Assert.True(settings.ChangeSmtp(null, 0, SmtpSecurity.StartTls, null, null, false, null, null, s_now).IsSuccess);
        Assert.False(settings.EmailConfigured);
        Assert.Null(settings.SmtpPasswordProtected);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/abc", "https://discord.com/api/webhooks/1/abc?wait=true")]
    [InlineData(" https://discord.com/api/webhooks/1/abc ", "https://discord.com/api/webhooks/1/abc?wait=true")]
    [InlineData("https://discord.com/api/webhooks/1/abc?thread_id=123", "https://discord.com/api/webhooks/1/abc?thread_id=123&wait=true")]
    public void AWebhookIsPostedToWithItsOwnQueryKept(string webhook, string expected)
    {
        Assert.True(NotificationChannel.IsDiscordWebhook(webhook));
        Assert.Equal(expected, NotificationChannel.DiscordPostAddress(webhook));
    }

    [Fact]
    public void TheSmtpPasswordIsNeverSentToAnotherServerOrAccount()
    {
        var settings = NotificationSettings.Default(s_now);
        Assert.True(settings.ChangeSmtp("smtp.example.com", 587, SmtpSecurity.StartTls, "pulsar", [7], false, "pulsar@example.com", null, s_now).IsSuccess);

        // Same server and account (the host's case does not matter): kept.
        Assert.True(settings.ChangeSmtp("SMTP.example.com", 465, SmtpSecurity.Tls, "pulsar", null, false, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.Equal([7], settings.SmtpPasswordProtected);

        // Another server, without a new password: forgotten, not sent to it.
        Assert.True(settings.ChangeSmtp("smtp.other.example", 465, SmtpSecurity.Tls, "pulsar", null, false, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.Null(settings.SmtpPasswordProtected);

        Assert.True(settings.ChangeSmtp("smtp.other.example", 465, SmtpSecurity.Tls, "pulsar", [8], false, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.True(settings.ChangeSmtp("smtp.other.example", 465, SmtpSecurity.Tls, "someone-else", null, false, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.Null(settings.SmtpPasswordProtected);

        // A new password with the new server or account is taken.
        Assert.True(settings.ChangeSmtp("smtp.example.com", 587, SmtpSecurity.StartTls, "pulsar", [9], false, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.Equal([9], settings.SmtpPasswordProtected);
    }

    [Fact]
    public void CredentialsAreNotSentInClear()
    {
        var settings = NotificationSettings.Default(s_now);

        Assert.Equal(NotificationErrors.SmtpCredentialsNeedTls,
            settings.ChangeSmtp("smtp.example.com", 25, SmtpSecurity.None, "pulsar", [7], false, "pulsar@example.com", null, s_now).Error);
        Assert.Null(settings.SmtpHost);

        // A relay without signing in, or one on this machine, can go without encryption.
        Assert.True(settings.ChangeSmtp("relay.example.com", 25, SmtpSecurity.None, null, null, false, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.True(settings.ChangeSmtp("localhost", 25, SmtpSecurity.None, "pulsar", [7], false, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.True(settings.ChangeSmtp("127.0.0.1", 25, SmtpSecurity.None, "pulsar", [7], false, "pulsar@example.com", null, s_now).IsSuccess);
        Assert.True(settings.ChangeSmtp("::1", 25, SmtpSecurity.None, "pulsar", [7], false, "pulsar@example.com", null, s_now).IsSuccess);
    }

    [Fact]
    public void ANodeIsToldOfflineOnceAndBackOnlineOnce()
    {
        var node = Assert.IsType<Node>(Node.Create("node", null, null, s_now).Value);
        Assert.False(node.BackOnline());
        Assert.True(node.OfflineNotified(s_now));
        Assert.False(node.OfflineNotified(s_now));
        Assert.True(node.BackOnline());
        Assert.False(node.BackOnline());

        Assert.True(node.DiskLowNotified(s_now));
        Assert.False(node.DiskLowNotified(s_now));
        node.DiskSpaceRecovered();
        Assert.True(node.DiskLowNotified(s_now));
    }

    private static NotificationChannel Discord() =>
        Assert.IsType<NotificationChannel>(NotificationChannel.Create(NotificationKind.Discord, "Discord", [NotificationEvent.ServerCrashed], null, "en",
            true, s_protected, null, s_now).Value);

    private static Common.Result<NotificationChannel> Email(string name = "Admins", NotificationEvent[]? events = null, Guid[]? servers = null,
        string language = "en", string[]? recipients = null) =>
        NotificationChannel.Create(NotificationKind.Email, name, events ?? [NotificationEvent.ServerCrashed], servers, language, true, null,
            recipients ?? ["ops@example.com"], s_now);

    private static NotificationMessage Message() => new("Server crashed", "It exited.", NotificationSeverity.Error, [], null);
}
