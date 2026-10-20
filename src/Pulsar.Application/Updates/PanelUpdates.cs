using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Application.Realtime;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Updates;

namespace Pulsar.Application.Updates;

/// <summary>Where the panel reads the newest release's manifest and signature: GitHub (<see cref="UpdateRelease"/>), or a test's.</summary>
public interface IUpdateSource
{
    Task<(byte[] Manifest, byte[] Signature)> GetLatestAsync(CancellationToken cancellationToken);
}

/// <param name="PanelVersion">The version of the panel answering.</param>
/// <param name="HasKeys">Whether this build trusts any update key: without one, it takes no update.</param>
/// <param name="Available">The newest release, when it is newer than the panel or than some node.</param>
public sealed record UpdatesView(string PanelVersion, bool HasKeys, bool CheckAutomatically, DateTimeOffset? CheckedAt, string? CheckError,
    AvailableUpdate? Available, IReadOnlyList<NodeUpdateView> Nodes);

/// <param name="Notes">What changed, as the release's signed manifest says (plain text).</param>
/// <param name="NewerThanPanel">Whether the panel itself is older: it is updated through the node it runs on, first.</param>
public sealed record AvailableUpdate(string Version, DateTimeOffset Published, string? Notes, bool NewerThanPanel);

/// <param name="HostsPanel">The panel runs on this node: updating it updates the panel too.</param>
/// <param name="Blocked">Why this node cannot take the available release now (an error code), or null.</param>
public sealed record NodeUpdateView(Guid NodeId, string Name, bool Connected, string? AgentVersion, bool HostsPanel, bool CanUpdate,
    bool UpdateAvailable, string? Blocked, NodeUpdateStatusView? Last);

public sealed record NodeUpdateStatusView(string Status, string? From, string? To, string? Error, DateTimeOffset RequestedAt, DateTimeOffset UpdatedAt,
    bool UnderWay);

/// <summary>What happened to each node asked to update.</summary>
public sealed record UpdateRequestResult(Guid NodeId, bool Sent, string? ErrorCode, string? Message);

