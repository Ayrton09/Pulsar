using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>An addon of the server's game: what the manifest says about it, and the release installed (none: not installed).</summary>
/// <param name="Source">Where it comes from: <c>githubRelease</c>, or (Pulsar 1.0.14) <c>thunderstore</c>, which publishes no checksum.</param>
public sealed record AddonView(
    string Key,
    string Name,
    string? Description,
    string License,
    string Url,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> Conflicts,
    bool ReinstallAfterUpdate,
    string? InstalledRelease,
    DateTimeOffset? InstalledAt,
    string Source = "githubRelease");

/// <summary>
/// The addons of a server's game (docs/architecture/02 and 04, "Addons"): installed (or brought to their latest release)
/// and removed by operations on its node, while the server is stopped. What is installed is kept from the operations'
/// results and sent in the server's spec, so the agent keeps the addons' lines at every start.
/// </summary>
public sealed class ServerAddons(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    ServerOperations operations,
    SteamAccounts steamAccounts,
    IAuditLog audit,
    IRequestContext request,
    TimeProvider clock)
{
    public async Task<Result<List<AddonView>>> ListAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return server is null ? ServerErrors.NotFound : await ViewsAsync(server, cancellationToken);
    }

    /// <summary>Installs <paramref name="key"/>, or brings it to its latest release when it is installed already.</summary>
    public Task<Result<List<AddonView>>> InstallAsync(ServerId id, string key, CancellationToken cancellationToken) =>
        ChangeAsync(id, key, install: true, cancellationToken);

    public Task<Result<List<AddonView>>> RemoveAsync(ServerId id, string key, CancellationToken cancellationToken) =>
        ChangeAsync(id, key, install: false, cancellationToken);

    /// <summary>
    /// A succeeded operation's result: the releases installed, and the addons removed (no release). Only addon operations
    /// and game updates change addons, and only the game's own addons are kept; for an addon named twice the last counts.
    /// </summary>
    public static async Task ApplyAsync(IPulsarDbContext db, Server server, OperationType operation, AddonsResult result, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(result);
        if (operation is not (OperationType.AddonInstall or OperationType.AddonRemove or OperationType.ServerUpdate) || result.Addons is null)
        {
            return;
        }

        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var known = ServerSpecs.Parse(template).AddonList.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var change in result.Addons)
        {
            if (change?.Key is { } key && known.Contains(key))
            {
                changes[key] = change.Release;
            }
        }

        var installed = await db.ServerAddons.Where(a => a.ServerId == server.Id).ToListAsync(cancellationToken);
        foreach (var (key, release) in changes)
        {
            var existing = installed.FirstOrDefault(a => a.Key == key);
            switch (release, existing)
            {
                case (null, { } removed):
                    db.ServerAddons.Remove(removed);
                    break;
                case ({ Length: > 0 } added, null):
                    db.ServerAddons.Add(ServerAddon.Installed(server.Id, key, added, now));
                    break;
                case ({ Length: > 0 } newer, { } updated):
                    updated.Updated(newer, now);
                    break;
            }
        }
    }

    /// <summary>Keys of the server's installed addons, for its spec.</summary>
    public static Task<List<string>> InstalledKeysAsync(IPulsarDbContext db, ServerId serverId, CancellationToken cancellationToken) =>
        db.ServerAddons.AsNoTracking().Where(a => a.ServerId == serverId).OrderBy(a => a.Key).Select(a => a.Key).ToListAsync(cancellationToken);

    private async Task<Result<List<AddonView>>> ChangeAsync(ServerId id, string key, bool install, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var addons = ServerSpecs.Parse(template).AddonList;
        if (addons.FirstOrDefault(a => a.Key == key) is not { } addon)
        {
            return ServerErrors.AddonUnknown;
        }

        if (connections.Find(server.NodeId) is not { } agent || !sessions.IsActive(agent.SessionId))
        {
            return NodeErrors.NotConnected;
        }

        if (!agent.Capabilities.Contains(install ? AgentCapabilities.InstallAddon : AgentCapabilities.RemoveAddon))
        {
            return ServerErrors.AddonsUnsupported;
        }

        var installed = (await InstalledKeysAsync(db, server.Id, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (install)
        {
            if (addon.RequiresList.FirstOrDefault(r => !installed.Contains(r)) is { } missing)
            {
                return ServerErrors.AddonRequires(addon.Name, NameOf(addons, missing));
            }

            if (addon.ConflictsList.Concat(addons.Where(a => a.ConflictsList.Contains(addon.Key)).Select(a => a.Key))
                    .FirstOrDefault(installed.Contains) is { } conflict)
            {
                return ServerErrors.AddonConflicts(addon.Name, NameOf(addons, conflict));
            }
        }
        else if (addons.FirstOrDefault(a => installed.Contains(a.Key) && a.RequiresList.Contains(addon.Key)) is { } dependent)
        {
            return ServerErrors.AddonRequiredBy(addon.Name, dependent.Name);
        }

        var operation = Operation.Create(install ? OperationType.AddonInstall : OperationType.AddonRemove, server.NodeId, server.Id, request.UserId,
            OperationRequester.User, clock.GetUtcNow());
        audit.Record(install ? AuditActions.Server.AddonInstallRequested : AuditActions.Server.AddonRemoveRequested, metadata: new { addon = addon.Key })
            .WithServer(server.Id, server.NodeId);
        SteamLogin? login = null;
        if (!install)
        {
            // Game files it replaced come back with a validating update: SteamCMD signs in as for any update.
            var signIn = await steamAccounts.LoginForAsync(server, ServerSpecs.Parse(await db.GameTemplates.AsNoTracking()
                .SingleAsync(t => t.Id == server.TemplateId, cancellationToken)), agent, null, cancellationToken);
            if (!signIn.IsSuccess)
            {
                return signIn.Error!;
            }

            login = signIn.Value;
        }

        AgentCommand command = install
            ? new InstallAddonCommand(server.Id.Value, addon.Key)
            : new RemoveAddonCommand(server.Id.Value, addon.Key, login);
        var run = await operations.RunAsync(server, operation, command, beforeSave: null, cancellationToken);
        return run.IsSuccess ? await ViewsAsync(server, cancellationToken) : run.Error!;
    }

    private async Task<List<AddonView>> ViewsAsync(Server server, CancellationToken cancellationToken)
    {
        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var installed = await db.ServerAddons.AsNoTracking().Where(a => a.ServerId == server.Id).ToDictionaryAsync(a => a.Key, cancellationToken);
        return [.. ServerSpecs.Parse(template).AddonList.Select(a => new AddonView(a.Key, a.Name, a.Description, a.License, a.Url, a.RequiresList,
            a.ConflictsList, a.ReinstallAfterUpdate, installed.GetValueOrDefault(a.Key)?.Release, installed.GetValueOrDefault(a.Key)?.InstalledAt,
            a.Source is Manifests.ThunderstoreSource ? "thunderstore" : "githubRelease"))];
    }

    private static string NameOf(IReadOnlyList<Manifests.AddonDefinition> addons, string key) => addons.FirstOrDefault(a => a.Key == key)?.Name ?? key;
}
