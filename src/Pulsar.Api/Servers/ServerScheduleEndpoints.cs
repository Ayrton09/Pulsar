using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Scheduling;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;

namespace Pulsar.Api.Servers;

/// <param name="Cron">Five fields: minute, hour, day of month, month, day of week.</param>
/// <param name="TimeZone">IANA id (e.g. Europe/Madrid): the schedule follows its daylight saving time.</param>
/// <param name="Command">The console command (console command tasks only).</param>
/// <param name="Validate">Whether an update verifies every file (update tasks only).</param>
/// <param name="OnlyIfRunning">Skip the run while the server is not running (restarts and backups only).</param>
public sealed record ScheduledTaskRequest(
    [property: Required, StringLength(ScheduledTask.MaxNameLength)] string Name,
    [property: Required, StringLength(ScheduledTask.MaxCronLength)] string Cron,
    [property: Required, StringLength(ScheduledTask.MaxTimeZoneLength)] string TimeZone,
    ScheduledAction Action,
    [property: StringLength(ScheduledTask.MaxCommandLength)] string? Command,
    bool Validate,
    bool OnlyIfRunning,
    bool Enabled)
{
    internal ScheduledTaskDefinition ToDefinition() => new(Name, Cron, TimeZone, Action, Command, Validate, OnlyIfRunning, Enabled);
}

/// <summary>
/// A server's scheduled tasks (docs/architecture/07, "Schedules"): listing with each task's next and last run, creating,
/// changing, deleting, running one now and its run history. Scheduling an action also needs the permission to do it.
/// </summary>
public static class ServerScheduleEndpoints
{
    public static RouteGroupBuilder MapServerScheduleEndpoints(this RouteGroupBuilder api)
    {
        var schedules = api.MapGroup("/servers/{id:guid}/schedules").WithTags("Schedules");
        schedules.MapGet("/", async (Guid id, ServerSchedules s, CancellationToken ct) => (await s.ListAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.SchedulesView);
        schedules.MapGet("/preview", async (Guid id, string cron, string timeZone, ServerSchedules s, CancellationToken ct) =>
                (await s.PreviewAsync(new ServerId(id), cron, timeZone, ct)).ToHttp())
            .RequirePermission(Permissions.Server.SchedulesView);
        schedules.MapPost("/", CreateAsync).RequirePermission(Permissions.Server.SchedulesEdit);
        schedules.MapPut("/{taskId:guid}", async (Guid id, Guid taskId, ScheduledTaskRequest request, ServerSchedules s, CancellationToken ct) =>
                (await s.UpdateAsync(new ServerId(id), new ScheduledTaskId(taskId), request.ToDefinition(), ct)).ToHttp())
            .RequirePermission(Permissions.Server.SchedulesEdit);
        schedules.MapDelete("/{taskId:guid}", async (Guid id, Guid taskId, ServerSchedules s, CancellationToken ct) =>
                (await s.DeleteAsync(new ServerId(id), new ScheduledTaskId(taskId), ct)).ToHttp())
            .RequirePermission(Permissions.Server.SchedulesEdit);
        schedules.MapPost("/{taskId:guid}/run", async (Guid id, Guid taskId, ServerSchedules s, CancellationToken ct) =>
                (await s.RunNowAsync(new ServerId(id), new ScheduledTaskId(taskId), ct)).ToHttp())
            .RequirePermission(Permissions.Server.SchedulesEdit);
        schedules.MapGet("/{taskId:guid}/runs", async (Guid id, Guid taskId, int? limit, ServerSchedules s, CancellationToken ct) =>
                (await s.RunsAsync(new ServerId(id), new ScheduledTaskId(taskId), limit ?? 20, ct)).ToHttp())
            .RequirePermission(Permissions.Server.SchedulesView);
        return api;
    }

    private static async Task<Results<Created<ScheduledTaskView>, ProblemHttpResult>> CreateAsync(Guid id, ScheduledTaskRequest request,
        ServerSchedules schedules, CancellationToken ct)
    {
        var result = await schedules.CreateAsync(new ServerId(id), request.ToDefinition(), ct);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/servers/{id}/schedules/{result.Value.Id}", result.Value)
            : ApiProblems.FromDomainError(result.Error!);
    }
}
