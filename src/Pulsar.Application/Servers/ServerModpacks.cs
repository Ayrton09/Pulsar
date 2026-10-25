using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

/// <summary>A modpack on a server, or on its way.</summary>
/// <param name="ServerType">The server type it runs on.</param>
/// <param name="LoaderVersion">The loader's version its index names; null until the node reported it.</param>
public sealed record ModpackView(
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    string ServerType,
    string Loader,
    string GameVersion,
    string? LoaderVersion,
    DateTimeOffset InstalledAt);

/// <summary>A version of a modpack, and whether it installs on the server (and on what).</summary>
/// <param name="ServerType">The server type it would run on; null when none of the game's runs it.</param>
/// <param name="GameVersion">The game version it is made for; null when it names none the server takes.</param>
public sealed record ModpackVersionView(
    string Id,
    string Name,
    string VersionNumber,
    string VersionType,
    DateTimeOffset Published,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<string> GameVersions,
    string? ServerType,
    string? GameVersion);

public sealed record ModpackVersionList(IReadOnlyList<ModpackVersionView> Versions);

/// <summary>
/// Modpacks from the catalog (Pulsar 1.0.9; docs/architecture/04, "Modpacks"): searched among those made for the loaders
/// the game's mod types run, whatever the server's own type, since a pack sets it. Installing one is an operation on the
/// stopped server: the node installs the pack's server type, game version and loader, then its files. The pack, and the
/// server's type and versions, are recorded once the node says it succeeded.
/// </summary>
public sealed partial class ServerMods
{
    /// <summary>The catalog loaders a pack's index can name (<c>modrinth.index.json</c>'s dependencies).</summary>
    private static readonly HashSet<string> s_modpackLoaders = new(StringComparer.Ordinal) { "fabric", "forge", "neoforge", "quilt" };

    public async Task<Result<ModSearchView>> SearchModpacksAsync(ServerId id, string? query, int offset, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (PackSource(await ManifestAsync(server, cancellationToken)) is not { } source)
        {
            return ModpackErrors.Unsupported;
        }

        var text = query?.Trim();
        if (text is { Length: > 100 } || text?.Any(char.IsControl) == true)
        {
            return ModErrors.QueryInvalid;
        }

        return await CatalogAsync(async () =>
        {
            var found = await modrinth.SearchAsync(text, "modpack", PackLoaders(source), null, Math.Clamp(offset, 0, 10_000), SearchPageSize, cancellationToken);
            var installed = await db.ServerModpacks.AsNoTracking().Where(p => p.ServerId == server.Id && p.State == ServerModpackState.Installed)
                .Select(p => p.ProjectId).ToListAsync(cancellationToken);
            return new ModSearchView([.. found.Hits.Select(h => new ModHitView(h.ProjectId, h.Slug, h.Title, h.Description, h.Author, h.Downloads, h.Updated,
                installed.Contains(h.ProjectId)))], found.Total);
        });
    }

    /// <summary>The pack's versions for the game's loaders, newest first, each with what it would run on.</summary>
    public async Task<Result<ModpackVersionList>> ModpackVersionsAsync(ServerId id, string projectId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var manifest = await ManifestAsync(server, cancellationToken);
        if (PackSource(manifest) is not { } source)
        {
            return ModpackErrors.Unsupported;
        }

        if (!IsCatalogId(projectId))
        {
            return ModpackErrors.NoVersion;
        }

        return await CatalogAsync(async () =>
        {
            var versions = await modrinth.VersionsAsync(projectId, PackLoaders(source), null, cancellationToken);
            return new ModpackVersionList([.. versions.Take(50).Select(v =>
            {
                var target = PackTarget(manifest, source, v);
                return new ModpackVersionView(v.Id, v.Name, v.VersionNumber, v.VersionType, v.Published, v.Loaders, v.GameVersions,
                    target.IsSuccess ? target.Value.ServerType : null, target.IsSuccess ? target.Value.GameVersion : null);
            })]);
        });
    }

