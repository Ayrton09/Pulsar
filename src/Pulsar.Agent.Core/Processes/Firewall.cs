using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// Inbound firewall rules for servers (docs/architecture/06, "Windows Firewall"), when the node's administrator lets the
/// agent manage them: one rule per server port and declared protocol, limited to the server's executable. Nothing else
/// is ever opened (RCON's TCP side stays closed: game ports declare UDP only), and the firewall is never turned off.
/// </summary>
public interface IServerFirewall
{
    /// <summary>Whether the agent manages rules on this node (announced to the panel).</summary>
    bool Enabled { get; }

    /// <summary>Replaces the server's rules with ones for <paramref name="ports"/> (before every start).</summary>
    /// <param name="portNames">Every port name of the server's manifest: rules of ports no longer bound are removed.</param>
    void Allow(string shortId, string executablePath, IReadOnlyList<BoundPort> ports, IReadOnlyCollection<string> portNames);

    /// <summary>Removes the server's rules (when it is deleted).</summary>
    void Remove(string shortId, IReadOnlyCollection<string> portNames);
}

/// <summary>The node's firewall is left to its administrator.</summary>
public sealed class UnmanagedFirewall : IServerFirewall
{
    public bool Enabled => false;

    public void Allow(string shortId, string executablePath, IReadOnlyList<BoundPort> ports, IReadOnlyCollection<string> portNames)
    {
    }

    public void Remove(string shortId, IReadOnlyCollection<string> portNames)
    {
    }
}
