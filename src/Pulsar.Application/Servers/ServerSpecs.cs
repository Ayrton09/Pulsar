using System.Text;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

/// <summary>
/// What a server is made of beyond its row: its variable values and its port allocations, checked against its
/// manifest, and the runtime spec the agent receives (docs/architecture/03). Secret values are decrypted only here,
/// right before they are signed into a command.
/// </summary>
public sealed class ServerSpecs(IPulsarDbContext db, IVariableProtector protector)
{
    private const string Platform = ManifestValidator.WindowsX64;

    public static GameManifest Parse(GameTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(template.Content));
        return result.IsValid ? result.Manifest! : throw new InvalidOperationException($"Template {template.Key} {template.Version} is not valid anymore.");
    }

    /// <summary>The spec the agent renders the server from: manifest text, values (secrets decrypted) and ports.</summary>
    public async Task<ServerRuntimeSpec> BuildAsync(Server server, GameTemplate template, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(template);
        var allocations = await AllocationsOf(server.Id, cancellationToken);
        return new ServerRuntimeSpec(server.ShortId, template.Content, Values(server), server.ExtraArguments, server.AutoStart, server.RestartOnCrash,
            allocations.ToDictionary(a => a.PortName!, a => new ServerPort(a.IpAddress, a.Port)), Limits(server));
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
    /// Gives the server one free allocation of its node per manifest port: <paramref name="requested"/> ones where
    /// given (null releases an optional port), otherwise the current one, otherwise the free allocation on the port's
    /// default (or the lowest free port) for required ports.
    /// </summary>
    public async Task<Result> AssignPortsAsync(Server server, GameManifest manifest, IReadOnlyDictionary<string, Guid?>? requested,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(manifest);
        var definitions = manifest.PortList.ToDictionary(p => p.Name, StringComparer.Ordinal);
        if (requested?.Keys.FirstOrDefault(k => !definitions.ContainsKey(k)) is { } unknown)
        {
            return ServerErrors.InvalidSettings($"Unknown port '{unknown}'.");
        }

        var nodeAllocations = await db.NodeAllocations.Where(a => a.NodeId == server.NodeId).ToListAsync(cancellationToken);
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
                        .OrderBy(a => a.Port == port.Default ? 0 : 1).ThenBy(a => a.Port).ThenBy(a => a.IpAddress, StringComparer.Ordinal);
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
