using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Application.Notifications;
using Pulsar.Application.Scheduling;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

/// <summary>Who plays on a running server, as its game's query last answered (the panel keeps it in memory).</summary>
public interface IServerPlayers
{
    /// <summary>Players online; null when unknown (no query, no recent answer, the server not running).</summary>
    int? Online(ServerId server);
}

/// <summary>For hosts without live metrics (tests of the application layer): nobody is known to play.</summary>
internal sealed class NoServerPlayers : IServerPlayers
{
    public int? Online(ServerId server) => null;
}

/// <param name="Supported">The game's updates can be checked (SteamCMD, Minecraft).</param>
/// <param name="CanWaitForEmpty">The game says who plays (its query), so an update can wait until nobody does.</param>
/// <param name="CanWarn">The game has a broadcast command, so the players can be warned.</param>
/// <param name="Checking">The node is asking now.</param>
/// <param name="Pending">An automatic update is due and waits (for the players to leave, for the server to be free).</param>
/// <param name="NotBefore">An automatic update waits for this: one starts at most once an hour.</param>
/// <param name="Running">The automatic update running now (or the last one): of the game, or to a newer template.</param>
/// <param name="TemplatePolicy">What a newer version of the game's template does (1.0.10).</param>
/// <param name="NewerTemplate">The newest version of the game's template above the server's, when the panel has one.</param>
/// <param name="TemplatePending">An automatic move to <paramref name="AutoTemplate"/> is due and waits.</param>
/// <param name="AutoTemplate">
/// The version an automatic move would go to: the newest one a Pulsar update brought (not an imported one, not a
/// pre-release), when it is above the server's.
/// </param>
public sealed record GameUpdateView(
    GameUpdatePolicy Policy,
    bool WhenEmpty,
    int WarningMinutes,
    string WarningMessage,
    bool Supported,
    bool CanWaitForEmpty,
    bool CanWarn,
    string? Installed,
    string? Latest,
    bool? Available,
    DateTimeOffset? CheckedAt,
    string? CheckErrorCode,
    string? CheckError,
    bool Checking,
    DateTimeOffset? NextCheckAt,
    bool Pending,
    int? PlayersOnline,
    GameUpdateRunView? Running,
    DateTimeOffset? NotBefore = null,
    GameUpdatePolicy TemplatePolicy = GameUpdatePolicy.Notify,
    string? NewerTemplate = null,
    bool TemplatePending = false,
    string? AutoTemplate = null);

/// <param name="Step">The step being done, from 0.</param>
/// <param name="Template">A move to a newer template (1.0.10); false: a game update.</param>
public sealed record GameUpdateRunView(Guid Id, SequenceStatus Status, int Step, int Steps, SequenceStepKind? StepKind, DateTimeOffset? WakeAt,
    DateTimeOffset CreatedAt, string? ErrorCode, string? Message, bool Template = false);

