using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>Which process holds an IPv4 TCP connection, from Windows' TCP table (<c>GetExtendedTcpTable</c>).</summary>
internal static partial class TcpOwners
{
    private const int TcpTableOwnerPidAll = 5;
    private const int RowSize = 24;

    /// <summary>The process whose socket is bound to <paramref name="local"/>; null when none is (or off Windows).</summary>
    public static int? ProcessOf(IPEndPoint local)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (!OperatingSystem.IsWindows() || local.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, 0, (int)AddressFamily.InterNetwork, TcpTableOwnerPidAll, 0);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(table, ref size, 0, (int)AddressFamily.InterNetwork, TcpTableOwnerPidAll, 0) != 0)
                {
                    continue; // grew meanwhile: size holds the new one
                }

                var bytes = new byte[size];
                Marshal.Copy(table, bytes, 0, size);
                var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                var address = BinaryPrimitives.ReadUInt32LittleEndian(local.Address.GetAddressBytes());
                for (var row = 0; row < count; row++)
                {
                    var entry = bytes.AsSpan(4 + (row * RowSize), RowSize);
                    var port = BinaryPrimitives.ReadUInt16BigEndian(entry[8..10]);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(entry[4..8]) == address && port == local.Port)
                    {
                        return BinaryPrimitives.ReadInt32LittleEndian(entry[20..24]);
                    }
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        return null;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, int order, int addressFamily, int tableClass, uint reserved);
}
