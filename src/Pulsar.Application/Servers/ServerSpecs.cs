using System.Text;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

/// <summary>The allocation for each port asked for by number (null: left out), and those added to the node for them.</summary>
public sealed record PortNumberAllocations(IReadOnlyDictionary<string, Guid?> Ports, IReadOnlyList<NodeAllocation> Added);

/// <summary>
/// What a server is made of beyond its row: its variable values and its port allocations, checked against its
/// manifest, and the runtime spec the agent receives (docs/architecture/03). Secret values are decrypted only here,
/// right before they are signed into a command.
/// </summary>
public sealed class ServerSpecs(IPulsarDbContext db, IVariableProtector protector)
{
    private const string Platform = ManifestValidator.WindowsX64;

    /// <summary>
    /// The stored template's manifest. One this panel's checks refuse is not used anyway (a stricter rule may be a
    /// security one): <see cref="DomainErrorException"/> with <see cref="ServerErrors.TemplateNoLongerValid"/>.
    /// </summary>
    public static GameManifest Parse(GameTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(template.Content));
        return result.IsValid
            ? result.Manifest!
            : throw new DomainErrorException(ServerErrors.TemplateNoLongerValid(template.Key, template.Version),
                $"Template {template.Key} {template.Version} is not valid anymore: {string.Join("; ", result.Errors)}");
    }

    /// <summary>The spec the agent renders the server from: manifest text, values (secrets decrypted) and ports.</summary>
    public async Task<ServerRuntimeSpec> BuildAsync(Server server, GameTemplate template, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(template);
        var allocations = await AllocationsOf(server.Id, cancellationToken);
        var addons = await ServerAddons.InstalledKeysAsync(db, server.Id, cancellationToken);
        return new ServerRuntimeSpec(server.ShortId, template.Content, Values(server), server.ExtraArguments, server.AutoStart, server.RestartOnCrash,
            allocations.ToDictionary(a => a.PortName!, a => new ServerPort(a.IpAddress, a.Port)), Limits(server), addons.Count > 0 ? addons : null,
            server.SteamBranch);
    }

    /// <summary>The server's caps for its agent; null without any (older agents never see the field then).</summary>
    public static ServerLimits? Limits(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.CpuLimitPercent is null && server.MemoryLimitMb is null
            ? null
            : new ServerLimits(server.CpuLimitPercent, server.MemoryLimitMb * 1024L * 1024);
    }

    /// <summary>The server's stored values, secret ones decrypted.</summary>
    public Dictionary<string, string?> Values(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Variables.ToDictionary(v => v.Key, v => v.ProtectedValue is { } secret ? protector.Unprotect(secret) : v.Value);
    }

    public Task<List<NodeAllocation>> AllocationsOf(ServerId serverId, CancellationToken cancellationToken) =>
        db.NodeAllocations.Where(a => a.ServerId == serverId).ToListAsync(cancellationToken);

    /// <summary>
    /// Applies <paramref name="changes"/> to the server's variables (null or empty: back to the manifest default). Every
    /// value is checked against its definition; variables users cannot edit need <paramref name="canEditProtected"/>.
    /// </summary>
    public Result SetVariables(Server server, GameManifest manifest, IReadOnlyDictionary<string, string?> changes, bool canEditProtected,
        UserId? by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(changes);
        var definitions = manifest.VariableList.ToDictionary(v => v.Key, StringComparer.Ordinal);
        foreach (var (key, value) in changes)
        {
            if (!definitions.TryGetValue(key, out var definition))
            {
                return ServerErrors.InvalidSettings($"Unknown variable '{key}'.");
            }

            if (!definition.UserEditable && !canEditProtected)
            {
                return ServerErrors.VariableProtected(key);
            }

            var clean = string.IsNullOrEmpty(value) ? null : value;
            if (clean is not null && VariableValues.Check(definition, clean) is { } error)
            {
                return ServerErrors.InvalidSettings($"{definition.Label}: {error}");
            }

            if (definition.Secret)
            {
                server.SetSecretVariable(key, clean is null ? null : protector.Protect(clean), by, now);
            }
            else
            {
                server.SetVariable(key, clean, by, now);
            }
        }

        // A value stored in clear for a variable that is secret now (a template upgrade made it so) is encrypted.
        foreach (var variable in server.Variables.Where(v => v.Value is not null && definitions.TryGetValue(v.Key, out var definition) && definition.Secret).ToList())
        {
            server.SetSecretVariable(variable.Key, protector.Protect(variable.Value!), by, now);
        }

        // Required values (and defaults) must still make a valid whole.
        try
        {
            _ = VariableValues.Resolve(manifest, Values(server));
        }
        catch (LaunchPlanException ex)
        {
            return ServerErrors.InvalidSettings(Describe(manifest, ex.Message));
        }

        return Result.Success();
    }

    /// <summary>
    /// The allocations of the port numbers asked for (<paramref name="numbers"/>; null leaves an optional port out), for
    /// <see cref="AssignPortsAsync"/>: the server's own on that number, otherwise a free one of the node (every interface
    /// first), otherwise a new one on every interface when <paramref name="canAdd"/> (the user manages the node). The
    /// ports that follow one get theirs the same way, on its address. New allocations are added to the context unsaved:
    /// the caller saves them with the server, its node's row locked (two of them must not overlap).
    /// </summary>
    public async Task<Result<PortNumberAllocations>> AllocationsForNumbersAsync(Server server, GameManifest manifest,
        IReadOnlyDictionary<string, int?> numbers, bool canAdd, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(numbers);
        var definitions = manifest.PortList.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var name in numbers.Keys)
        {
            if (!definitions.TryGetValue(name, out var definition))
            {
                return ServerErrors.InvalidSettings($"Unknown port '{name}'.");
            }

            if (definition.Follows is { } follows)
            {
                return ServerErrors.InvalidSettings($"{definition.Label} is not chosen: it comes with {definitions[follows.Port].Label}.");
            }
        }

        // Every number the server would listen on, the followers' too, once.
        var wanted = numbers.Where(n => n.Value is not null).SelectMany(n => manifest.PortList
                .Where(p => p.Follows?.Port == n.Key).Select(f => n.Value!.Value + f.Follows!.Offset).Prepend(n.Value!.Value))
            .ToList();
        if (wanted.Any(port => port is < 1 or > 65535))
        {
            return NodeErrors.AllocationPortInvalid;
        }

        if (wanted.Distinct().Count() != wanted.Count)
        {
            return ServerErrors.InvalidSettings("Two ports of the server cannot use the same port number.");
        }

        var nodeAllocations = await db.NodeAllocations.Where(a => a.NodeId == server.NodeId).ToListAsync(cancellationToken);
        var added = new List<NodeAllocation>();

        // The allocation of `port`: on `address` when given (a follower's, on its port's address).
        Result<NodeAllocation> Allocation(int port, string? address)
        {
            var onPort = nodeAllocations.Where(a => a.Port == port).ToList();
            var usable = onPort.Where(a => (a.ServerId is null || a.ServerId == server.Id) && (address is null || a.IpAddress == address))
                .OrderBy(a => a.ServerId == server.Id ? 0 : 1)
                .ThenBy(a => a.IpAddress == NodeAllocation.AnyAddress ? 0 : 1)
                .ThenBy(a => a.IpAddress, StringComparer.Ordinal)
                .FirstOrDefault();
            if (usable is not null)
            {
                return usable;
            }

            // A new allocation must not overlap one there is: another server's, or a free one on another address (a
            // follower stays on its port's address).
            var overlapping = onPort.Where(a => a.Overlaps(address ?? NodeAllocation.AnyAddress, port)).ToList();
            if (overlapping.Any(a => a.ServerId is not null && a.ServerId != server.Id))
            {
                return ServerErrors.PortTaken(port);
            }

            if (overlapping.Count > 0)
            {
                return NodeErrors.AllocationTaken;
            }

            if (!canAdd)
            {
                return ServerErrors.PortNotAllocated(port);
            }

            var created = NodeAllocation.Create(server.NodeId, address ?? NodeAllocation.AnyAddress, port, notes: null, now);
            if (!created.IsSuccess)
            {
                return created.Error!;
            }

            nodeAllocations.Add(created.Value);
            added.Add(created.Value);
            return created.Value;
        }

        var chosen = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var (name, number) in numbers)
        {
            if (number is not { } port)
            {
                chosen[name] = null;
                continue;
            }

            var allocation = Allocation(port, address: null);
            if (!allocation.IsSuccess)
            {
                return allocation.Error!;
            }

            foreach (var follower in manifest.PortList.Where(p => p.Follows?.Port == name))
            {
                var next = Allocation(port + follower.Follows!.Offset, allocation.Value.IpAddress);
                if (!next.IsSuccess)
                {
                    return next.Error!;
                }
            }

            chosen[name] = allocation.Value.Id.Value;
        }

        db.NodeAllocations.AddRange(added);
        return new PortNumberAllocations(chosen, added);
    }

    /// <summary>
    /// Gives the server one free allocation of its node per manifest port: <paramref name="requested"/> ones where
    /// given (null releases an optional port), otherwise the current one, otherwise the free allocation on the port's
    /// default (or the lowest free port) for required ports.
    /// </summary>
    /// <param name="prefer">Free allocations taken first for a port that needs one (those the server had for its old game).</param>
    public async Task<Result> AssignPortsAsync(Server server, GameManifest manifest, IReadOnlyDictionary<string, Guid?>? requested,
        CancellationToken cancellationToken, IReadOnlySet<AllocationId>? prefer = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(manifest);
        var definitions = manifest.PortList.ToDictionary(p => p.Name, StringComparer.Ordinal);
        if (requested?.Keys.FirstOrDefault(k => !definitions.ContainsKey(k)) is { } unknown)
        {
            return ServerErrors.InvalidSettings($"Unknown port '{unknown}'.");
        }

        var nodeAllocations = await db.NodeAllocations.Where(a => a.NodeId == server.NodeId).ToListAsync(cancellationToken);
        // Those added for this request's port numbers are not saved yet (AllocationsForNumbersAsync).
        nodeAllocations.AddRange(db.NodeAllocations.Local.Where(a => a.NodeId == server.NodeId && db.Entry(a).State == EntityState.Added));
        var mine = nodeAllocations.Where(a => a.ServerId == server.Id).ToList();
        var chosen = new Dictionary<string, NodeAllocation>(StringComparer.Ordinal);
        var followers = manifest.PortList.Where(p => p.Follows is not null).ToList();

        // The allocation a following port gets once the port it follows has one (free, or already the server's).
        NodeAllocation? FollowerOf(NodeAllocation followed, PortFollow follow) => nodeAllocations.FirstOrDefault(a =>
            a.IpAddress == followed.IpAddress && a.Port == followed.Port + follow.Offset && (a.ServerId is null || a.ServerId == server.Id));

        bool FollowersFit(PortDefinition port, NodeAllocation candidate) =>
            followers.Where(f => f.Follows!.Port == port.Name).All(f => FollowerOf(candidate, f.Follows!) is { } next && !chosen.ContainsValue(next));

        foreach (var port in manifest.PortList.Where(p => p.Follows is null))
        {
            NodeAllocation? allocation;
            if (requested is not null && requested.TryGetValue(port.Name, out var id))
            {
                if (id is null)
                {
                    if (port.Required)
                    {
                        return ServerErrors.InvalidSettings($"{port.Label} needs an allocation.");
                    }

                    continue;
                }

                allocation = nodeAllocations.FirstOrDefault(a => a.Id == new AllocationId(id.Value) && (a.ServerId is null || a.ServerId == server.Id));
                if (allocation is null)
                {
                    return ServerErrors.AllocationUnavailable;
                }
            }
            else
            {
                allocation = mine.FirstOrDefault(a => a.PortName == port.Name);
                if (allocation is null && port.Required)
                {
                    var free = nodeAllocations.Where(a => a.ServerId is null && !chosen.ContainsValue(a) && chosen.Values.All(c => c.Port != a.Port)
                            && FollowersFit(port, a))
                        .OrderBy(a => prefer?.Contains(a.Id) == true ? 0 : 1).ThenBy(a => a.Port == port.Default ? 0 : 1).ThenBy(a => a.Port).ThenBy(a => a.IpAddress, StringComparer.Ordinal);
                    allocation = free.FirstOrDefault();
                    if (allocation is null)
                    {
                        return ServerErrors.NoFreeAllocation;
                    }
                }

                if (allocation is null)
                {
                    continue;
                }
            }

            if (chosen.ContainsValue(allocation) || chosen.Values.Any(c => c.Port == allocation.Port))
            {
                return ServerErrors.InvalidSettings("Two ports of the server cannot use the same port number.");
            }

            chosen[port.Name] = allocation;
        }

        // Following ports are never chosen: they come with the port they follow (a requested one is ignored).
        foreach (var port in followers)
        {
            if (!chosen.TryGetValue(port.Follows!.Port, out var followed))
            {
                continue; // an optional port left out takes its followers with it
            }

            var next = FollowerOf(followed, port.Follows);
            if (next is null || chosen.ContainsValue(next))
            {
                return ServerErrors.InvalidSettings(
                    $"{port.Label} needs the allocation {followed.IpAddress}:{followed.Port + port.Follows.Offset}, free on the node, next to {followed.IpAddress}:{followed.Port}.");
            }

            chosen[port.Name] = next;
        }

        foreach (var allocation in mine.Where(a => !chosen.ContainsValue(a)))
        {
            allocation.Release();
        }

        foreach (var (name, allocation) in chosen)
        {
            allocation.Assign(server.Id, name);
        }

        return Result.Success();
    }

    /// <summary>The whole server renders: values, ports and extra arguments together (the agent checks again).</summary>
    public Result Check(Server server, GameManifest manifest, IReadOnlyCollection<NodeAllocation> allocations)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(allocations);
        if (server.ExtraArguments.Count > 0 && manifest.Runtime.GetValueOrDefault(Platform)?.AllowExtraArguments != true)
        {
            return ServerErrors.ExtraArgumentsNotAllowed;
        }

        var binding = new ServerBinding(server.ShortId, Values(server),
            allocations.ToDictionary(a => a.PortName!, a => new PortAssignment(a.IpAddress, a.Port)), server.ExtraArguments);
        return LaunchPlanRenderer.Check(manifest, Platform, binding) is { } error
            ? ServerErrors.InvalidSettings(Describe(manifest, error))
            : Result.Success();
    }

    /// <summary>Renderer messages name variables by key; users know them by label.</summary>
    private static string Describe(GameManifest manifest, string message)
    {
        foreach (var variable in manifest.VariableList)
        {
            if (message.StartsWith(variable.Key + ":", StringComparison.Ordinal))
            {
                return variable.Label + message[variable.Key.Length..];
            }
        }

        return message;
    }
}
