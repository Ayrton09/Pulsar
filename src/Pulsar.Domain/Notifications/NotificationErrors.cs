using Pulsar.Domain.Common;

namespace Pulsar.Domain.Notifications;

public static class NotificationErrors
{
    public static DomainError NotFound { get; } = new("notifications.not_found", "The notification channel does not exist.");

    public static DomainError NameInvalid { get; } =
        new("notifications.name_invalid", $"A channel name is one line of 1 to {NotificationChannel.MaxNameLength} characters.");

    public static DomainError EventsRequired { get; } = new("notifications.events_required", "A channel needs at least one event.");

    public static DomainError ServersInvalid { get; } =
        new("notifications.servers_invalid", $"Choose 1 to {NotificationChannel.MaxServers} servers, or every server.");

    public static DomainError ServerUnknown { get; } = new("notifications.server_unknown", "One of the chosen servers does not exist.");

    public static DomainError LanguageInvalid { get; } = new("notifications.language_invalid", "The language is one of the panel's: en, es, pt, fr, it, de, ja, ko or zh.");

    public static DomainError WebhookInvalid { get; } =
        new("notifications.webhook_invalid", "The webhook is not a Discord webhook address (https://discord.com/api/webhooks/…).");

    public static DomainError RecipientsInvalid { get; } =
        new("notifications.recipients_invalid", $"An email channel needs 1 to {NotificationChannel.MaxRecipients} valid email addresses.");

    public static DomainError TooMany { get; } = new("notifications.too_many", $"The panel has at most {NotificationChannel.MaxChannels} channels.");

    public static DomainError PanelUrlInvalid { get; } =
        new("notifications.panel_url_invalid", "The panel address is an http or https address (e.g. https://panel.example.com:8443).");

    public static DomainError ThresholdsInvalid { get; } =
        new("notifications.thresholds_invalid",
            "Low disk is 1 to 50 % free; a node is offline after 1 to 1440 minutes; high CPU or memory is 50 to 100 % for 1 to 1440 minutes.");

    public static DomainError SmtpHostInvalid { get; } = new("notifications.smtp_host_invalid", "The SMTP server is not a host name or address.");

    public static DomainError SmtpPortInvalid { get; } = new("notifications.smtp_port_invalid", "The SMTP port is 1 to 65535.");

    public static DomainError SmtpUserInvalid { get; } = new("notifications.smtp_user_invalid", "The SMTP user name is not valid.");

    public static DomainError SmtpCredentialsNeedTls { get; } =
        new("notifications.smtp_credentials_need_tls", "Signing in to an SMTP server needs STARTTLS or TLS, unless the server is on this machine.");

    public static DomainError FromInvalid { get; } = new("notifications.from_invalid", "The sender is one valid email address (and an optional short name).");

    public static DomainError EmailNotConfigured { get; } =
        new("notifications.email_not_configured", "Email is not set up: set the SMTP server in the notification settings first.");
}
