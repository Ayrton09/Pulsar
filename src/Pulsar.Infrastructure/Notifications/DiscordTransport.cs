using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulsar.Application.Notifications;
using Pulsar.Domain.Notifications;

namespace Pulsar.Infrastructure.Notifications;

/// <summary>
/// Posts to a Discord webhook as an embed. The address is checked again before every post (only Discord's own, over
/// HTTPS), redirects are not followed, and the client logs no URLs: the webhook token is a secret.
/// </summary>
internal sealed class DiscordTransport(IHttpClientFactory clients, INotificationSecretProtector secrets) : INotificationTransport
{
    public const string ClientName = "Pulsar.Discord";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public NotificationKind Kind => NotificationKind.Discord;

    public async Task SendAsync(NotificationChannel channel, NotificationSettings settings, NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        string url;
        try
        {
            url = channel.WebhookUrlProtected is { } stored ? secrets.Unprotect(stored) : throw new NotificationSendException("The channel has no webhook.", false);
        }
        catch (CryptographicException ex)
        {
            throw new NotificationSendException("The webhook cannot be read with this panel's keys: set it again.", retryable: false, innerException: ex);
        }

        if (!NotificationChannel.IsDiscordWebhook(url))
        {
            throw new NotificationSendException("The webhook is not a Discord webhook address.", retryable: false);
        }

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(ClientName).PostAsJsonAsync(NotificationChannel.DiscordPostAddress(url), Payload(message), s_json,
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new NotificationSendException($"Discord could not be reached: {ex.HttpRequestError}.", retryable: true, innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationSendException("Discord did not answer in time.", retryable: true, innerException: ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var status = (int)response.StatusCode;
            throw response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => new NotificationSendException("Discord is limiting the messages to this webhook (429).", retryable: true,
                    await RetryAfterAsync(response, cancellationToken)),
                HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new NotificationSendException($"The webhook does not exist any more ({status}): it was deleted in Discord.", retryable: false),
                _ when status >= 500 => new NotificationSendException($"Discord failed ({status}).", retryable: true),
                _ => new NotificationSendException($"Discord refused the message ({status}).", retryable: false),
            };
        }
    }

    private static object Payload(NotificationMessage message) => new
    {
        username = "Pulsar",
        // Nothing in a message may mention @everyone or a role.
        allowed_mentions = new { parse = Array.Empty<string>() },
        embeds = new[]
        {
            new
            {
                title = Clip(message.Title, 256),
                description = Clip(message.Text, 4096),
                url = message.Link,
                color = message.Severity switch
                {
                    NotificationSeverity.Error => 0xE5484D,
                    NotificationSeverity.Warning => 0xF5A524,
                    _ => 0x30A46C,
                },
                fields = message.Fields.Take(25).Select(f => new { name = Clip(f.Name, 256), value = Clip(f.Value, 1024), inline = true }).ToArray(),
                footer = new { text = "Pulsar" },
            },
        },
    };

    private static async Task<TimeSpan?> RetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return body.RootElement.TryGetProperty("retry_after", out var seconds) && seconds.TryGetDouble(out var value)
                ? TimeSpan.FromSeconds(Math.Min(value, 3600))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text, int length) =>
        string.IsNullOrEmpty(text) ? "-" : text.Length <= length ? text : string.Concat(text.AsSpan(0, length - 1), "…");
}
