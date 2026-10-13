using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Application.Nodes;

public static class AgentTransportErrors
{
    public static DomainError NoAgentsEndpoint { get; } =
        new("agent_transport.no_agents_endpoint", "The panel has no agents' endpoint (the Kestrel endpoint \"Agents\"): agents would have nowhere to go.");
}

/// <summary>An enrolled node whose agent did not connect with a client certificate the last time (or never said).</summary>
/// <param name="AgentCanUseCertificate">Its agent can move to the agents' address on its own; false: it must be updated first.</param>
public sealed record NodeWithoutCertificate(Guid Id, string Name, bool Connected, bool? AgentCanUseCertificate);

/// <param name="EndpointConfigured">The panel listens on an agents' endpoint.</param>
/// <param name="AgentsUrl">Its address as agents reach it, seen from this request; null without one.</param>
/// <param name="InEffect">Certificates are required and the endpoint exists (without it the switch does nothing).</param>
/// <param name="NodesWithoutCertificateCount">All of them; <paramref name="NodesWithoutCertificate"/> lists the first by name.</param>
public sealed record AgentTransportOverview(
    bool EndpointConfigured,
    string? AgentsUrl,
    bool RequireClientCertificates,
    bool InEffect,
    int NodesWithoutCertificateCount,
    IReadOnlyList<NodeWithoutCertificate> NodesWithoutCertificate);

public sealed record UpdateAgentTransportRequest(bool RequireClientCertificates);

/// <summary>
/// The "require client certificates" switch (docs/architecture/03, "Client certificates"). Turning it on ends every
/// agent session signed in without a certificate: those agents sign in again and are sent to the agents' address.
/// </summary>
public sealed class AgentTransportManagement(
    IPulsarDbContext db,
    AgentTransportPolicy policy,
    AgentSessionStore sessions,
    IAgentConnections connections,
    IAuditLog audit,
    TimeProvider clock)
{
    public const int MaxListedNodes = 100;

    public async Task<AgentTransportOverview> GetAsync(string? agentsUrl, CancellationToken cancellationToken)
    {
        await policy.EnsureLoadedAsync(db, cancellationToken);
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Status == NodeStatus.Active && n.ConnectedWithCertificate != true)
            .OrderBy(n => n.Name)
            .Select(n => new { n.Id, n.Name, n.AgentCanUseCertificate })
            .ToListAsync(cancellationToken);
        var listed = nodes.Take(MaxListedNodes).Select(n =>
        {
            // A connected agent says what it can do now; the stored answer is from its last Hello.
            var connection = connections.Find(n.Id);
            var canMove = connection is null
                ? n.AgentCanUseCertificate
                : connection.Capabilities.Contains(AgentCapabilities.UseAgentsAddress, StringComparer.Ordinal);
            return new NodeWithoutCertificate(n.Id.Value, n.Name, connection is not null, canMove);
        }).ToList();

        var require = policy.RequireClientCertificates;
        return new AgentTransportOverview(agentsUrl is not null, agentsUrl, require, require && agentsUrl is not null, nodes.Count, listed);
    }

    public async Task<Result<AgentTransportOverview>> SetAsync(UpdateAgentTransportRequest request, string? agentsUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequireClientCertificates && agentsUrl is null)
        {
            return AgentTransportErrors.NoAgentsEndpoint;
        }

        await policy.Changing.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await SaveAsync(request, cancellationToken);
            }
            catch
            {
                // The change may have committed before the call failed (cancelled as the answer came back): the switch in
                // memory follows what is stored, read again; when even that fails, from the database at the next sign-in.
                await ApplyStoredAsync();
                throw;
            }

            Apply(request.RequireClientCertificates);
        }
        finally
        {
            policy.Changing.Release();
        }

        return await GetAsync(agentsUrl, cancellationToken);
    }

    private void Apply(bool requireClientCertificates)
    {
        policy.Set(requireClientCertificates);
        if (requireClientCertificates)
        {
            foreach (var node in sessions.EndWithoutCertificate())
            {
                // Only a connection whose session just ended: a node that already moved keeps its connection with a
                // certificate, though the session it left on the main endpoint was still there.
                if (connections.Find(node) is { } connection && !sessions.IsActive(connection.SessionId))
                {
                    connections.Disconnect(node);
                }
            }
        }
    }

    private async Task ApplyStoredAsync()
    {
        try
        {
            db.ChangeTracker.Clear();
            var stored = await db.AgentTransportSettings.AsNoTracking().SingleOrDefaultAsync(CancellationToken.None);
            Apply(stored?.RequireClientCertificates == true);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            policy.Forget();
        }
    }

    private async Task SaveAsync(UpdateAgentTransportRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var now = clock.GetUtcNow();
            var settings = await db.AgentTransportSettings.SingleOrDefaultAsync(cancellationToken);
            if (settings is null)
            {
                settings = AgentTransportSettings.Default(now);
                db.AgentTransportSettings.Add(settings);
            }

            if (!settings.ChangeRequirement(request.RequireClientCertificates, now))
            {
                db.ChangeTracker.Clear();
                break;
            }

            audit.Record(AuditActions.AgentTransport.Changed, metadata: new { requireClientCertificates = request.RequireClientCertificates })
                .WithTarget("agent_transport", "settings");
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Created meanwhile by a first save racing this one: apply the change to that row.
                db.ChangeTracker.Clear();
            }
        }
    }
}