    /// <summary>Installs the pack (its newest version the server can run, a release first, or <paramref name="versionId"/>).</summary>
    public async Task<Result<ModsView>> InstallModpackAsync(ServerId id, string projectId, string? versionId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (!IsCatalogId(projectId) || (versionId is not null && !IsCatalogId(versionId)))
        {
            return ModpackErrors.NoVersion;
        }

        await SettleModpacksAsync(server.Id, cancellationToken);
        var manifest = await ManifestAsync(server, cancellationToken);
        if (PackSource(manifest) is not { } source)
        {
            return ModpackErrors.Unsupported;
        }

        var chosen = await CatalogAsync(() => ChooseModpackAsync(manifest, source, projectId, versionId, cancellationToken));
        if (!chosen.IsSuccess)
        {
            return chosen.Error!;
        }

        var (version, target) = chosen.Value;

        // Its files are the game's while it runs (Windows does not replace a file in use), and its install replaces the server's.
        if (server.RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        if (connections.Find(server.NodeId) is not { } agent || !sessions.IsActive(agent.SessionId))
        {
            return NodeErrors.NotConnected;
        }

        if (!agent.Capabilities.Contains(AgentCapabilities.InstallModpack))
        {
            return ModpackErrors.Unsupported with { Message = "The server's node has an agent too old to install modpacks: update it." };
        }

        if (await db.ServerModpacks.AnyAsync(p => p.ServerId == server.Id && p.State == ServerModpackState.Installing, cancellationToken)
            || await db.ServerMods.AnyAsync(m => m.ServerId == server.Id && m.State != ServerModState.Installed, cancellationToken))
        {
            return ModErrors.ChangeRunning;
        }

        var name = version.Name;
        try
        {
            name = FirstOf(await modrinth.ProjectsAsync([version.ProjectId], cancellationToken))?.Title ?? name;
        }
        catch (ModCatalogUnavailableException)
        {
            // the version's own name will do
        }

        var now = clock.GetUtcNow();
        var operation = Operation.Create(OperationType.ModpackInstall, server.NodeId, server.Id, request.UserId, OperationRequester.User, now);
        var command = new InstallModpackCommand(server.Id.Value, new ModDownload(target.File.FileName, target.File.Url, target.File.Sha512, target.File.Size),
            target.ServerType, target.Loader, target.GameVersion);
        var run = await operations.RunAsync(server, operation, command, beforeSave: () =>
        {
            db.ServerModpacks.Add(ServerModpack.Installing(server.Id, ModSource.Modrinth, version.ProjectId, version.Id, name, version.VersionNumber,
                target.ServerType, target.Loader, target.GameVersion, operation.Id, now));
            audit.Record(AuditActions.Server.ModpackInstallRequested, metadata: new
            {
                project = version.ProjectId,
                version = version.VersionNumber,
                name,
                type = target.ServerType,
                gameVersion = target.GameVersion,
            }).WithServer(server.Id, server.NodeId);
        }, cancellationToken);
        if (!run.IsSuccess)
        {
            db.ChangeTracker.Clear();
            return run.Error!;
        }

        // Refused by the node at once: nothing changed.
        if (!run.Value.IsActive)
        {
            await ModpackCompletedAsync(db, run.Value, null, clock.GetUtcNow(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(server.Id, cancellationToken);
    }

    /// <summary>The version asked for, or the pack's newest the server can run (a release first).</summary>
    private async Task<Result<(ModrinthVersion Version, PackPlan Plan)>> ChooseModpackAsync(GameManifest manifest, ModSourceDefinition source, string projectId,
        string? versionId, CancellationToken cancellationToken)
    {
        if (versionId is not null)
        {
            var asked = await modrinth.VersionAsync(versionId, cancellationToken);
            if (asked is null || asked.ProjectId != projectId)
            {
                return ModpackErrors.NoVersion;
            }

            var plan = PackTarget(manifest, source, asked);
            return plan.IsSuccess ? (asked, plan.Value) : plan.Error!;
        }

        var versions = await modrinth.VersionsAsync(projectId, PackLoaders(source), null, cancellationToken);
        if (versions.Count == 0)
        {
            return ModpackErrors.NoVersion;
        }

        var installable = versions.Select(v => (Version: v, Plan: PackTarget(manifest, source, v))).Where(p => p.Plan.IsSuccess).ToList();
        if (installable.Count == 0)
        {
            return PackTarget(manifest, source, versions[0]).Error!; // why the newest cannot be
        }

        var best = installable.FirstOrDefault(p => p.Version.VersionType == "release");
        best = best.Version is null ? installable[0] : best;
        return (best.Version, best.Plan.Value);
    }

    /// <summary>
    /// Settles the pack an operation installed: on success it replaces the server's previous pack, and the server's type,
    /// game version and loader version become the pack's (the node installed them); otherwise it is dropped.
    /// </summary>
    /// <param name="result">What the node reported; null when it reported nothing (an operation that ended unheard of).</param>
    public static async Task ModpackCompletedAsync(IPulsarDbContext db, Operation operation, ModpackResult? result, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Type != OperationType.ModpackInstall || operation.IsActive || operation.ServerId is not { } serverId)
        {
            return;
        }

        var packs = await db.ServerModpacks.Where(p => p.ServerId == serverId).ToListAsync(cancellationToken);
        if (packs.FirstOrDefault(p => p.OperationId == operation.Id) is not { } pack)
        {
            return;
        }

        if (operation.Status != OperationStatus.Succeeded)
        {
            db.ServerModpacks.Remove(pack);
            return;
        }

        // Loads its variables into the server the caller may track already.
        var server = await db.Servers.Include(s => s.Variables).SingleAsync(s => s.Id == serverId, cancellationToken);
        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var manifest = ServerSpecs.Parse(template);
        var loaderVersion = result?.LoaderVersion is { Length: > 0 } reported ? reported : null;
        if (PackSource(manifest) is { Modpacks: { } modpacks } source)
        {
            server.SetVariable(source.TypeVar, pack.ServerType, null, now);
            server.SetVariable(source.VersionVar, pack.GameVersion, null, now);
            if (manifest.VariableList.FirstOrDefault(v => v.Key == modpacks.LoaderVar) is not { } loaderVariable || loaderVersion is null
                || VariableValues.Check(loaderVariable, loaderVersion) is not null)
            {
                loaderVersion = null; // not one the server's variable takes: it keeps its own
            }
            else
            {
                server.SetVariable(modpacks.LoaderVar, loaderVersion, null, now);
            }
        }

        server.GameInstalled(result?.GameVersion ?? pack.GameVersion, now);
        foreach (var previous in packs.Where(p => p != pack && p.State == ServerModpackState.Installed))
        {
            db.ServerModpacks.Remove(previous);
        }

        pack.Installed(loaderVersion, now);
    }

    /// <summary>Packs left installing by an operation that ended unheard of take its outcome; those whose operation is gone are dropped.</summary>
    private async Task SettleModpacksAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        var pending = await db.ServerModpacks.Where(p => p.ServerId == serverId && p.State == ServerModpackState.Installing).ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var pack in pending)
        {
            var operation = pack.OperationId is { } operationId
                ? await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == operationId, cancellationToken)
                : null;
            if (operation is null)
            {
                db.ServerModpacks.Remove(pack);
                changed = true;
            }
            else if (!operation.IsActive)
            {
                await ModpackCompletedAsync(db, operation, null, clock.GetUtcNow(), cancellationToken);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The node's report settled it meanwhile.
        }

        db.ChangeTracker.Clear();
    }

    /// <param name="ServerType">The option of the server type variable the pack runs on.</param>
    private sealed record PackPlan(string ServerType, string Loader, string GameVersion, ModrinthFile File);

    /// <summary>
    /// What a version of a pack runs on: the first of its loaders one of the game's mod types runs (in the manifest's order),
    /// the single game version it names (one the server's variable takes), and its <c>.mrpack</c> on Modrinth's CDN.
    /// </summary>
    private static Result<PackPlan> PackTarget(GameManifest manifest, ModSourceDefinition source, ModrinthVersion version)
    {
        var type = source.Types.Where(t => t.Value.ProjectType == "mod")
            .SelectMany(t => t.Value.Loaders.Where(l => s_modpackLoaders.Contains(l) && version.Loaders.Contains(l)).Select(l => (Type: t.Key, Loader: l)))
            .FirstOrDefault();
        if (type.Type is null)
        {
            return ModpackErrors.LoaderUnsupported;
        }

        var versionVariable = manifest.VariableList.FirstOrDefault(v => v.Key == source.VersionVar);
        if (version.GameVersions is not [var gameVersion] || versionVariable is null || VariableValues.Check(versionVariable, gameVersion) is not null
            || gameVersion == MinecraftServerStep.Latest)
        {
            return ModpackErrors.GameVersionInvalid;
        }

        var file = version.Files.FirstOrDefault(f => f.Primary) ?? FirstOf(version.Files);
        if (file is null || !file.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) || file.Sha512.Length != 128
            || !file.Sha512.All(char.IsAsciiHexDigit))
        {
            return ModpackErrors.FileInvalid;
        }

        return new PackPlan(type.Type, type.Loader, gameVersion, file with { Sha512 = file.Sha512.ToLowerInvariant() });
    }

    private static ModSourceDefinition? PackSource(GameManifest manifest) => manifest.ModList.FirstOrDefault(s => s.Modpacks is not null);

    /// <summary>The loaders of the game's mod types that packs can be made for.</summary>
    private static List<string> PackLoaders(ModSourceDefinition source) =>
        [.. source.Types.Values.Where(t => t.ProjectType == "mod").SelectMany(t => t.Loaders).Where(s_modpackLoaders.Contains).Distinct(StringComparer.Ordinal)];

    private static ModpackView? ModpackViewOf(ServerModpack? pack) => pack is null
        ? null
        : new ModpackView(pack.ProjectId, pack.VersionId, pack.Name, pack.VersionNumber, pack.ServerType, pack.Loader, pack.GameVersion, pack.LoaderVersion,
            pack.InstalledAt);
}
