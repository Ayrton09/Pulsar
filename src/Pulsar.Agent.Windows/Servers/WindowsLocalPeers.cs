using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// The process at the other end of a local TCP connection, from Windows' table of connections and their owning
/// processes (<c>GetExtendedTcpTable</c>), and the account it runs as, from its token (docs/architecture/08, T18).
/// </summary>
public sealed class WindowsLocalPeers : ILocalPeers
{
    private const int Ipv4RowSize = 24; // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, process
    private const int Ipv6RowSize = 56; // MIB_TCP6ROW_OWNER_PID: local address and scope, port; remote likewise; state, process

    public int? PeerProcess(IPEndPoint local, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        local = Plain(local);
        remote = Plain(remote);
        if (local.AddressFamily != remote.AddressFamily)
        {
            return null;
        }

        // The peer's row is this connection seen from the other side: its local end is our remote one.
        var ipv4 = remote.AddressFamily == AddressFamily.InterNetwork;
        var table = Table(remote.AddressFamily);
        var rowSize = ipv4 ? Ipv4RowSize : Ipv6RowSize;
        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        for (var i = 0; i < count && 4 + ((i + 1) * rowSize) <= table.Length; i++)
        {
            var row = table.AsSpan(4 + (i * rowSize), rowSize);
            var (rowLocal, rowRemote, process) = ipv4
                ? (Endpoint(row[4..8], row[8..12]), Endpoint(row[12..16], row[16..20]), BinaryPrimitives.ReadInt32LittleEndian(row[20..]))
                : (Endpoint(row[..16], row[20..24], row[16..20]), Endpoint(row[24..40], row[44..48], row[40..44]), BinaryPrimitives.ReadInt32LittleEndian(row[52..]));
            if (rowLocal.Equals(remote) && rowRemote.Equals(local))
            {
                return process;
            }
        }

        return null;
    }

    public bool RunsAs(int processId, ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        SecurityIdentifier? expected;
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            expected = identity is ProcessIdentity.AccountIdentity account
                ? (SecurityIdentifier)new NTAccount(account.Name).Translate(typeof(SecurityIdentifier))
                : current.User;
        }
        catch (IdentityNotMappedException)
        {
            return false; // the account is gone
        }

        // Gone, or a protected process (never a server's game): nothing to ask.
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsInvalid || !PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return false;
        }

        using (token)
        using (var user = new WindowsIdentity(token.DangerousGetHandle()))
        {
            return user.User is { } sid && sid == expected;
        }
    }

    /// <summary>The connections of one address family with their owning processes, as Windows returns them.</summary>
    private static unsafe byte[] Table(AddressFamily family)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new byte[Math.Max(size, 4)];
            uint error;
            fixed (byte* table = buffer)
            {
                error = PInvoke.GetExtendedTcpTable(table, &size, false, (uint)family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            }

            if (error == (uint)WIN32_ERROR.NO_ERROR)
            {
                return buffer;
            }

            if (error != (uint)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new Win32Exception((int)error);
            }

            size += 4096; // connections opened meanwhile
        }

        throw new Win32Exception((int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER);
    }

    /// <summary>An address and a port as the table holds them: the address's bytes as they are, the port in network order.</summary>
    private static IPEndPoint Endpoint(ReadOnlySpan<byte> address, ReadOnlySpan<byte> port) => new(new IPAddress(address), (port[0] << 8) | port[1]);

    private static IPEndPoint Endpoint(ReadOnlySpan<byte> address, ReadOnlySpan<byte> port, ReadOnlySpan<byte> scope) =>
        new(new IPAddress(address, BinaryPrimitives.ReadUInt32LittleEndian(scope)), (port[0] << 8) | port[1]);

    private static IPEndPoint Plain(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : new IPEndPoint(endpoint.Address, endpoint.Port);
}
