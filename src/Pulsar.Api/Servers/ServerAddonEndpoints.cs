using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Servers;

/// <summary>
/// The addons of a server's game (docs/architecture/07, "Addons"): listed with what is installed, installed (or brought to
/// their latest release) and removed while the server is stopped. Installing and removing are operations, cancelled like
/// any other (<c>POST /servers/{id}/operations/{operationId}/cancel</c>); both change the server's files, so they need the
/// right to install it.
/// </summary>
public static class ServerAddonEndpoints
{
    public static RouteGroupBuilder MapServerAddonEndpoints(this RouteGroupBuilder api)
    {
        var addons = api.MapGroup("/servers/{id:guid}/addons").WithTags("Addons");
        addons.MapGet("/", async (Guid id, ServerAddons a, CancellationToken ct) => (await a.ListAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.View);
        addons.MapPost("/{key}", async (Guid id, string key, ServerAddons a, CancellationToken ct) =>
                (await a.InstallAsync(new ServerId(id), key, ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install);
        addons.MapDelete("/{key}", async (Guid id, string key, ServerAddons a, CancellationToken ct) =>
                (await a.RemoveAsync(new ServerId(id), key, ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install);
        return api;
    }
}