/// <summary>
/// Pulsar's updates from the panel (Phase 19, docs/guides/update.md, "One-click updates"): it looks for the newest
/// release (once a day unless turned off, or when asked), keeps it only signed by a trusted key, and asks the nodes an
/// administrator chooses to install it. The node's agent downloads and checks the release itself; the panel only tells it
/// which one (the version and its manifest's SHA-256). The panel's own machine is updated through its agent (it announces
/// <see cref="AgentCapabilities.PanelHost"/>), before the other nodes: an agent newer than its panel is refused.
/// </summary>
public sealed partial class PanelUpdates(
    IPulsarDbContext db,
    IUpdateSource source,
    UpdateVerifier verifier,
    IAgentConnections connections,
    AgentSessionStore sessions,
    IAuditLog audit,
    IRequestContext request,
    IRealtimeNotifier notifier,
    TimeProvider clock,
    ILogger<PanelUpdates> logger)
{
    /// <summary>The panel's own version: its release part, and whether it is a pre-release (a development build).</summary>
    public static (UpdateVersion Version, bool PreRelease) PanelVersion { get; } = ReadPanelVersion();

    public static string PanelVersionText { get; } = typeof(PanelUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+', 2)[0] ?? "0.0.0";

    public async Task<UpdatesView> GetAsync(CancellationToken cancellationToken)
    {
        var state = await db.UpdateState.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? UpdateState.Default(clock.GetUtcNow());
        var latest = Latest(state);
        var now = clock.GetUtcNow();
        var nodes = await db.Nodes.AsNoTracking().OrderBy(n => n.Name).ToListAsync(cancellationToken);
        var updates = await db.NodeUpdates.AsNoTracking().ToDictionaryAsync(u => u.NodeId, cancellationToken);
        var views = nodes.Select(node =>
        {
            var live = Live(node.Id);
            var version = live?.AgentVersion ?? node.Inventory?.AgentVersion;
            var blocked = latest is null ? null : Blocked(latest, live, version, updates.GetValueOrDefault(node.Id), now);
            var newer = latest is not null && UpdateVersion.Installed(version) is { } installed && latest.Version.IsNewerThan(installed);
            var last = updates.GetValueOrDefault(node.Id) is { } u
                ? new NodeUpdateStatusView(u.Status, u.FromVersion, u.ToVersion, u.Error, u.RequestedAt, u.UpdatedAt, u.IsUnderWay(now))
                : null;
            return new NodeUpdateView(node.Id.Value, node.Name, live is not null, version, HostsPanel(live),
                live?.Capabilities.Contains(AgentCapabilities.UpdatePulsar) == true, newer, blocked, last);
        }).ToList();

        AvailableUpdate? available = null;
        if (latest is not null && (latest.Version.IsNewerThan(PanelVersion) || views.Any(v => v.UpdateAvailable)))
        {
            available = new AvailableUpdate(latest.Manifest.Version, latest.Manifest.Published, latest.Manifest.Notes, latest.Version.IsNewerThan(PanelVersion));
        }

        return new UpdatesView(PanelVersionText, verifier.HasKeys, state.CheckAutomatically, state.CheckedAt, state.CheckError, available, views);
    }

    public async Task<bool> SetCheckAutomaticallyAsync(bool checkAutomatically, CancellationToken cancellationToken)
    {
        var state = await StateAsync(cancellationToken);
        if (!state.ChangeCheckAutomatically(checkAutomatically, clock.GetUtcNow()))
        {
            return false;
        }

        audit.Record(AuditActions.Updates.SettingsChanged, metadata: new { checkAutomatically });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Looks at GitHub now: the newest release, kept when its signature verifies (an older one than kept is ignored: a
    /// release is never taken back). A failure is recorded and what was found before stays offered.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        string? error = null;
        UpdateCheck? check = null;
        byte[]? manifest = null;
        if (!verifier.HasKeys)
        {
            error = "This build of Pulsar trusts no update key, so it takes no update: install a release.";
        }
        else
        {
            try
            {
                (manifest, var signature) = await source.GetLatestAsync(cancellationToken);
                check = verifier.Verify(manifest, signature);
                error = check.Manifest is null ? $"The newest release was refused ({check.ErrorCode}): {check.Error}" : null;
            }
            catch (Exception ex) when (ex is UpdateDownloadException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                error = ex is TaskCanceledException ? "GitHub did not answer in time." : ex.Message;
            }
        }

        var state = await StateAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (check?.Manifest is { } found && manifest is not null && UpdateVersion.TryParse(found.Version, out var version))
        {
            var kept = Latest(state);
            if (kept is null || version > kept.Version)
            {
                state.Found(found.Version, Encoding.UTF8.GetString(manifest), Convert.ToHexStringLower(SHA256.HashData(manifest)), now);
                LogFound(found.Version);
            }
            else
            {
                state.Found(kept.Manifest.Version, state.LatestManifest!, state.LatestManifestSha256!, now);
            }
        }
        else
        {
            state.Failed(error ?? "The check failed.", now);
            LogCheckFailed(error);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Asks each node to install the newest release (the caller checked the administrator's password and code): a node
    /// that cannot take it now is left with why, the others are told.
    /// </summary>
    public async Task<Result<IReadOnlyList<UpdateRequestResult>>> RequestAsync(IReadOnlyList<NodeId> nodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var state = await StateAsync(cancellationToken);
        if (Latest(state) is not { } latest)
        {
            return UpdateErrors.NothingAvailable;
        }

        var results = new List<UpdateRequestResult>();
        foreach (var nodeId in nodes.Distinct())
        {
            var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nodeId, cancellationToken);
            if (node is null)
            {
                results.Add(new UpdateRequestResult(nodeId.Value, false, NodeErrors.NotFound.Code, NodeErrors.NotFound.Message));
                continue;
            }

            var now = clock.GetUtcNow();
            var live = Live(nodeId);
            var version = live?.AgentVersion ?? node.Inventory?.AgentVersion;
            var update = await db.NodeUpdates.SingleOrDefaultAsync(u => u.NodeId == nodeId, cancellationToken);
            if (Blocked(latest, live, version, update, now) is { } blocked)
            {
                results.Add(new UpdateRequestResult(nodeId.Value, false, blocked, null));
                continue;
            }

            var from = version!;
            if (update is null)
            {
                db.NodeUpdates.Add(NodeUpdate.Request(nodeId, from, latest.Manifest.Version, request.UserId, now));
            }
            else
            {
                update.RequestAgain(from, latest.Manifest.Version, request.UserId, now);
            }

            audit.Record(AuditActions.Updates.Requested, metadata: new { from, to = latest.Manifest.Version, panel = HostsPanel(live) })
                .WithTarget("node", nodeId.Value.ToString());
            await db.SaveChangesAsync(cancellationToken);

            var ack = await connections.SendAsync(nodeId, new UpdatePulsarCommand(latest.Manifest.Version, state.LatestManifestSha256!), request.UserId,
                CancellationToken.None);
            if (ack?.Status is AckStatus.Accepted)
            {
                results.Add(new UpdateRequestResult(nodeId.Value, true, null, null));
                continue;
            }

            // Refused or not answered: recorded as the end of this request, with the node's reason.
            var code = ack is null ? NodeErrors.NotConnected.Code : AgentInput.ErrorCode(ack.ErrorCode) ?? "update.refused";
            var message = ack?.Message is { } said ? AgentInput.Text(said, NodeUpdate.MaxErrorLength) : null;
            var record = await db.NodeUpdates.SingleAsync(u => u.NodeId == nodeId, CancellationToken.None);
            record.Reported(UpdateStatuses.Failed, null, null, message ?? code, clock.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            results.Add(new UpdateRequestResult(nodeId.Value, false, code, message));
        }

        notifier.NodesChanged(nodes);
        return results;
    }

    /// <summary>
    /// What a node's agent reports of its update (<see cref="IAgentHub.UpdateReport"/>): its strings checked as all an
    /// agent's are, kept on the node's row; the end is audited once.
    /// </summary>
    public async Task ReportAsync(NodeId nodeId, UpdateReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Status is not { } status || !UpdateStatuses.All.Contains(status))
        {
            return;
        }

        var from = Version(report.From);
        var to = Version(report.To);
        var error = report.Error is null ? null : AgentInput.Text(report.Error, NodeUpdate.MaxErrorLength);
        var now = clock.GetUtcNow();
        var update = await db.NodeUpdates.SingleOrDefaultAsync(u => u.NodeId == nodeId, cancellationToken);
        bool changed;
        if (update is null)
        {
            if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
            {
                return;
            }

            db.NodeUpdates.Add(NodeUpdate.FromReport(nodeId, status, from, to, error, now));
            changed = true;
        }
        else
        {
            changed = update.Reported(status, from, to, error, now);
        }

        if (!changed)
        {
            return;
        }

        if (UpdateStatuses.IsFinal(status))
        {
            audit.Record(AuditActions.Updates.Finished, status == UpdateStatuses.Succeeded ? AuditOutcome.Success : AuditOutcome.Failure,
                    new { status, from, to, error })
                .WithTarget("node", nodeId.Value.ToString());
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two reports at once (a step and the result after a Hello): the next one is kept.
            return;
        }

        notifier.NodesChanged([nodeId]);
    }

    /// <summary>
    /// Why <paramref name="live"/>'s node cannot take <paramref name="latest"/> now, or null: offline, not able to update
    /// from here, already there, too old for it, under way, or newer than the panel (the panel first).
    /// </summary>
    private static string? Blocked(LatestRelease latest, AgentConnectionInfo? live, string? version, NodeUpdate? update, DateTimeOffset now)
    {
        if (live is null)
        {
            return "update.node_offline";
        }

        if (!live.Capabilities.Contains(AgentCapabilities.UpdatePulsar))
        {
            return "update.node_cannot";
        }

        if (UpdateVersion.Installed(version) is not { } installed || !latest.Version.IsNewerThan(installed))
        {
            return "update.up_to_date";
        }

        if (UpdateVersion.TryParse(latest.Manifest.MinimumUpdateFrom, out var minimum) && installed.Version < minimum)
        {
            return "update.too_old";
        }

        if (update?.IsUnderWay(now) == true)
        {
            return "update.in_progress";
        }

        return !HostsPanel(live) && latest.Version.IsNewerThan(PanelVersion) ? "update.panel_first" : null;
    }

    private static bool HostsPanel(AgentConnectionInfo? live) => live?.Capabilities.Contains(AgentCapabilities.PanelHost) == true;

    private AgentConnectionInfo? Live(NodeId node) => connections.Find(node) is { } live && sessions.IsActive(live.SessionId) ? live : null;

    /// <summary>The kept release, verified again (a row someone changed in the database is not taken), or null.</summary>
    private static LatestRelease? Latest(UpdateState state)
    {
        if (state.LatestManifest is not { } text || state.LatestManifestSha256 is not { } sha)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha, StringComparison.Ordinal)
            || UpdateVerifier.Read(bytes) is not { } manifest || !UpdateVersion.TryParse(manifest.Version, out var version))
        {
            return null;
        }

        return new LatestRelease(manifest, version);
    }

    private async Task<UpdateState> StateAsync(CancellationToken cancellationToken)
    {
        if (await db.UpdateState.SingleOrDefaultAsync(cancellationToken) is { } state)
        {
            return state;
        }

        state = UpdateState.Default(clock.GetUtcNow());
        db.UpdateState.Add(state);
        return state;
    }

    private static string? Version(string? text) => UpdateVersion.Installed(text) is { } parsed
        ? text!.Length <= NodeUpdate.MaxVersionLength ? text : parsed.Version.ToString()
        : null;

    private static (UpdateVersion, bool) ReadPanelVersion() =>
        UpdateVersion.Installed(typeof(PanelUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? (default, true);

    private sealed record LatestRelease(UpdateManifest Manifest, UpdateVersion Version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pulsar {Version} is the newest release")]
    private partial void LogFound(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The check for Pulsar updates failed: {Error}")]
    private partial void LogCheckFailed(string? error);
}

public static class UpdateErrors
{
    public static readonly DomainError NothingAvailable = new("update.nothing_available", "No newer release is known: check for updates first.");
}
