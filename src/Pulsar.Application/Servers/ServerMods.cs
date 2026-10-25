using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

/// <param name="RequiredBy">The installed mods that need it.</param>
public sealed record ServerModView(
    Guid Id,
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    string Path,
    long Size,
    bool Dependency,
    ServerModState State,
    DateTimeOffset InstalledAt,
    IReadOnlyList<string> RequiredBy);

/// <summary>What a server takes from a mod catalog, and what it has.</summary>
/// <param name="Supported">The server's game takes files from a catalog (whatever its type).</param>
/// <param name="ProjectType">What its type takes: <c>mod</c> or <c>plugin</c>; null when its type takes nothing.</param>
/// <param name="GameVersion">The game's version the catalog's files are chosen for; null when not known yet.</param>
/// <param name="Unavailable">Why nothing can be added now (<c>mods.type_unsupported</c>, <c>mods.version_unknown</c>); null when it can.</param>
public sealed record ModsView(
    bool Supported,
    string? Catalog,
    string? ProjectType,
    string? Folder,
    IReadOnlyList<string> Loaders,
    string? GameVersion,
    string? Unavailable,
    IReadOnlyList<ServerModView> Installed);

public sealed record ModHitView(string ProjectId, string Slug, string Title, string? Description, string? Author, long Downloads, DateTimeOffset? Updated,
    bool Installed);

public sealed record ModSearchView(IReadOnlyList<ModHitView> Hits, int Total);

public sealed record ModVersionView(string Id, string Name, string VersionNumber, string VersionType, DateTimeOffset Published);

public sealed record ModVersionList(IReadOnlyList<ModVersionView> Versions);

public sealed record ModUpdateView(Guid ModId, string Name, string VersionNumber, string NewVersionId, string NewVersionNumber);

public sealed record ModUpdateList(IReadOnlyList<ModUpdateView> Updates);

