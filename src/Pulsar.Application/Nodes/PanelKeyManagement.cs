using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Application.Nodes;

public static class PanelKeyErrors
{
    public static DomainError RotationInProgress { get; } =
        new("panel_key.rotation_in_progress", "A previous key is still being retired. Retire it before rotating again.");

    public static DomainError NotRetiring { get; } =
        new("panel_key.not_retiring", "Only a key that is being retired can be retired.");

    public static DomainError NotFound { get; } = new("panel_key.not_found", "The panel key does not exist.");
}

/// <param name="NodesTrusting">Enrolled nodes whose agent pins this key, as last reported.</param>
public sealed record PanelKeyView(string KeyId, PanelSigningKeyStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt, int NodesTrusting);

/// <param name="Nodes">Enrolled nodes.</param>
/// <param name="NodesOutdated">Nodes that pin only retiring keys: retiring those keys cuts them off until re-enrolled.</param>
/// <param name="NodesUnknown">Nodes that have not reported their keys yet (never connected since this panel version).</param>
public sealed record PanelKeysOverview(IReadOnlyList<PanelKeyView> Keys, int Nodes, int NodesOutdated, int NodesUnknown);

/// <summary>
/// Rotation of the panel signing key (docs/architecture/03): a new key starts signing, the previous one keeps being
/// accepted while agents receive the new key list (signed by a key they already trust), and the administrator retires
/// it once every node has it.
/// </summary>
public sealed class PanelKeyManagement(
    IPulsarDbContext db,
    IPanelKeyRing keys,
    IAgentConnections connections,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<PanelKeysOverview> GetAsync(CancellationToken cancellationToken)
    {
        await keys.GetPublicKeysAsync(cancellationToken); // creates the first key on a new panel
        var stored = await db.PanelSigningKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(cancellationToken);
        var nodes = await db.Nodes.AsNoTracking().Where(n => n.Status != NodeStatus.PendingEnrollment).ToListAsync(cancellationToken);
        var pins = nodes.Select(n => connections.Find(n.Id)?.PinnedPanelKeys ?? n.PinnedPanelKeys).ToList();
        var active = stored.Single(k => k.Status == PanelSigningKeyStatus.Active).KeyId;

        return new PanelKeysOverview(
            [.. stored.Select(k => new PanelKeyView(k.KeyId, k.Status, k.CreatedAt, k.RetiredAt, pins.Count(p => p?.Contains(k.KeyId, StringComparer.Ordinal) == true)))],
            nodes.Count,
            pins.Count(p => p is not null && !p.Contains(active, StringComparer.Ordinal)),
            pins.Count(p => p is null));
    }

    public async Task<Result<PanelKeysOverview>> RotateAsync(CancellationToken cancellationToken)
    {
        await keys.GetPublicKeysAsync(cancellationToken);
        if (await db.PanelSigningKeys.AnyAsync(k => k.Status == PanelSigningKeyStatus.Retiring, cancellationToken))
        {
            return PanelKeyErrors.RotationInProgress;
        }

        var previous = await db.PanelSigningKeys.SingleAsync(k => k.Status == PanelSigningKeyStatus.Active, cancellationToken);
        var next = keys.CreateKey();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Two saves: "a single active key" is a unique index, checked per statement.
            previous.StartRetiring();
            await db.SaveChangesAsync(cancellationToken);
            db.PanelSigningKeys.Add(next);
            audit.Record(AuditActions.PanelKey.Rotated, metadata: new { key = next.KeyId, previous = previous.KeyId })
                .WithTarget("panel_key", next.KeyId);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        keys.Reload();
        await connections.UpdatePanelKeysAsync(null, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task<Result<PanelKeysOverview>> RetireAsync(string keyId, CancellationToken cancellationToken)
    {
        var key = await db.PanelSigningKeys.SingleOrDefaultAsync(k => k.KeyId == keyId, cancellationToken);
        if (key is null)
        {
            return PanelKeyErrors.NotFound;
        }

        if (key.Status != PanelSigningKeyStatus.Retiring)
        {
            return PanelKeyErrors.NotRetiring;
        }

        key.Retire(clock.GetUtcNow());
        audit.Record(AuditActions.PanelKey.Retired, metadata: new { key = key.KeyId }).WithTarget("panel_key", key.KeyId);
        await db.SaveChangesAsync(cancellationToken);
        keys.Reload();

        // Connected agents drop it now; the others get the list (signed with the active key) when they connect.
        await connections.UpdatePanelKeysAsync(null, cancellationToken);
        return await GetAsync(cancellationToken);
    }
}
