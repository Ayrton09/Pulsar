using System.Net;
using System.Net.Sockets;
using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>Who holds the other end of a TCP connection between two processes of this machine.</summary>
public interface ILocalPeers
{
    /// <summary>
    /// The process at the other end of the connection from <paramref name="local"/> to <paramref name="remote"/> (the
    /// one that listens, and accepted it); null when none of this machine's processes holds it.
    /// </summary>
    int? PeerProcess(IPEndPoint local, IPEndPoint remote);

    /// <summary>Whether the process runs as <paramref name="identity"/>; false when it is gone or cannot be asked.</summary>
    bool RunsAs(int processId, ProcessIdentity identity);
}

/// <summary>
/// The RCON password, and the commands, go only to the server's own game (docs/architecture/08, T18): another server's
/// account (a plugin) could listen on a stopped server's RCON port and take what the agent sends there once the server
/// starts. Right after connecting, before anything is written, the connection's other end must be a process running as
/// the server's account.
/// </summary>
internal static class RconPeers
{
    /// <exception cref="RconException">The other end is not the server's game (<see cref="RconException.NotTheGame"/>).</exception>
    public static void Verify(ILocalPeers? peers, Socket socket, RconEndpoint endpoint, ProcessIdentity owner)
    {
        if (peers is null)
        {
            return; // no way to tell on this platform (tests)
        }

        try
        {
            if (socket.LocalEndPoint is IPEndPoint local && socket.RemoteEndPoint is IPEndPoint remote
                && peers.PeerProcess(local, remote) is { } process && peers.RunsAs(process, owner))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The system could not say who holds the connection (its table, an account it cannot look up): what cannot be
            // verified is not sent the password, and the callers, which expect RconException, keep waiting for the game.
            throw new RconException(
                $"Pulsar could not check who listens at {endpoint.Address}:{endpoint.Port} ({ex.Message}), so it does not send it the password or commands.", ex)
            {
                NotTheGame = true,
            };
        }

        throw new RconException(
            $"What listens at {endpoint.Address}:{endpoint.Port} is not the server's game, so Pulsar does not send it the password or commands.")
        {
            NotTheGame = true,
        };
    }
}