/// <summary>
/// A server's game updates (Pulsar 1.0.9): every <see cref="GameUpdateWatch.CheckInterval"/> the server's node asks the
/// publisher (Steam through SteamCMD, Mojang) whether there is something newer than what the server has. A new one is
/// shown and notified once; with <see cref="GameUpdatePolicy.Install"/> it is installed as a sequence
/// (<see cref="ServerSequences"/>): the players are warned, the server stops (when nobody plays, if asked), updates and
/// starts again if it was running. Installing by itself needs the rights it uses (update, stop, start, and console
/// commands to warn): it cannot do what its author could not. Since 1.0.10 a newer version of the server's game template
/// (a Pulsar update brings them, or an administrator imports one) is followed the same way
/// (<see cref="GameUpdateWatch.TemplatePolicy"/>): notified once, or moved to by itself with the same warnings. Only
/// the versions Pulsar's updates bring are moved to by themselves (an imported one is reviewed and moved to by hand),
/// and only once the move's own checks pass: nobody is warned or stopped for a move that could not be made.
/// </summary>
public sealed partial class GameUpdates(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    IServerPlayers players,
    ServerManagement servers,
    TemplateCatalog templates,
    IPermissionEvaluator permissions,
    IRowLocks locks,
    Notifier notifier,
    SequenceSignal signal,
    IAuditLog audit,
    IRequestContext request,
    TimeProvider clock,
    ILogger<GameUpdates> logger)
{
    /// <summary>Who acts for automatic updates in the audit log.</summary>
    public const string ActorName = "Game updates";

    /// <summary>Checks sent per pass: each one waits for its node's ack.</summary>
    public const int ChecksPerPass = 20;

    /// <summary>A node that refused a check because the server was busy is asked again after this.</summary>
    private static readonly TimeSpan s_busyRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A check asked by hand is not sent again while one is on its way or one answered this recently: each one signs in
    /// to Steam, and a node runs two at a time for all its servers.
    /// </summary>
    private static readonly TimeSpan s_manualCheckEvery = TimeSpan.FromMinutes(1);

    /// <summary>A game whose updates cannot be checked is looked at again only this rarely (its template may change).</summary>
    private static readonly TimeSpan s_unsupportedRetry = TimeSpan.FromDays(1);

    public async Task<Result<GameUpdateView>> GetAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var watch = await db.GameUpdateWatches.AsNoTracking().SingleOrDefaultAsync(w => w.ServerId == id, cancellationToken)
            ?? GameUpdateWatch.Create(id, clock.GetUtcNow());
        return await ViewAsync(server, watch, cancellationToken);
    }

    /// <param name="templatePolicy">What a newer template version does; null keeps what is set.</param>
    public async Task<Result<GameUpdateView>> ChangeAsync(ServerId id, GameUpdatePolicy policy, bool whenEmpty, int warningMinutes, string? warningMessage,
        CancellationToken cancellationToken, GameUpdatePolicy? templatePolicy = null)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var stored = await db.GameUpdateWatches.AsNoTracking().SingleOrDefaultAsync(w => w.ServerId == id, cancellationToken);
        var template = templatePolicy ?? stored?.TemplatePolicy ?? GameUpdatePolicy.Notify;
        var manifest = await ManifestAsync(server, cancellationToken);
        // A game whose updates cannot be checked keeps what it has (its templates are followed all the same).
        if (policy != GameUpdatePolicy.Off && policy != (stored?.Policy ?? GameUpdatePolicy.Notify) && !IsCheckable(manifest))
        {
            return ServerErrors.GameUpdatesUnsupported;
        }

        var installs = policy == GameUpdatePolicy.Install || template == GameUpdatePolicy.Install;
        if (installs && whenEmpty && manifest.Query is null)
        {
            return ServerErrors.WhenEmptyUnsupported;
        }

        // It acts as the game updates later: whoever sets it must be allowed what it does.
        if (installs && !await MayInstallAsync(server, warningMinutes > 0 && manifest.Commands?.Broadcast is not null, cancellationToken))
        {
            return ServerErrors.GameUpdatesForbidden;
        }

        return await RetriedAsync<GameUpdateView>(async () =>
        {
            var watch = await TrackedAsync(id, cancellationToken);
            var before = (watch.Policy, watch.TemplatePolicy, watch.WhenEmpty, watch.WarningMinutes, watch.WarningMessage);
            var changed = watch.Change(policy, template, whenEmpty, warningMinutes, warningMessage, clock.GetUtcNow());
            if (!changed.IsSuccess)
            {
                return changed.Error!;
            }

            if (before != (watch.Policy, watch.TemplatePolicy, watch.WhenEmpty, watch.WarningMinutes, watch.WarningMessage))
            {
                audit.Record(AuditActions.Server.GameUpdatesChanged,
                        metadata: new
                        {
                            policy = watch.Policy.ToString(),
                            templatePolicy = watch.TemplatePolicy.ToString(),
                            watch.WhenEmpty,
                            watch.WarningMinutes,
                            watch.WarningMessage,
                        })
                    .WithServer(server.Id, server.NodeId);
            }

            await db.SaveChangesAsync(cancellationToken);
            signal.Notify();
            return await ViewAsync(server, watch, cancellationToken);
        });
    }

    /// <summary>Asks the node now (the next pass sends it).</summary>
    public async Task<Result<GameUpdateView>> CheckNowAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (!IsCheckable(await ManifestAsync(server, cancellationToken)))
        {
            return ServerErrors.GameUpdatesUnsupported;
        }

        if (Connection(server.NodeId) is not { } agent)
        {
            return NodeErrors.NotConnected;
        }

        if (!agent.Capabilities.Contains(AgentCapabilities.CheckGameUpdate))
        {
            return ServerErrors.GameUpdateCheckUnsupported;
        }

        // A node asked twice (the first attempt lost a race) answers the second at once that it is on it.
        return await RetriedAsync<GameUpdateView>(async () =>
        {
            var watch = await TrackedAsync(id, cancellationToken);
            var now = clock.GetUtcNow();
            if ((watch.CheckRequestedAt is { } asked && now - asked < GameUpdateWatch.AnswerTimeout)
                || (watch.CheckedAt is { } answered && now - answered < s_manualCheckEvery))
            {
                return await ViewAsync(server, watch, cancellationToken); // on its way, or just answered
            }

            watch.CheckSoon(now);
            await SendCheckAsync(server, watch, agent, cancellationToken);
            audit.Record(AuditActions.Server.GameUpdateCheckRequested).WithServer(server.Id, server.NodeId);
            await db.SaveChangesAsync(cancellationToken);
            return await ViewAsync(server, watch, cancellationToken);
        });
    }

    /// <summary>
    /// Installs the update now as an automatic one does (warning the players, stopping, updating, starting again), without
    /// waiting for anyone to leave. Needs the rights the sequence uses.
    /// </summary>
    public async Task<Result<GameUpdateView>> InstallNowAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var manifest = await ManifestAsync(server, cancellationToken);
        if (manifest.Update is null)
        {
            return ServerErrors.UpdateUnsupported;
        }

        var watch = await TrackedAsync(id, cancellationToken);
        if (!await MayInstallAsync(server, watch.WarningMinutes > 0 && manifest.Commands?.Broadcast is not null, cancellationToken))
        {
            return ServerErrors.GameUpdatesForbidden;
        }

        var started = await StartSequenceAsync(server, manifest, watch, cancellationToken, byItself: false);
        if (!started.IsSuccess)
        {
            return started.Error!;
        }

        return await ViewAsync(server, watch, cancellationToken);
    }

    /// <summary>
    /// Cancels the automatic update: what is left of it is not done, the update it started is cancelled, and the server is
    /// started again if it was running (a cancel never leaves it stopped). One that waits to start is skipped.
    /// </summary>
    public async Task<Result<GameUpdateView>> CancelAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        return await RetriedAsync<GameUpdateView>(async () =>
        {
            var watch = await TrackedAsync(id, cancellationToken);
            var sequence = watch.SequenceId is { } running
                ? await db.ServerSequences.SingleOrDefaultAsync(s => s.Id == running, cancellationToken)
                : null;
            var now = clock.GetUtcNow();
            var operation = sequence?.OperationId;
            string? template = null;
            if (sequence is null || !sequence.Cancel(now).IsSuccess)
            {
                // Not started yet (waiting for the players): this build, or this template, is not installed by itself.
                if (watch.AutoUpdateDue)
                {
                    watch.Skip(now);
                }
                else if ((await TemplatesAboveAsync(server, cancellationToken)).Auto is { } newer && watch.TemplateUpgradeDue(newer.Version))
                {
                    watch.SkipTemplate(newer.Version, now);
                    template = newer.Version;
                }
                else
                {
                    return ScheduleErrors.SequenceOver;
                }

                operation = null;
            }
            else if (sequence.Origin == SequenceOrigin.TemplateUpdate)
            {
                // Its provisioning and install go on to their end: a server left half moved would not start.
                template = watch.AttemptedTemplate;
                operation = null;
            }

            audit.Record(AuditActions.Server.GameUpdateCancelled, metadata: template is null ? new { latest = watch.Latest } : new { template })
                .WithServer(server.Id, server.NodeId);
            await db.SaveChangesAsync(cancellationToken);
            if (operation is { } inFlight && await db.Operations.AsNoTracking()
                    .AnyAsync(o => o.Id == inFlight && (o.Status == OperationStatus.Queued || o.Status == OperationStatus.Running), cancellationToken))
            {
                // The update it started: the steps that always run (the start) wait for it to end.
                await servers.CancelOperationAsync(server.Id, inFlight.Value, cancellationToken);
            }

            signal.Notify();
            return await ViewAsync(server, watch, cancellationToken);
        });
    }

    /// <summary>
    /// One pass (the scheduler's loop): asks the nodes whose servers are due a check, and starts the automatic updates
    /// whose servers allow it now. Returns how many checks it sent.
    /// </summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await AddMissingAsync(now, cancellationToken);
        await EndStaleAsync(now, cancellationToken);
        var due = await db.GameUpdateWatches.AsNoTracking()
            .Where(w => w.Policy != GameUpdatePolicy.Off && w.NextCheckAt <= now)
            .OrderBy(w => w.NextCheckAt)
            .Take(ChecksPerPass * 4)
            .Select(w => w.ServerId)
            .ToListAsync(cancellationToken);
        var sent = 0;
        foreach (var id in due)
        {
            if (sent >= ChecksPerPass)
            {
                break;
            }

            // Each watch on its own: one a user changes meanwhile is left to the next pass, the others go on.
            db.ChangeTracker.Clear();
            var watch = await db.GameUpdateWatches.SingleOrDefaultAsync(w => w.ServerId == id, cancellationToken);
            if (watch is null || !watch.CheckDue(now))
            {
                continue;
            }

            try
            {
                if (await AskAsync(watch, now, cancellationToken))
                {
                    sent++;
                }

                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another pass, the node's answer or a user got there first: the next pass looks again.
            }
            catch (DomainErrorException ex)
            {
                // Its template no longer passes this panel's checks: the others go on, and it is looked at again only rarely
                // (it would stay due and fill the passes otherwise).
                LogServerSkipped(id, ex.Error.Code);
                await PostponeAsync(id, now + s_unsupportedRetry, cancellationToken);
            }
        }

        db.ChangeTracker.Clear();
        await StartDueUpdatesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        await TemplatesDueAsync(cancellationToken);
        return sent;
    }

    /// <summary>Asks the watch's node for a check, or notes why not now. True when a check was sent.</summary>
    private async Task<bool> AskAsync(GameUpdateWatch watch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == watch.ServerId, cancellationToken);
        if (server is null || server.InstallState != InstallState.Installed || server.ActiveOperationType is OperationType.ServerInstall
                or OperationType.ServerProvision or OperationType.ServerUpdate or OperationType.BackupRestore)
        {
            watch.Postpone(now + GameUpdateWatch.CheckInterval); // nothing to ask about now: next time
            return false;
        }

        if (!IsCheckable(await ManifestAsync(server, cancellationToken)))
        {
            watch.Checked(null, null, null, ServerErrors.GameUpdatesUnsupported.Code, ServerErrors.GameUpdatesUnsupported.Message, now);
            watch.Postpone(now + s_unsupportedRetry);
            return false;
        }

        if (Connection(server.NodeId) is not { } agent || !agent.Capabilities.Contains(AgentCapabilities.CheckGameUpdate))
        {
            watch.Postpone(now + s_busyRetry); // asked once the node (or its agent) can answer
            return false;
        }

        await SendCheckAsync(server, watch, agent, cancellationToken);
        return true;
    }

    /// <summary>
    /// Watches still pointing at an automatic update that ended (a panel that stopped before it recorded the end, or lost a
    /// race doing so) are freed: what is installed is checked again, and a newer build can be installed.
    /// </summary>
    private async Task EndStaleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stale = await db.GameUpdateWatches
            .Where(w => w.SequenceId != null && db.ServerSequences.Any(s => s.Id == w.SequenceId && s.Status != SequenceStatus.Running))
            .Take(ChecksPerPass)
            .ToListAsync(cancellationToken);
        foreach (var watch in stale)
        {
            watch.AutoUpdateEnded(now);
        }

        if (stale.Count > 0)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The next pass.
            }

            db.ChangeTracker.Clear();
        }
    }

    /// <summary>What a node's check found (<see cref="IAgentHub.GameUpdateChecked"/>), for a server of that node.</summary>
    public async Task ReportedAsync(NodeId node, GameUpdateCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == new ServerId(check.ServerId) && s.NodeId == node,
            cancellationToken);
        if (server is null)
        {
            return; // not this node's
        }

        var due = await RetriedAsync<bool>(async () =>
        {
            var watch = await TrackedAsync(server.Id, cancellationToken);
            if (!watch.AwaitsAnswer)
            {
                LogUnasked(server.Id);
                return false; // nobody asked (or a second answer to the same question): a node does not decide when it is checked
            }

            var now = clock.GetUtcNow();
            // A node is not trusted with what browsers and notifications show: only short printable text.
            var fresh = watch.Checked(Text(check.Installed, GameUpdateWatch.MaxBuildLength), Text(check.Latest, GameUpdateWatch.MaxBuildLength),
                check.ErrorCode is null ? check.Available : null, Code(check.ErrorCode), Text(check.Message, GameUpdateWatch.MaxMessageLength), now);
            LogChecked(server.Id, watch.Installed, watch.Latest, watch.Available, watch.CheckErrorCode);
            if (fresh)
            {
                await notifier.GameUpdateAvailableAsync(server, watch.Installed, watch.Latest!, watch.Policy == GameUpdatePolicy.Install,
                    cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            return watch.AutoUpdateDue;
        });
        if (due is { IsSuccess: true, Value: true })
        {
            signal.Notify();
        }
    }

    /// <summary>A sequence ended: an automatic update's outcome is recorded and notified.</summary>
    public async Task SequenceEndedAsync(ServerSequence sequence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.Origin is not (SequenceOrigin.GameUpdate or SequenceOrigin.TemplateUpdate))
        {
            return;
        }

        await RetriedAsync<bool>(async () =>
        {
            var watch = await db.GameUpdateWatches.SingleOrDefaultAsync(w => w.ServerId == sequence.ServerId, cancellationToken);
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sequence.ServerId, cancellationToken);
            if (watch is null || server is null || watch.SequenceId != sequence.Id)
            {
                return false;
            }

            watch.AutoUpdateEnded(clock.GetUtcNow());
            var error = sequence.Message ?? sequence.ErrorCode;
            switch (sequence.Status, sequence.Origin)
            {
                case (SequenceStatus.Failed, SequenceOrigin.TemplateUpdate):
                    await notifier.TemplateUpdateFailedAsync(server, watch.AttemptedTemplate, error, cancellationToken);
                    break;
                case (SequenceStatus.Succeeded, SequenceOrigin.TemplateUpdate):
                    await notifier.TemplateUpdateInstalledAsync(server, watch.AttemptedTemplate, cancellationToken);
                    break;
                case (SequenceStatus.Failed, _):
                    await notifier.GameUpdateFailedAsync(server, watch.Latest, error, cancellationToken);
                    break;
                case (SequenceStatus.Succeeded, _):
                    await notifier.GameUpdateInstalledAsync(server, watch.Latest, cancellationToken);
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
            return true;
        });
        signal.Notify();
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> again, on what is stored now, when another writer changed the watch meanwhile (the
    /// pass asking its node, the node answering, a user): each attempt starts from a clean change tracker.
    /// </summary>
    private async Task<Result<T>> RetriedAsync<T>(Func<Task<Result<T>>> attempt)
    {
        for (var tries = 1; ; tries++)
        {
            try
            {
                return await attempt();
            }
            catch (DbUpdateConcurrencyException) when (tries < 4)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>The steps of an update as the watch asks: warnings while the game runs, stop, update, start again if it ran.</summary>
    public static IReadOnlyList<SequenceStep> Steps(GameUpdateWatch watch, bool canWarn, bool running)
    {
        var steps = Warnings(watch, canWarn, running);
        steps.Add(new SequenceStep(SequenceStepKind.Stop));
        steps.Add(new SequenceStep(SequenceStepKind.Update));
        // Back online even when the update failed: SteamCMD leaves the previous build in place.
        steps.Add(new SequenceStep(SequenceStepKind.Start, OnlyIfWasRunning: true, Always: true));
        return steps;
    }

    /// <summary>
    /// The steps of a move to the template <paramref name="templateId"/> (1.0.10): the same warnings, stop, the move (the
    /// server provisioned and installed again with it), start again if it ran, after a cancel too (after a failed move the
    /// server is not installed and cannot start: the move's failure is what the sequence reports).
    /// </summary>
    public static IReadOnlyList<SequenceStep> TemplateSteps(GameUpdateWatch watch, bool canWarn, bool running, Guid templateId)
    {
        var steps = Warnings(watch, canWarn, running);
        steps.Add(new SequenceStep(SequenceStepKind.Stop));
        steps.Add(new SequenceStep(SequenceStepKind.UpgradeTemplate, templateId.ToString()));
        steps.Add(new SequenceStep(SequenceStepKind.Start, OnlyIfWasRunning: true, Always: true));
        return steps;
    }

    /// <summary>The players warned while the game runs (5 min, 1 min, 10 s, as the watch says), when the game can tell them.</summary>
    private static List<SequenceStep> Warnings(GameUpdateWatch watch, bool canWarn, bool running)
    {
        ArgumentNullException.ThrowIfNull(watch);
        var steps = new List<SequenceStep>();
        if (canWarn && running && watch.WarningMinutes > 0)
        {
            var message = watch.WarningMessage ?? GameUpdateWatch.DefaultWarningMessage;
            string At(string time) => message.Replace(GameUpdateWatch.TimePlaceholder, time, StringComparison.Ordinal);
            var minutes = watch.WarningMinutes;
            if (minutes > 1)
            {
                steps.Add(new SequenceStep(SequenceStepKind.Broadcast, At($"{minutes.ToString(CultureInfo.InvariantCulture)} min")));
                steps.Add(new SequenceStep(SequenceStepKind.Wait, Seconds: (minutes - 1) * 60));
            }

            steps.Add(new SequenceStep(SequenceStepKind.Broadcast, At("1 min")));
            steps.Add(new SequenceStep(SequenceStepKind.Wait, Seconds: 50));
            steps.Add(new SequenceStep(SequenceStepKind.Broadcast, At("10 s")));
            steps.Add(new SequenceStep(SequenceStepKind.Wait, Seconds: 10));
        }

        return steps;
    }

    /// <summary>Starts the automatic updates whose servers allow it now: free, and empty when the watch waits for that.</summary>
    private async Task StartDueUpdatesAsync(CancellationToken cancellationToken)
    {
        var due = await db.GameUpdateWatches.AsNoTracking()
            .Where(w => w.Policy == GameUpdatePolicy.Install && w.Available == true && w.SequenceId == null)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var candidate in due.Where(w => w.AutoUpdateMayStart(now)))
        {
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == candidate.ServerId, cancellationToken);
            if (server is null || server.InstallState != InstallState.Installed || server.ActiveOperationId is not null
                || Connection(server.NodeId) is null)
            {
                continue; // later
            }

            var running = server.RuntimeState is ServerRuntimeState.Starting or ServerRuntimeState.Running;
            if (candidate.WhenEmpty && running && players.Online(server.Id) is not 0)
            {
                continue; // someone plays, or nobody can tell yet
            }

            db.ChangeTracker.Clear();
            var watch = await TrackedAsync(server.Id, cancellationToken);
            if (!watch.AutoUpdateMayStart(clock.GetUtcNow()))
            {
                continue;
            }

            GameManifest manifest;
            try
            {
                manifest = await ManifestAsync(server, cancellationToken);
            }
            catch (DomainErrorException ex)
            {
                // Its template no longer passes this panel's checks: this build is not installed by itself (a newer one is tried).
                LogServerSkipped(server.Id, ex.Error.Code);
                watch.Skip(clock.GetUtcNow());
                await SaveQuietlyAsync(cancellationToken);
                continue;
            }

            var started = await StartSequenceAsync(server, manifest, watch, cancellationToken);
            if (!started.IsSuccess && started.Error != ScheduleErrors.SequenceRunning)
            {
                LogAutoUpdateNotStarted(server.Id, started.Error!.Code);
            }
        }
    }

    /// <summary>Starts the update's sequence for the watch's server and records it (audited as the game updates when no user asks).</summary>
    /// <param name="byItself">An automatic update (counted in the hourly limit); false: a user asked for it.</param>
    private async Task<Result<ServerSequence>> StartSequenceAsync(Server server, GameManifest manifest, GameUpdateWatch watch,
        CancellationToken cancellationToken, bool byItself = true)
    {
        var now = clock.GetUtcNow();
        var running = server.RuntimeState is ServerRuntimeState.Starting or ServerRuntimeState.Running;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var started = await ServerSequences.StartAsync(db, locks, server, SequenceOrigin.GameUpdate, ActorName,
            Steps(watch, manifest.Commands?.Broadcast is not null, running), now, cancellationToken);
        if (!started.IsSuccess)
        {
            return started.Error!;
        }

        watch.AutoUpdateStarted(started.Value.Id, now, byItself);
        // By the user who asked, or by the game updates (the pass acts as them).
        audit.Record(AuditActions.Server.GameUpdateStarted, metadata: new { installed = watch.Installed, latest = watch.Latest })
            .WithServer(server.Id, server.NodeId);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ScheduleErrors.SequenceRunning; // the watch changed meanwhile: the next pass looks again
        }

        await transaction.CommitAsync(cancellationToken);
        LogAutoUpdateStarted(server.Id, watch.Installed, watch.Latest);
        signal.Notify();
        return started;
    }

    /// <summary>
    /// Newer template versions (1.0.10): notified once each, and moved to by themselves where the watch says so and the
    /// server allows it now (installed, free, its node connected, empty when the watch waits for that). The panel knows
    /// them without asking anyone: two queries a pass, then only the servers with something to do.
    /// </summary>
    private async Task TemplatesDueAsync(CancellationToken cancellationToken)
    {
        var watched = await (
                from w in db.GameUpdateWatches.AsNoTracking()
                where w.TemplatePolicy != GameUpdatePolicy.Off && w.SequenceId == null
                join s in db.Servers.AsNoTracking() on w.ServerId equals s.Id
                where s.InstallState == InstallState.Installed
                join t in db.GameTemplates.AsNoTracking() on s.TemplateId equals t.Id
                select new { w.ServerId, w.TemplatePolicy, w.NotifiedTemplate, w.AttemptedTemplate, t.Key, t.Version })
            .ToListAsync(cancellationToken);
        if (watched.Count == 0)
        {
            return;
        }

        var newest = await NewestTemplatesAsync(cancellationToken);
        foreach (var candidate in watched)
        {
            var (any, auto) = Above(newest, candidate.Key, candidate.Version);
            var notify = any is { } shown && shown.Version != candidate.NotifiedTemplate;
            var move = auto is { } target && candidate.TemplatePolicy == GameUpdatePolicy.Install && target.Version != candidate.AttemptedTemplate;
            if (!notify && !move)
            {
                continue; // nothing newer, or nothing left to do about it
            }

            try
            {
                await TemplateDueAsync(candidate.ServerId, candidate.Version, any, auto, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A user or another pass changed it: the next pass looks again.
            }
            catch (DomainErrorException ex)
            {
                LogServerSkipped(candidate.ServerId, ex.Error.Code); // its template no longer passes this panel's checks: the others go on
            }
        }
    }

    /// <summary>One server's newer template versions: notified, and moved to by itself when it may be now.</summary>
    private async Task TemplateDueAsync(ServerId id, string current, (TemplateId Id, string Version)? any, (TemplateId Id, string Version)? auto,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return;
        }

        var watch = await TrackedAsync(server.Id, cancellationToken);
        var now = clock.GetUtcNow();
        if (any is { } newer && watch.TemplateNoticed(newer.Version, now))
        {
            await notifier.TemplateUpdateAvailableAsync(server, current, newer.Version,
                watch.TemplatePolicy == GameUpdatePolicy.Install && auto?.Version == newer.Version, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (auto is not { } target || !watch.TemplateUpgradeMayStart(target.Version, now) || server.ActiveOperationId is not null
            || Connection(server.NodeId) is null)
        {
            return; // later, or never by itself
        }

        var running = server.RuntimeState is ServerRuntimeState.Starting or ServerRuntimeState.Running;
        if (watch.WhenEmpty && running && players.Online(server.Id) is not 0)
        {
            return; // someone plays, or nobody can tell yet
        }

        // The move's own checks first: nobody is warned or stopped for a move that could not be made. One that cannot is
        // not tried again by itself (a newer version is); the server's page offers it by hand, saying why.
        var rehearsed = await servers.RehearseTemplateUpgradeAsync(server.Id, target.Id.Value, cancellationToken);
        watch = await TrackedAsync(server.Id, cancellationToken);
        if (!watch.TemplateUpgradeMayStart(target.Version, clock.GetUtcNow()))
        {
            return;
        }

        if (rehearsed.Error is { } refusal)
        {
            // For now: the next pass looks again. A node whose agent does not read the newer version yet (its Pulsar update
            // comes after the panel's) moves once it does.
            if (refusal == ServerErrors.Busy || refusal == ServerErrors.NotFound || refusal == NodeErrors.NotConnected || refusal == ServerErrors.TemplateUnsupported)
            {
                return;
            }

            watch.SkipTemplate(target.Version, clock.GetUtcNow());
            await notifier.TemplateUpdateRefusedAsync(server, target.Version, refusal.Message, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            LogTemplateUpdateRefused(server.Id, target.Version, refusal.Code);
            return;
        }

        // The warnings use the game's broadcast command as the server's template has it; one the panel no longer reads (the
        // very servers a newer version mends) is moved without warning anyone.
        GameManifest? manifest;
        try
        {
            manifest = await ManifestAsync(server, cancellationToken);
        }
        catch (DomainErrorException)
        {
            manifest = null;
        }

        var started = await StartTemplateSequenceAsync(server, manifest, watch, target.Id.Value, target.Version, cancellationToken);
        if (!started.IsSuccess && started.Error != ScheduleErrors.SequenceRunning)
        {
            LogAutoUpdateNotStarted(server.Id, started.Error!.Code);
        }
    }

    /// <summary>A template version as the template updates compare them.</summary>
    private sealed record TemplateRow(TemplateId Id, string Key, string Version, TemplateSource Source);

    /// <summary>Every version of every game's template this panel offers.</summary>
    private async Task<List<TemplateRow>> NewestTemplatesAsync(CancellationToken cancellationToken)
    {
        var all = await db.GameTemplates.AsNoTracking().Select(t => new TemplateRow(t.Id, t.Key, t.Version, t.Source)).ToListAsync(cancellationToken);
        return [.. all.Where(t => templates.Offers(t.Key))];
    }

    /// <summary>
    /// Above <paramref name="version"/> of <paramref name="key"/>: the newest version (shown and notified), and the newest
    /// one an automatic move may go to: one a Pulsar update brought (bundled), not a pre-release. An imported version is
    /// the administrator's to review: servers are moved to it by hand.
    /// </summary>
    private static ((TemplateId Id, string Version)? Any, (TemplateId Id, string Version)? Auto) Above(List<TemplateRow> all, string key, string version)
    {
        var newer = all.Where(t => t.Key == key && GameTemplate.CompareVersions(t.Version, version) > 0)
            .OrderByDescending(t => t.Version, Comparer<string>.Create(GameTemplate.CompareVersions))
            .ToList();
        var any = newer.FirstOrDefault();
        var auto = newer.FirstOrDefault(t => t.Source == TemplateSource.Bundled && !t.Version.Contains('-', StringComparison.Ordinal));
        return (any is null ? null : (any.Id, any.Version), auto is null ? null : (auto.Id, auto.Version));
    }

    /// <summary>The server's newer template versions (<see cref="Above"/>).</summary>
    private async Task<((TemplateId Id, string Version)? Any, (TemplateId Id, string Version)? Auto)> TemplatesAboveAsync(Server server,
        CancellationToken cancellationToken)
    {
        var current = await db.GameTemplates.AsNoTracking().Where(t => t.Id == server.TemplateId).Select(t => new { t.Key, t.Version })
            .SingleAsync(cancellationToken);
        return Above(await NewestTemplatesAsync(cancellationToken), current.Key, current.Version);
    }

    /// <summary>Starts the move of the watch's server to the template <paramref name="templateId"/> and records it (audited as the game updates).</summary>
    private async Task<Result<ServerSequence>> StartTemplateSequenceAsync(Server server, GameManifest? manifest, GameUpdateWatch watch, Guid templateId,
        string version, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var running = server.RuntimeState is ServerRuntimeState.Starting or ServerRuntimeState.Running;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var started = await ServerSequences.StartAsync(db, locks, server, SequenceOrigin.TemplateUpdate, ActorName,
            TemplateSteps(watch, manifest?.Commands?.Broadcast is not null, running, templateId), now, cancellationToken);
        if (!started.IsSuccess)
        {
            return started.Error!;
        }

        watch.TemplateUpgradeStarted(started.Value.Id, version, now);
        audit.Record(AuditActions.Server.TemplateUpdateStarted, metadata: new { template = version }).WithServer(server.Id, server.NodeId);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ScheduleErrors.SequenceRunning; // the watch changed meanwhile: the next pass looks again
        }

        await transaction.CommitAsync(cancellationToken);
        LogTemplateUpdateStarted(server.Id, version);
        signal.Notify();
        return started;
    }

    /// <summary>
    /// Asks the node. The question is stored before it is sent: a node can answer before its ack is back, and an answer
    /// to a question the panel has no record of is not taken.
    /// </summary>
    private async Task SendCheckAsync(Server server, GameUpdateWatch watch, AgentConnectionInfo agent, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        watch.Asked(now);
        await db.SaveChangesAsync(cancellationToken);
        var ack = await connections.SendAsync(agent.NodeId, new CheckGameUpdateCommand(server.Id.Value), request.UserId, cancellationToken);
        if (ack?.Status is AckStatus.Accepted or AckStatus.AlreadyInState)
        {
            return;
        }

        // Not taken: the stored question is withdrawn, unless the node answered meanwhile (the next pass looks again then).
        await db.Entry(watch).ReloadAsync(cancellationToken);
        if (!watch.AwaitsAnswer)
        {
            return;
        }

        if (ack?.Status is AckStatus.Busy or null)
        {
            watch.NotAsked(now + s_busyRetry, now); // an install or update is running, or the node went away
        }
        else
        {
            watch.Checked(null, null, null, Code(ack.ErrorCode) ?? ServerErrors.CommandRejected.Code, Text(ack.Message, GameUpdateWatch.MaxMessageLength),
                now);
        }
    }

    private async Task<GameUpdateView> ViewAsync(Server server, GameUpdateWatch watch, CancellationToken cancellationToken)
    {
        var manifest = await ManifestAsync(server, cancellationToken);
        // The one running, or the last one with its outcome.
        var sequence = await db.ServerSequences.AsNoTracking()
            .Where(s => s.ServerId == server.Id && (s.Origin == SequenceOrigin.GameUpdate || s.Origin == SequenceOrigin.TemplateUpdate))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var (newer, auto) = await TemplatesAboveAsync(server, cancellationToken);
        var templatePending = auto is { } next && server.InstallState == InstallState.Installed && watch.TemplateUpgradeDue(next.Version);
        var now = clock.GetUtcNow();
        return new GameUpdateView(watch.Policy, watch.WhenEmpty, watch.WarningMinutes, watch.WarningMessage ?? GameUpdateWatch.DefaultWarningMessage,
            IsCheckable(manifest), manifest.Query is not null, manifest.Commands?.Broadcast is not null, watch.Installed, watch.Latest, watch.Available,
            watch.CheckedAt, watch.CheckErrorCode, watch.CheckError,
            watch.CheckRequestedAt is { } asked && now - asked < GameUpdateWatch.AnswerTimeout,
            watch.Policy == GameUpdatePolicy.Off ? null : watch.NextCheckAt, watch.AutoUpdateDue, players.Online(server.Id),
            sequence is null
                ? null
                : new GameUpdateRunView(sequence.Id.Value, sequence.Status, sequence.StepIndex, sequence.Steps.Count, sequence.Current?.Kind,
                    sequence.Status == SequenceStatus.Running ? sequence.WakeAt : null, sequence.CreatedAt, sequence.ErrorCode, sequence.Message,
                    sequence.Origin == SequenceOrigin.TemplateUpdate),
            (watch.AutoUpdateDue || templatePending) && watch.AutoUpdateNotBefore is { } notBefore && notBefore > now ? notBefore : null,
            watch.TemplatePolicy, newer?.Version, templatePending, auto?.Version);
    }

    /// <summary>Watches for installed servers that have none yet (checked and notified, the default).</summary>
    private async Task AddMissingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var missing = await db.Servers.AsNoTracking()
            .Where(s => s.InstallState == InstallState.Installed && !db.GameUpdateWatches.Any(w => w.ServerId == s.Id))
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var id in missing)
        {
            db.GameUpdateWatches.Add(GameUpdateWatch.Create(id, now));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); // another pass (or a user) added one meanwhile: next pass
        }
    }

    /// <summary>The server's watch is not looked at before <paramref name="until"/> (a write another one beats is left to the next pass).</summary>
    private async Task PostponeAsync(ServerId id, DateTimeOffset until, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        if (await db.GameUpdateWatches.SingleOrDefaultAsync(w => w.ServerId == id, cancellationToken) is { } watch)
        {
            watch.Postpone(until);
            await SaveQuietlyAsync(cancellationToken);
        }
    }

    private async Task SaveQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else wrote it meanwhile: the next pass looks again.
        }
    }

    private async Task<GameUpdateWatch> TrackedAsync(ServerId id, CancellationToken cancellationToken)
    {
        var watch = await db.GameUpdateWatches.SingleOrDefaultAsync(w => w.ServerId == id, cancellationToken);
        if (watch is null)
        {
            watch = GameUpdateWatch.Create(id, clock.GetUtcNow());
            db.GameUpdateWatches.Add(watch);
        }

        return watch;
    }

    private async Task<bool> MayInstallAsync(Server server, bool warns, CancellationToken cancellationToken)
    {
        if (request.UserId is not { } user)
        {
            return false;
        }

        var target = AccessTarget.ForServer(server.Id, server.NodeId);
        foreach (var permission in (string[])[Permissions.Server.Install, Permissions.Server.Stop, Permissions.Server.Start])
        {
            if (!await permissions.HasAsync(user, permission, target, cancellationToken))
            {
                return false;
            }
        }

        return !warns || await permissions.HasAsync(user, Permissions.Server.ConsoleWrite, target, cancellationToken);
    }

    private async Task<GameManifest> ManifestAsync(Server server, CancellationToken cancellationToken) =>
        ServerSpecs.Parse(await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken));

    /// <summary>
    /// Updates through SteamCMD, Minecraft's steps, a GitHub release or a .NET runtime can be checked; others (a fixed
    /// download) cannot.
    /// </summary>
    public static bool IsCheckable(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return (manifest.Update?.Steps ?? []).Any(s => s is SteamCmdStep or MinecraftServerStep or MinecraftBedrockStep or GitHubReleaseStep or DotnetRuntimeStep);
    }

    private AgentConnectionInfo? Connection(NodeId node) =>
        connections.Find(node) is { } live && sessions.IsActive(live.SessionId) ? live : null;

    /// <summary>A node's text: printable, trimmed, cut; null when nothing is left.</summary>
    private static string? Text(string? text, int length)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !DisplayNames.IsDisplayable(trimmed))
        {
            return null;
        }

        return trimmed.Length <= length ? trimmed : trimmed[..length];
    }

    /// <summary>An error code is a short dotted identifier.</summary>
    private static string? Code(string? code) =>
        code is { Length: > 0 and <= GameUpdateWatch.MaxErrorCodeLength } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? code
            : code is null ? null : "update_check.failed";

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: game update check found {Installed} installed, {Latest} offered, update {Available} {Code}")]
    private partial void LogChecked(ServerId serverId, string? installed, string? latest, bool? available, string? code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: its node reported an update check nobody asked for (or answered twice); ignored")]
    private partial void LogUnasked(ServerId serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: automatic game update from {Installed} to {Latest} started")]
    private partial void LogAutoUpdateStarted(ServerId serverId, string? installed, string? latest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: the automatic game update could not start ({Code})")]
    private partial void LogAutoUpdateNotStarted(ServerId serverId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: automatic move to version {Version} of its template started")]
    private partial void LogTemplateUpdateStarted(ServerId serverId, string version);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Server {ServerId}: version {Version} of its template cannot be moved to ({Code}); it is not tried by itself again")]
    private partial void LogTemplateUpdateRefused(ServerId serverId, string version, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId}: skipped by this pass of the game updates ({Code})")]
    private partial void LogServerSkipped(ServerId serverId, string code);
}
