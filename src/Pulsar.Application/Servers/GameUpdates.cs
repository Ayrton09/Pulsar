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
/// <param name="Running">The automatic update running now.</param>
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
    DateTimeOffset? NotBefore = null);

/// <param name="Step">The step being done, from 0.</param>
public sealed record GameUpdateRunView(Guid Id, SequenceStatus Status, int Step, int Steps, SequenceStepKind? StepKind, DateTimeOffset? WakeAt,
    DateTimeOffset CreatedAt, string? ErrorCode, string? Message);

/// <summary>
/// A server's game updates (Pulsar 1.0.9), as AMP, TCAdmin and WindowsGSM follow them: every
/// <see cref="GameUpdateWatch.CheckInterval"/> the server's node asks the publisher (Steam through SteamCMD, Mojang)
/// whether there is something newer than what the server has. A new one is shown and notified once; with
/// <see cref="GameUpdatePolicy.Install"/> it is installed as a sequence (<see cref="ServerSequences"/>): the players are
/// warned, the server stops (when nobody plays, if asked), updates and starts again if it was running. Installing by
/// itself needs the rights it uses (update, stop, start, and console commands to warn): it cannot do what its author
/// could not.
/// </summary>
public sealed partial class GameUpdates(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    IServerPlayers players,
    ServerManagement servers,
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

    public async Task<Result<GameUpdateView>> ChangeAsync(ServerId id, GameUpdatePolicy policy, bool whenEmpty, int warningMinutes, string? warningMessage,
        CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var manifest = await ManifestAsync(server, cancellationToken);
        if (policy != GameUpdatePolicy.Off && !IsCheckable(manifest))
        {
            return ServerErrors.GameUpdatesUnsupported;
        }

        if (policy == GameUpdatePolicy.Install && whenEmpty && manifest.Query is null)
        {
            return ServerErrors.WhenEmptyUnsupported;
        }

        // It acts as the game updates later: whoever sets it must be allowed what it does.
        if (policy == GameUpdatePolicy.Install && !await MayInstallAsync(server, warningMinutes > 0 && manifest.Commands?.Broadcast is not null,
                cancellationToken))
        {
            return ServerErrors.GameUpdatesForbidden;
        }

        return await RetriedAsync<GameUpdateView>(async () =>
        {
            var watch = await TrackedAsync(id, cancellationToken);
            var before = (watch.Policy, watch.WhenEmpty, watch.WarningMinutes, watch.WarningMessage);
            var changed = watch.Change(policy, whenEmpty, warningMinutes, warningMessage, clock.GetUtcNow());
            if (!changed.IsSuccess)
            {
                return changed.Error!;
            }

            if (before != (watch.Policy, watch.WhenEmpty, watch.WarningMinutes, watch.WarningMessage))
            {
                audit.Record(AuditActions.Server.GameUpdatesChanged,
                        metadata: new { policy = watch.Policy.ToString(), watch.WhenEmpty, watch.WarningMinutes, watch.WarningMessage })
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
            if (sequence is null || !sequence.Cancel(now).IsSuccess)
            {
                if (!watch.AutoUpdateDue)
                {
                    return ScheduleErrors.SequenceOver;
                }

                // Not started yet (waiting for the players): this build is not installed by itself.
                watch.Skip(now);
                operation = null;
            }

            audit.Record(AuditActions.Server.GameUpdateCancelled, metadata: new { latest = watch.Latest }).WithServer(server.Id, server.NodeId);
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
        }

        db.ChangeTracker.Clear();
        await StartDueUpdatesAsync(cancellationToken);
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
        if (sequence.Origin != SequenceOrigin.GameUpdate)
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
            if (sequence.Status == SequenceStatus.Failed)
            {
                await notifier.GameUpdateFailedAsync(server, watch.Latest, sequence.Message ?? sequence.ErrorCode, cancellationToken);
            }
            else if (sequence.Status == SequenceStatus.Succeeded)
            {
                await notifier.GameUpdateInstalledAsync(server, watch.Latest, cancellationToken);
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

        steps.Add(new SequenceStep(SequenceStepKind.Stop));
        steps.Add(new SequenceStep(SequenceStepKind.Update));
        // Back online even when the update failed: SteamCMD leaves the previous build in place.
        steps.Add(new SequenceStep(SequenceStepKind.Start, OnlyIfWasRunning: true, Always: true));
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

            var started = await StartSequenceAsync(server, await ManifestAsync(server, cancellationToken), watch, cancellationToken);
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
            .Where(s => s.ServerId == server.Id && s.Origin == SequenceOrigin.GameUpdate)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return new GameUpdateView(watch.Policy, watch.WhenEmpty, watch.WarningMinutes, watch.WarningMessage ?? GameUpdateWatch.DefaultWarningMessage,
            IsCheckable(manifest), manifest.Query is not null, manifest.Commands?.Broadcast is not null, watch.Installed, watch.Latest, watch.Available,
            watch.CheckedAt, watch.CheckErrorCode, watch.CheckError,
            watch.CheckRequestedAt is { } asked && now - asked < GameUpdateWatch.AnswerTimeout,
            watch.Policy == GameUpdatePolicy.Off ? null : watch.NextCheckAt, watch.AutoUpdateDue, players.Online(server.Id),
            sequence is null
                ? null
                : new GameUpdateRunView(sequence.Id.Value, sequence.Status, sequence.StepIndex, sequence.Steps.Count, sequence.Current?.Kind,
                    sequence.Status == SequenceStatus.Running ? sequence.WakeAt : null, sequence.CreatedAt, sequence.ErrorCode, sequence.Message),
            watch.AutoUpdateDue && watch.AutoUpdateNotBefore is { } notBefore && notBefore > now ? notBefore : null);
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

    /// <summary>Updates through SteamCMD or Minecraft's steps can be checked; others (a fixed download) cannot.</summary>
    public static bool IsCheckable(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return (manifest.Update?.Steps ?? []).Any(s => s is SteamCmdStep or MinecraftServerStep or MinecraftBedrockStep);
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
}
