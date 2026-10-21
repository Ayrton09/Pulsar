using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Pulsar.Application.Notifications;
using Pulsar.Domain.Notifications;

namespace Pulsar.Infrastructure.Notifications;

/// <summary>
/// Sends a message by email through the SMTP server in the notification settings (MailKit), as plain text and HTML.
/// The server's certificate is checked; STARTTLS is required when chosen (no silent fallback to plain text).
/// </summary>
internal sealed class EmailTransport(INotificationSecretProtector secrets) : INotificationTransport
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    public NotificationKind Kind => NotificationKind.Email;

    public async Task SendAsync(NotificationChannel channel, NotificationSettings settings, NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(message);
        if (!settings.EmailConfigured)
        {
            throw new NotificationSendException("Email is not set up: set the SMTP server in the notification settings.", retryable: false);
        }

        string? password = null;
        try
        {
            password = settings.SmtpPasswordProtected is { } stored ? secrets.Unprotect(stored) : null;
        }
        catch (CryptographicException ex)
        {
            throw new NotificationSendException("The SMTP password cannot be read with this panel's keys: set it again.", retryable: false, innerException: ex);
        }

        using var mail = Build(channel, settings, message);
        // No revocation lists or OCSP answers fetched from where the server's certificate points (the agents' port does
        // the same, docs/architecture/08, T31): whoever sets the server, or sits on the way, cannot make the panel send
        // requests to addresses of their choosing, and an unreachable revocation server does not lose the mail.
        using var client = new SmtpClient { Timeout = (int)s_timeout.TotalMilliseconds, CheckCertificateRevocation = false };
        try
        {
            var security = settings.SmtpSecurity switch
            {
                SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            };
            await client.ConnectAsync(settings.SmtpHost!, settings.SmtpPort, security, cancellationToken);
            if (settings.SmtpUserName is { } user)
            {
                await client.AuthenticateAsync(new NetworkCredential(user, password ?? string.Empty), cancellationToken);
            }

            await client.SendAsync(mail, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (AuthenticationException ex)
        {
            throw new NotificationSendException("The SMTP server refused the user name or password.", retryable: false, innerException: ex);
        }
        catch (SslHandshakeException ex)
        {
            throw new NotificationSendException("The SMTP server's certificate is not trusted, or it does not speak TLS on this port.", retryable: false,
                innerException: ex);
        }
        catch (SmtpCommandException ex)
        {
            // 4xx: try later (greylisting, mailbox busy); 5xx: it will not work.
            var code = (int)ex.StatusCode;
            throw new NotificationSendException($"The SMTP server answered {code}: {ex.Message}", retryable: code < 500, innerException: ex);
        }
        catch (SmtpProtocolException ex)
        {
            throw new NotificationSendException($"The SMTP conversation failed: {ex.Message}", retryable: true, innerException: ex);
        }
        catch (NotSupportedException ex)
        {
            throw new NotificationSendException($"The SMTP server does not support what is needed: {ex.Message}", retryable: false, innerException: ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ServiceNotConnectedException or TimeoutException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new NotificationSendException($"The SMTP server could not be reached ({settings.SmtpHost}:{settings.SmtpPort}).", retryable: true,
                innerException: ex);
        }
    }

    private static MimeMessage Build(NotificationChannel channel, NotificationSettings settings, NotificationMessage message)
    {
        var mail = new MimeMessage();
        mail.From.Add(new MailboxAddress(settings.FromName ?? "Pulsar", settings.FromAddress!));
        foreach (var recipient in channel.Recipients)
        {
            mail.To.Add(MailboxAddress.Parse(recipient));
        }

        mail.Subject = $"[Pulsar] {message.Title}";

        var text = new StringBuilder().AppendLine(message.Title).AppendLine().AppendLine(message.Text).AppendLine();
        foreach (var field in message.Fields)
        {
            text.Append(field.Name).Append(": ").AppendLine(field.Value);
        }

        if (message.Link is not null)
        {
            text.AppendLine().AppendLine(message.Link);
        }

        var color = message.Severity switch
        {
            NotificationSeverity.Error => "#e5484d",
            NotificationSeverity.Warning => "#f5a524",
            _ => "#30a46c",
        };
        var html = new StringBuilder()
            .Append("<div style=\"font-family:system-ui,sans-serif;max-width:560px\">")
            .Append("<div style=\"border-left:4px solid ").Append(color).Append(";padding:4px 12px\">")
            .Append("<h2 style=\"margin:0 0 8px;font-size:18px\">").Append(WebUtility.HtmlEncode(message.Title)).Append("</h2>")
            .Append("<p style=\"margin:0 0 12px\">").Append(WebUtility.HtmlEncode(message.Text)).Append("</p>");
        if (message.Fields.Count > 0)
        {
            html.Append("<table style=\"border-collapse:collapse;font-size:14px\">");
            foreach (var field in message.Fields)
            {
                html.Append("<tr><td style=\"padding:2px 12px 2px 0;color:#666\">").Append(WebUtility.HtmlEncode(field.Name))
                    .Append("</td><td style=\"padding:2px 0\">").Append(WebUtility.HtmlEncode(field.Value)).Append("</td></tr>");
            }

            html.Append("</table>");
        }
        if (message.Link is not null)
        {
            html.Append("<p style=\"margin:12px 0 0\"><a href=\"").Append(WebUtility.HtmlEncode(message.Link)).Append("\">")
                .Append(WebUtility.HtmlEncode(message.Link)).Append("</a></p>");
        }

        html.Append("</div></div>");
        mail.Body = new BodyBuilder { TextBody = text.ToString(), HtmlBody = html.ToString() }.ToMessageBody();
        return mail;
    }
}
