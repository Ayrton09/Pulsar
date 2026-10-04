using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Notifications;
using Pulsar.Domain.Access;
using Pulsar.Domain.Notifications;

namespace Pulsar.Api.Notifications;

/// <param name="WebhookUrl">Discord channels: the webhook address. On an update, leave it out to keep the current one.</param>
/// <param name="Recipients">Email channels: 1 to 10 addresses.</param>
/// <param name="ServerIds">Server events only for these servers; null for every server.</param>
public sealed record NotificationChannelRequest(
    [property: Required, StringLength(NotificationChannel.MaxNameLength)] string Name,
    NotificationKind Kind,
    bool Enabled,
    [property: Required] IReadOnlyList<NotificationEvent> Events,
    IReadOnlyList<Guid>? ServerIds,
    [property: Required, StringLength(8)] string Language,
    [property: StringLength(512)] string? WebhookUrl,
    IReadOnlyList<string>? Recipients)
{
    internal NotificationChannelDefinition ToDefinition() => new(Name, Kind, Enabled, Events, ServerIds, Language, WebhookUrl, Recipients);
}

/// <param name="PanelUrl">How users reach the panel, for links in messages.</param>
/// <param name="SmtpHost">Leave it out to turn email off.</param>
/// <param name="SmtpPassword">Leave it out to keep the current password.</param>
public sealed record NotificationSettingsRequest(
    [property: StringLength(NotificationSettings.MaxUrlLength)] string? PanelUrl,
    int DiskLowPercent,
    int NodeOfflineMinutes,
    [property: StringLength(NotificationSettings.MaxHostLength)] string? SmtpHost,
    int SmtpPort,
    SmtpSecurity SmtpSecurity,
    [property: StringLength(NotificationSettings.MaxUserNameLength)] string? SmtpUserName,
    [property: StringLength(256)] string? SmtpPassword,
    bool ClearSmtpPassword,
    [property: StringLength(NotificationChannel.MaxEmailLength)] string? FromAddress,
    [property: StringLength(NotificationSettings.MaxFromNameLength)] string? FromName)
{
    internal NotificationSettingsDefinition ToDefinition() => new(PanelUrl, DiskLowPercent, NodeOfflineMinutes, SmtpHost, SmtpPort, SmtpSecurity,
        SmtpUserName, SmtpPassword, ClearSmtpPassword, FromAddress, FromName);
}

/// <summary>
/// Notification channels, settings and recent deliveries (docs/architecture/07, "Notifications"); panel settings, so
/// <c>settings.manage</c>.
/// </summary>
public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        var notifications = api.MapGroup("/notifications").WithTags("Notifications").RequirePermission(Permissions.Settings.Manage);
        notifications.MapGet("/channels", async (NotificationManagement n, CancellationToken ct) => TypedResults.Ok(await n.ListAsync(ct)));
        notifications.MapPost("/channels", CreateAsync);
        notifications.MapPut("/channels/{id:guid}", async (Guid id, NotificationChannelRequest request, NotificationManagement n, CancellationToken ct) =>
            (await n.UpdateAsync(id, request.ToDefinition(), ct)).ToHttp());
        notifications.MapDelete("/channels/{id:guid}", async (Guid id, NotificationManagement n, CancellationToken ct) => (await n.DeleteAsync(id, ct)).ToHttp());
        notifications.MapPost("/channels/{id:guid}/test", async (Guid id, NotificationManagement n, CancellationToken ct) => (await n.TestAsync(id, ct)).ToHttp());
        notifications.MapGet("/deliveries", async (NotificationManagement n, CancellationToken ct) => TypedResults.Ok(await n.DeliveriesAsync(ct)));
        notifications.MapGet("/settings", async (NotificationManagement n, CancellationToken ct) => TypedResults.Ok(await n.GetSettingsAsync(ct)));
        notifications.MapPut("/settings", async (NotificationSettingsRequest request, NotificationManagement n, CancellationToken ct) =>
            (await n.UpdateSettingsAsync(request.ToDefinition(), ct)).ToHttp());
        return api;
    }

    private static async Task<Results<Created<NotificationChannelView>, ProblemHttpResult>> CreateAsync(NotificationChannelRequest request,
        NotificationManagement notifications, CancellationToken ct)
    {
        var result = await notifications.CreateAsync(request.ToDefinition(), ct);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/notifications/channels/{result.Value.Id}", result.Value)
            : ApiProblems.FromDomainError(result.Error!);
    }
}
