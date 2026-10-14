using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// Firewall rules for servers (docs/architecture/06, "Windows Firewall"), when the node's administrator lets the agent
/// manage them: one inbound rule per server port and declared protocol, limited to the server's executable, and one
/// outbound rule that keeps the server's account from the panel's ports. Nothing else is ever opened (RCON's TCP side
/// stays closed: game ports declare UDP only), and the firewall is never turned off.
/// </summary>
public interface IServerFirewall
{
    /// <summary>Whether the agent manages rules on this node (announced to the panel).</summary>
    bool Enabled { get; }

    /// <summary>
    /// Replaces every rule of the server (whatever ports it had) with ones for <paramref name="ports"/>, and blocks
    /// <paramref name="identity"/>, when it is the server's own account, from the panel (before every start).
    /// </summary>
    void Allow(string shortId, string executablePath, IReadOnlyList<BoundPort> ports, ProcessIdentity identity);

    /// <summary>Removes the server's rules (when it is deleted).</summary>
    void Remove(string shortId);
}

/// <summary>The node's firewall is left to its administrator.</summary>
public sealed class UnmanagedFirewall : IServerFirewall
{
    public bool Enabled => false;

    public void Allow(string shortId, string executablePath, IReadOnlyList<BoundPort> ports, ProcessIdentity identity)
    {
    }

    public void Remove(string shortId)
    {
    }
}