/// <summary>
/// A server's mods and plugins from a catalog (Pulsar 1.0.9; docs/architecture/04, "Mods"): Modrinth, filtered by the
/// server's type (the loaders and folder its manifest names) and game version. Installing a project installs the newest
/// version for the server, or the one chosen, with the projects it requires; the node downloads the files, checked
/// against Modrinth's SHA-512, while the server is stopped. What is on the server is recorded when the change starts and
/// confirmed or rolled back with its operation.
/// </summary>
public sealed partial class ServerMods(
    IPulsarDbContext db,
    IModrinth modrinth,
    IAgentConnections connections,
    AgentSessionStore sessions,
    ServerOperations operations,
    IAuditLog audit,
    IRequestContext request,
    TimeProvider clock)
{
    public const int MaxModsPerServer = 300;
    public const int SearchPageSize = 20;

    /// <summary>Projects one install may bring: the one asked for and those it requires, recursively.</summary>
    public const int MaxProjectsPerInstall = 32;

    private sealed record Target(ModSourceDefinition Source, ModTargetDefinition Kind, string GameVersion, IReadOnlySet<string> Folders);

    private sealed record Planned(ModrinthVersion Version, ModrinthFile File, string Path, bool Dependency);

    private enum Change
    {
        Install,
        Remove,
        Update,
    }

    public async Task<Result<ModsView>> GetAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        await SettleAsync(server.Id, cancellationToken);
        var manifest = await ManifestAsync(server, cancellationToken);
        var installed = await ViewsAsync(server.Id, cancellationToken);
        if (FirstOf(manifest.ModList) is not { } source)
        {
            return new ModsView(false, null, null, null, [], null, ModErrors.Unsupported.Code, installed);
        }

        var values = Values(server, manifest);
        var kind = values.GetValueOrDefault(source.TypeVar) is { } type ? source.Types.GetValueOrDefault(type) : null;
        var version = GameVersion(server, source, values);
        var unavailable = kind is null ? ModErrors.TypeUnsupported.Code : version is null ? ModErrors.VersionUnknown.Code : null;
        return new ModsView(true, source.Source, kind?.ProjectType, kind?.Folder, kind?.Loaders ?? [], version, unavailable, installed);
    }

    /// <summary>The catalog's projects the server can take, by relevance, a page of <see cref="SearchPageSize"/>.</summary>
    public async Task<Result<ModSearchView>> SearchAsync(ServerId id, string? query, int offset, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var target = await TargetAsync(server, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        var text = query?.Trim();
        if (text is { Length: > 100 } || text?.Any(char.IsControl) == true)
        {
            return ModErrors.QueryInvalid;
        }

        return await CatalogAsync(async () =>
        {
            var found = await modrinth.SearchAsync(text, target.Value.Kind.ProjectType, target.Value.Kind.Loaders, target.Value.GameVersion,
                Math.Clamp(offset, 0, 10_000), SearchPageSize, cancellationToken);
            var installed = await db.ServerMods.AsNoTracking().Where(m => m.ServerId == server.Id).Select(m => m.ProjectId).ToListAsync(cancellationToken);
            return new ModSearchView([.. found.Hits.Select(h => new ModHitView(h.ProjectId, h.Slug, h.Title, h.Description, h.Author, h.Downloads, h.Updated,
                installed.Contains(h.ProjectId)))], found.Total);
        });
    }

    /// <summary>The project's versions the server can take, newest first.</summary>
    public async Task<Result<ModVersionList>> VersionsAsync(ServerId id, string projectId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var target = await TargetAsync(server, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        if (!IsCatalogId(projectId))
        {
            return ModErrors.NoVersion;
        }

        return await CatalogAsync(async () =>
        {
            var versions = await modrinth.VersionsAsync(projectId, target.Value.Kind.Loaders, target.Value.GameVersion, cancellationToken);
            return new ModVersionList([.. versions.Take(50).Select(v => new ModVersionView(v.Id, v.Name, v.VersionNumber, v.VersionType, v.Published))]);
        });
    }

    /// <summary>
    /// Installs the project (its newest version for the server, or <paramref name="versionId"/>), with the projects it
    /// requires that the server does not have; a project on the server already is replaced by that version.
    /// </summary>
    public async Task<Result<ModsView>> InstallAsync(ServerId id, string projectId, string? versionId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (!IsCatalogId(projectId) || (versionId is not null && !IsCatalogId(versionId)))
        {
            return ModErrors.NoVersion;
        }

        var prepared = await PrepareAsync(server, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        var (target, installed) = prepared.Value;
        var plan = await CatalogAsync(() => PlanAsync(target, installed, [(projectId, versionId, Dependency: false)], cancellationToken));
        if (!plan.IsSuccess)
        {
            return plan.Error!;
        }

        if (plan.Value.Count == 0)
        {
            return await GetAsync(id, cancellationToken); // that version is on the server already
        }

        return await ChangeAsync(server, installed, plan.Value, [], Change.Install, cancellationToken);
    }

    /// <summary>
    /// Brings mods to the newest version the catalog has for the server (all those with one, or <paramref name="modIds"/>),
    /// with any project a new version requires that the server does not have. A mod installed as another's dependency stays one.
    /// </summary>
    public async Task<Result<ModsView>> UpdateAsync(ServerId id, IReadOnlyList<ServerModId>? modIds, CancellationToken cancellationToken)
    {
        var server = await db.Servers.Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var prepared = await PrepareAsync(server, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        var (target, installed) = prepared.Value;
        if (modIds is not null && modIds.Any(m => installed.All(i => i.Id != m)))
        {
            return ModErrors.NotFound;
        }

        var plan = await CatalogAsync(async () =>
        {
            var chosen = installed.Where(m => modIds is null || modIds.Contains(m.Id)).ToList();
            var latest = chosen.Count == 0
                ? new Dictionary<string, ModrinthVersion>()
                : await modrinth.LatestVersionsAsync([.. chosen.Select(m => m.Sha512)], target.Kind.Loaders, target.GameVersion, cancellationToken);
            var updates = chosen.Where(m => latest.TryGetValue(m.Sha512, out var newer) && newer.Id != m.VersionId && newer.ProjectId == m.ProjectId)
                .Select(m => (m.ProjectId, (string?)latest[m.Sha512].Id, m.Dependency)).ToList();
            return updates.Count == 0 ? ModErrors.NoUpdates : await PlanAsync(target, installed, updates, cancellationToken);
        });
        if (!plan.IsSuccess)
        {
            return plan.Error!;
        }

        return await ChangeAsync(server, installed, plan.Value, [], Change.Update, cancellationToken);
    }

    /// <summary>Removes a mod; one another installed mod requires stays (<c>mods.required_by</c>).</summary>
    public async Task<Result<ModsView>> RemoveAsync(ServerId id, ServerModId modId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        await SettleAsync(server.Id, cancellationToken);
        var installed = await db.ServerMods.Where(m => m.ServerId == server.Id).ToListAsync(cancellationToken);
        if (installed.FirstOrDefault(m => m.Id == modId) is not { } mod)
        {
            return ModErrors.NotFound;
        }

        if (installed.Any(m => m.State != ServerModState.Installed))
        {
            return ModErrors.ChangeRunning;
        }

        if (installed.FirstOrDefault(m => m.Id != mod.Id && m.Requires.Contains(mod.ProjectId)) is { } dependent)
        {
            return ModErrors.RequiredBy(mod.Name, dependent.Name);
        }

        return await ChangeAsync(server, installed, [], [mod], Change.Remove, cancellationToken);
    }

    /// <summary>The installed mods with a newer version for the server, as the catalog says (looked up by their files' SHA-512).</summary>
    public async Task<Result<ModUpdateList>> UpdatesAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().Include(s => s.Variables).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var target = await TargetAsync(server, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        var installed = await db.ServerMods.AsNoTracking().Where(m => m.ServerId == server.Id && m.State == ServerModState.Installed)
            .ToListAsync(cancellationToken);
        if (installed.Count == 0)
        {
            return new ModUpdateList([]);
        }

        return await CatalogAsync(async () =>
        {
            var latest = await modrinth.LatestVersionsAsync([.. installed.Select(m => m.Sha512)], target.Value.Kind.Loaders, target.Value.GameVersion,
                cancellationToken);
            return new ModUpdateList([.. installed
                .Where(m => latest.TryGetValue(m.Sha512, out var newer) && newer.Id != m.VersionId && newer.ProjectId == m.ProjectId)
                .Select(m => new ModUpdateView(m.Id.Value, m.Name, m.VersionNumber, latest[m.Sha512].Id, latest[m.Sha512].VersionNumber))]);
        });
    }

    /// <summary>
    /// A finished operation's outcome on the mods it changed: those it added are installed and those it deleted gone, or,
    /// when it did not succeed, the other way round. Not saved.
    /// </summary>
    public static async Task CompletedAsync(IPulsarDbContext db, Operation operation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Type != OperationType.ModsChange || operation.IsActive)
        {
            return;
        }

        var changed = await db.ServerMods.Where(m => m.OperationId == operation.Id).ToListAsync(cancellationToken);
        var succeeded = operation.Status == OperationStatus.Succeeded;
        foreach (var mod in changed)
        {
            switch (mod.State, succeeded)
            {
                case (ServerModState.Installing, true):
                    mod.Installed(now);
                    break;
                case (ServerModState.Removing, true) or (ServerModState.Installing, false):
                    db.ServerMods.Remove(mod);
                    break;
                case (ServerModState.Removing, false):
                    mod.Kept(now);
                    break;
            }
        }
    }

    /// <summary>
    /// Mods left changing by an operation that ended unheard of (a panel that stopped meanwhile) take its outcome; those
    /// whose operation is gone (deleted with the operations' history) are taken as not changed.
    /// </summary>
    private async Task SettleAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        var ended = await db.Operations.AsNoTracking()
            .Where(o => o.ServerId == serverId && o.Type == OperationType.ModsChange && o.Status != OperationStatus.Queued
                && o.Status != OperationStatus.Running && db.ServerMods.Any(m => m.OperationId == o.Id))
            .ToListAsync(cancellationToken);
        var orphans = await db.ServerMods.Where(m => m.ServerId == serverId && m.State != ServerModState.Installed && m.OperationId == null)
            .ToListAsync(cancellationToken);
        if (ended.Count == 0 && orphans.Count == 0)
        {
            return;
        }

        foreach (var operation in ended)
        {
            await CompletedAsync(db, operation, clock.GetUtcNow(), cancellationToken);
        }

        foreach (var orphan in orphans)
        {
            if (orphan.State == ServerModState.Installing)
            {
                db.ServerMods.Remove(orphan);
            }
            else
            {
                orphan.Kept(clock.GetUtcNow());
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The node's report settled them meanwhile.
        }

        db.ChangeTracker.Clear();
    }

    private async Task<Result<(Target Target, List<ServerMod> Installed)>> PrepareAsync(Server server, CancellationToken cancellationToken)
    {
        await SettleAsync(server.Id, cancellationToken);
        var target = await TargetAsync(server, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        var installed = await db.ServerMods.Where(m => m.ServerId == server.Id).ToListAsync(cancellationToken);
        if (installed.Any(m => m.State != ServerModState.Installed))
        {
            return ModErrors.ChangeRunning;
        }

        return (target.Value, installed);
    }

    /// <summary>
    /// What an install or update brings: each version asked for (or the project's newest for the server, a release when
    /// there is one) and, recursively, the newest of each project it requires that the server does not have; one that is
    /// installed in that version already is left out. <c>Dependency</c> says how each asked-for project is recorded.
    /// </summary>
    private async Task<Result<List<Planned>>> PlanAsync(Target target, IReadOnlyList<ServerMod> installed,
        IReadOnlyList<(string Project, string? Version, bool Dependency)> asked, CancellationToken cancellationToken)
    {
        var plan = new List<Planned>();
        var pending = new Queue<(string Project, string? Version, bool Dependency, string? NeededBy)>(asked.Select(a => (a.Project, a.Version, a.Dependency,
            (string?)null)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var next))
        {
            var version = await ChooseAsync(target, next.Project, next.Version, cancellationToken);
            if (version is null)
            {
                return next.NeededBy is { } neededBy ? ModErrors.RequirementMissing(neededBy) : ModErrors.NoVersion;
            }

            if (!seen.Add(version.ProjectId))
            {
                continue;
            }

            var existing = installed.FirstOrDefault(m => m.ProjectId == version.ProjectId);
            if ((next.NeededBy is not null && existing is not null) || existing?.VersionId == version.Id)
            {
                continue; // a requirement the server has (any version will do), or that version is installed
            }

            var file = version.Files.FirstOrDefault(f => f.Primary) ?? FirstOf(version.Files);
            if (file is null || !ModFiles.IsFileName(file.FileName) || file.Sha512.Length != 128 || !file.Sha512.All(char.IsAsciiHexDigit))
            {
                return ModErrors.FileInvalid;
            }

            plan.Add(new Planned(version, file, $"{target.Kind.Folder}/{file.FileName}", next.Dependency));
            if (plan.Count > MaxProjectsPerInstall)
            {
                return ModErrors.TooMany;
            }

            foreach (var dependency in version.Dependencies)
            {
                if (dependency.Type == "incompatible" && dependency.ProjectId is { } other && installed.FirstOrDefault(m => m.ProjectId == other) is { } clash)
                {
                    return ModErrors.Incompatible(version.Name, clash.Name);
                }

                if (dependency.Type == "required" && (dependency.ProjectId ?? dependency.VersionId) is { } wanted && IsCatalogId(wanted))
                {
                    var project = dependency.ProjectId ?? await ProjectOfAsync(wanted, cancellationToken) ?? wanted;
                    pending.Enqueue((project, dependency.VersionId, true, version.Name));
                }
            }
        }

        return plan;
    }

    /// <summary>The version to install: the one asked for when it suits the server, else the project's newest that does (a release first).</summary>
    private async Task<ModrinthVersion?> ChooseAsync(Target target, string projectId, string? versionId, CancellationToken cancellationToken)
    {
        if (versionId is not null && await modrinth.VersionAsync(versionId, cancellationToken) is { } asked
            && (asked.ProjectId == projectId || asked.Id == versionId) && Suits(target, asked))
        {
            return asked;
        }

        var versions = await modrinth.VersionsAsync(projectId, target.Kind.Loaders, target.GameVersion, cancellationToken);
        var suitable = versions.Where(v => Suits(target, v)).ToList();
        return suitable.FirstOrDefault(v => v.VersionType == "release") ?? suitable.FirstOrDefault();
    }

    private async Task<string?> ProjectOfAsync(string versionId, CancellationToken cancellationToken) =>
        (await modrinth.VersionAsync(versionId, cancellationToken))?.ProjectId;

    private static bool Suits(Target target, ModrinthVersion version) =>
        version.GameVersions.Contains(target.GameVersion) && version.Loaders.Any(l => target.Kind.Loaders.Contains(l));

    /// <summary>Records the change and has the node make it: <paramref name="add"/> put on the server, <paramref name="remove"/> deleted.</summary>
    private async Task<Result<ModsView>> ChangeAsync(Server server, List<ServerMod> installed, IReadOnlyList<Planned> add, IReadOnlyList<ServerMod> remove,
        Change change, CancellationToken cancellationToken)
    {
        // The game holds its mods open while it runs (Windows does not delete or replace a file in use).
        if (server.RuntimeState is not (ServerRuntimeState.Offline or ServerRuntimeState.Crashed))
        {
            return ServerErrors.MustBeStopped;
        }

        if (connections.Find(server.NodeId) is not { } agent || !sessions.IsActive(agent.SessionId))
        {
            return NodeErrors.NotConnected;
        }

        if (!agent.Capabilities.Contains(AgentCapabilities.ChangeMods))
        {
            return ModErrors.Unsupported with { Message = "The server's node has an agent too old to install mods: update it." };
        }

        // A project installed in another version is replaced; another project in the same file cannot be.
        var replaced = installed.Where(m => add.Any(a => a.Version.ProjectId == m.ProjectId)).ToList();
        foreach (var planned in add)
        {
            if (installed.FirstOrDefault(m => string.Equals(m.Path, planned.Path, StringComparison.OrdinalIgnoreCase) && m.ProjectId != planned.Version.ProjectId)
                is { } taken)
            {
                return ModErrors.PathTaken(taken.Path);
            }
        }

        var leaving = remove.Concat(replaced).ToList();
        if (installed.Count - leaving.Count + add.Count > MaxModsPerServer || add.Count + leaving.Count > ChangeModsCommand.MaxFiles)
        {
            return ModErrors.TooMany;
        }

        var now = clock.GetUtcNow();
        var operation = Operation.Create(OperationType.ModsChange, server.NodeId, server.Id, request.UserId, OperationRequester.User, now);
        var command = new ChangeModsCommand(server.Id.Value,
            [.. add.Select(a => new ModDownload(a.Path, a.File.Url, a.File.Sha512.ToLowerInvariant(), a.File.Size))],
            [.. leaving.Where(m => !add.Any(a => string.Equals(a.Path, m.Path, StringComparison.OrdinalIgnoreCase))).Select(m => m.Path)]);
        var names = add.Count == 0 ? [] : await NamesAsync(add, cancellationToken);
        var run = await operations.RunAsync(server, operation, command, beforeSave: () =>
        {
            foreach (var planned in add)
            {
                var requires = planned.Version.Dependencies.Where(d => d.Type == "required" && d.ProjectId is not null).Select(d => d.ProjectId!).ToList();
                db.ServerMods.Add(ServerMod.Installing(server.Id, ModSource.Modrinth, planned.Version.ProjectId, planned.Version.Id,
                    names.GetValueOrDefault(planned.Version.ProjectId, planned.Version.Name), planned.Version.VersionNumber, planned.Path,
                    planned.File.Sha512.ToLowerInvariant(), planned.File.Size, planned.Dependency, requires, operation.Id, now));
            }

            foreach (var mod in leaving)
            {
                mod.Removing(operation.Id, now);
            }

            audit.Record(change switch
            {
                Change.Install => AuditActions.Server.ModsInstallRequested,
                Change.Update => AuditActions.Server.ModsUpdateRequested,
                _ => AuditActions.Server.ModsRemoveRequested,
            }, metadata: new
            {
                added = add.Select(a => new { project = a.Version.ProjectId, version = a.Version.VersionNumber, file = a.Path }).ToArray(),
                removed = leaving.Select(m => new { project = m.ProjectId, version = m.VersionNumber, file = m.Path }).ToArray(),
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
            await CompletedAsync(db, run.Value, clock.GetUtcNow(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(server.Id, cancellationToken);
    }

    /// <summary>The projects' titles (a version's own name is often only its number).</summary>
    private async Task<Dictionary<string, string>> NamesAsync(IReadOnlyList<Planned> add, CancellationToken cancellationToken)
    {
        try
        {
            var projects = await modrinth.ProjectsAsync([.. add.Select(a => a.Version.ProjectId).Distinct(StringComparer.Ordinal)], cancellationToken);
            return projects.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
        }
        catch (ModCatalogUnavailableException)
        {
            return []; // their versions' names will do
        }
    }


    private async Task<Result<Target>> TargetAsync(Server server, CancellationToken cancellationToken)
    {
        var manifest = await ManifestAsync(server, cancellationToken);
        if (FirstOf(manifest.ModList) is not { } source)
        {
            return ModErrors.Unsupported;
        }

        var values = Values(server, manifest);
        if (values.GetValueOrDefault(source.TypeVar) is not { } type || source.Types.GetValueOrDefault(type) is not { } kind)
        {
            return ModErrors.TypeUnsupported;
        }

        return GameVersion(server, source, values) is { } version
            ? new Target(source, kind, version, ModFiles.Folders(manifest))
            : ModErrors.VersionUnknown;
    }

    /// <summary>The version the last install reported ("latest" stands for it), else the one set, unless that is "latest".</summary>
    private static string? GameVersion(Server server, ModSourceDefinition source, IReadOnlyDictionary<string, string?> values) =>
        server.GameVersion ?? (values.GetValueOrDefault(source.VersionVar) is { Length: > 0 } set && set != MinecraftServerStep.Latest ? set : null);

    private static IReadOnlyDictionary<string, string?> Values(Server server, GameManifest manifest) =>
        VariableValues.Resolve(manifest, server.Variables.Where(v => v.ProtectedValue is null).ToDictionary(v => v.Key, v => (string?)v.Value));

    private async Task<List<ServerModView>> ViewsAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        var mods = await db.ServerMods.AsNoTracking().Where(m => m.ServerId == serverId).OrderBy(m => m.Name).ToListAsync(cancellationToken);
        return [.. mods.Select(m => new ServerModView(m.Id.Value, m.ProjectId, m.VersionId, m.Name, m.VersionNumber, m.Path, m.Size, m.Dependency, m.State,
            m.InstalledAt, [.. mods.Where(o => o.Id != m.Id && o.Requires.Contains(m.ProjectId)).Select(o => o.Name)]))];
    }

    private async Task<GameManifest> ManifestAsync(Server server, CancellationToken cancellationToken) =>
        ServerSpecs.Parse(await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken));

    private static async Task<Result<T>> CatalogAsync<T>(Func<Task<Result<T>>> call)
    {
        try
        {
            return await call();
        }
        catch (ModCatalogUnavailableException ex)
        {
            return ex.Busy ? ModErrors.CatalogBusy : ModErrors.CatalogUnavailable(ex.Message);
        }
    }

    private static async Task<Result<T>> CatalogAsync<T>(Func<Task<T>> call) => await CatalogAsync<T>(async () => (Result<T>)await call());

    private static T? FirstOf<T>(IReadOnlyList<T> list) where T : class => list.Count > 0 ? list[0] : null;

    /// <summary>A catalog's id or slug: letters, digits, '-' and '_'.</summary>
    private static bool IsCatalogId(string? id) =>
        id is { Length: > 0 and <= ServerMod.MaxIdLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
