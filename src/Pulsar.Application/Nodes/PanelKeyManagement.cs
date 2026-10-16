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

    public static DomainError NodesOutdated(IEnumerable<string> nodes) =>
        new("panel_key.nodes_outdated", $"Retiring the key would cut off nodes that trust no other key: {string.Join(", ", nodes)}. " +
            "Wait for them to sign in with the new key, or retire it anyway and enroll them again.");
}

/// <param name="NodesTrusting">Enrolled nodes whose agent pins this key, as its last sign-in signed.</param>
public sealed record PanelKeyView(string KeyId, PanelSigningKeyStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt, int NodesTrusting);

/// <param name="Nodes">Enrolled nodes.</param>
/// <param name="NodesOutdated">Nodes that pin only retiring keys: retiring those keys cuts them off until re-enrolled.</param>
/// <param name="NodesUnknown">Nodes that have not reported their keys yet (never connected since this panel version).</param>
/// <param name="NodesToReEnroll">
/// The names of the enrolled nodes, not disabled, that would trust none of the panel's keys once the key being retired
/// goes (none when no key is): retiring it cuts them off until they are enrolled again.
/// </param>
public sealed record PanelKeysOverview(IReadOnlyList<PanelKeyView> Keys, int Nodes, int NodesOutdated, int NodesUnknown,
    IReadOnlyList<string> NodesToReEnroll);

/// <summary>
/// Rotation of the panel signing key (docs/architecture/03): a new key starts signing, the previous one keeps being
/// accepted while agents receive the new key list (signed by a key they already trust), and the administrator retires
/// it once every node has it. What a node has is what its agent's last sign-in signed with the node's key (recorded at
/// enrollment and at each sign-in), never what a connection says: whoever holds a session token can say anything.
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
        var pins = nodes.Select(n => n.PinnedPanelKeys).ToList();
        var active = stored.Single(k => k.Status == PanelSigningKeyStatus.Active).KeyId;
        var retiring = stored.FirstOrDefault(k => k.Status == PanelSigningKeyStatus.Retiring);

        return new PanelKeysOverview(
            [.. stored.Select(k => new PanelKeyView(k.KeyId, k.Status, k.CreatedAt, k.RetiredAt, pins.Count(p => p?.Contains(k.KeyId, StringComparer.Ordinal) == true)))],
            nodes.Count,
            pins.Count(p => p is not null && !p.Contains(active, StringComparer.Ordinal)),
            pins.Count(p => p is null),
            retiring is null ? [] : CutOff(nodes, stored, retiring.KeyId));
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

    /// <param name="force">Retire it even when nodes would be cut off (<see cref="PanelKeysOverview.NodesToReEnroll"/>).</param>
    public async Task<Result<PanelKeysOverview>> RetireAsync(string keyId, bool force, CancellationToken cancellationToken)
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

        var cutOff = CutOff(await db.Nodes.AsNoTracking().ToListAsync(cancellationToken),
            await db.PanelSigningKeys.AsNoTracking().ToListAsync(cancellationToken), key.KeyId);
        if (cutOff.Count > 0 && !force)
        {
            return PanelKeyErrors.NodesOutdated(cutOff);
        }

        key.Retire(clock.GetUtcNow());
        audit.Record(AuditActions.PanelKey.Retired, metadata: new { key = key.KeyId, cutOff }).WithTarget("panel_key", key.KeyId);
        await db.SaveChangesAsync(cancellationToken);
        keys.Reload();

        // Connected agents drop it now; the others get the list (signed with the active key) when they connect.
        await connections.UpdatePanelKeysAsync(null, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    /// <summary>
    /// The enrolled nodes, not disabled, whose recorded keys (unknown ones too) include none of the panel's but
    /// <paramref name="retiring"/>: without it, they trust no key of the panel's.
    /// </summary>
    private static List<string> CutOff(IEnumerable<Node> nodes, IEnumerable<PanelSigningKey> keys, string retiring)
    {
        var others = keys.Where(k => k.Status != PanelSigningKeyStatus.Retired && k.KeyId != retiring).Select(k => k.KeyId).ToHashSet(StringComparer.Ordinal);
        return [.. nodes.Where(n => n.Status is NodeStatus.Active or NodeStatus.PendingApproval && !(n.PinnedPanelKeys ?? []).Any(others.Contains))
            .Select(n => n.Name).Order(StringComparer.Ordinal)];
    }
}
