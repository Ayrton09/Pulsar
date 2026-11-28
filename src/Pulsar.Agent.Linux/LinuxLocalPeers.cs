using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Who holds the other end of a local connection, and who holds a UDP port, on Linux (docs/architecture/08, T18): the
/// kernel's socket tables (<c>/proc/net/tcp</c>, <c>udp</c> and their IPv6 ones) give each socket's inode, and the
/// process that has that socket open is found among <c>/proc/&lt;pid&gt;/fd</c>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxLocalPeers(LinuxServerAccounts accounts, string proc = "/proc") : ILocalPeers
{
    private const string Established = "01";
    private const string Listening = "0A";

    /// <summary>Stands for a socket no process is seen holding (never a process id).</summary>
    public const int Unheld = -1;

    public int? PeerProcess(IPEndPoint local, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        // The other end's socket: its local address is our remote one, and the other way round.
        var sockets = Sockets("tcp").Concat(Sockets("tcp6")).ToList();
        var peer = sockets.FirstOrDefault(s => s.State == Established && Same(s.Local, remote) && Same(s.Remote, local));
        if (peer is null)
        {
            return null;
        }

        if (peer.Inode != 0)
        {
            return ProcessesWith(new HashSet<ulong> { peer.Inode }).FirstOrDefault() is var pid and > 0 ? pid : null;
        }

        // Not accepted yet (in its listener's queue, the kernel shows no inode for it; a Source server accepts only on its
        // next frame, and a hibernating one waits): only who holds the socket listening there can ever accept it. One
        // process, or none: listeners held by several (SO_REUSEPORT, a socket passed on) fail closed.
        var listeners = sockets.Where(s => s.State == Listening && s.Local.Port == remote.Port && (IsAny(s.Local.Address) || Same(s.Local.Address, remote.Address)))
            .Select(s => s.Inode).ToHashSet();
        if (listeners.Count == 0 || listeners.Contains(0))
        {
            return null;
        }

        var holders = HoldersOf(listeners);
        var processes = holders.Values.SelectMany(pids => pids).Distinct().ToList();
        return holders.Count == listeners.Count && processes.Count == 1 ? processes[0] : null;
    }

    public IReadOnlyList<int> UdpProcesses(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        var matching = Sockets("udp").Concat(Sockets("udp6"))
            .Where(s => s.Local.Port == port && (IsAny(s.Local.Address) || Same(s.Local.Address, address))).ToList();
        if (matching.Count == 0)
        {
            return [];
        }

        // A socket nobody is seen holding (passed to another process and closed, or its inode 0) may still receive what is
        // sent there: it stands for a process that is not the game, so the check fails closed.
        var inodes = matching.Select(s => s.Inode).ToHashSet();
        var holders = HoldersOf(inodes);
        return [.. holders.Values.SelectMany(pids => pids).Distinct(), .. inodes.Any(i => i == 0 || !holders.ContainsKey(i)) ? [Unheld] : Array.Empty<int>()];
    }

    public bool RunsAs(int processId, ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        uint? expected = identity switch
        {
            ProcessIdentity.AccountIdentity account => accounts.Lookup(account.Name)?.Uid,
            _ => Libc.geteuid(),
        };
        return expected is { } uid && ProcFs.UserOf(processId) == uid;
    }

    /// <summary>One line of a socket table: its addresses, state and inode.</summary>
    internal sealed record Socket(IPEndPoint Local, IPEndPoint Remote, string State, ulong Inode);

    internal IEnumerable<Socket> Sockets(string table)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(proc, "net", table));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            yield break; // no IPv6 on this machine, say
        }

        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 9 && Endpoint(fields[1]) is { } local && Endpoint(fields[2]) is { } remote
                && ulong.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
            {
                yield return new Socket(local, remote, fields[3], inode);
            }
        }
    }

    /// <summary>
    /// An address as the tables write it: hexadecimal, in the machine's byte order by 32-bit words (little-endian on
    /// x86_64), then the port in big-endian hexadecimal.
    /// </summary>
    internal static IPEndPoint? Endpoint(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !int.TryParse(text[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port))
        {
            return null;
        }

        var hex = text[..colon];
        if (hex.Length is not (8 or 32))
        {
            return null;
        }

        var bytes = new byte[hex.Length / 2];
        for (var word = 0; word < bytes.Length / 4; word++)
        {
            for (var b = 0; b < 4; b++)
            {
                // Each word is written as a number: its lowest byte last.
                bytes[(word * 4) + b] = byte.Parse(hex.AsSpan((word * 8) + ((3 - b) * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        return new IPEndPoint(new IPAddress(bytes), port);
    }

    private static bool Same(IPEndPoint left, IPEndPoint right) => left.Port == right.Port && Same(left.Address, right.Address);

    // An IPv4 address and its IPv6 form (a dual-stack socket's) are the same address.
    private static bool Same(IPAddress left, IPAddress right) => left.MapToIPv6().Equals(right.MapToIPv6());

    private static bool IsAny(IPAddress address) => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    /// <summary>The processes that have one of these sockets open.</summary>
    private IEnumerable<int> ProcessesWith(IReadOnlySet<ulong> inodes) => HoldersOf(inodes).Values.SelectMany(pids => pids).Distinct();

    /// <summary>For each of these sockets that some process has open, those processes.</summary>
    private Dictionary<ulong, List<int>> HoldersOf(IReadOnlySet<ulong> inodes)
    {
        var holders = new Dictionary<ulong, List<int>>();
        var wanted = inodes.Where(i => i != 0).ToDictionary(i => $"socket:[{i.ToString(CultureInfo.InvariantCulture)}]", i => i, StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(proc))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            IEnumerable<string> descriptors;
            try
            {
                descriptors = Directory.EnumerateFileSystemEntries(Path.Combine(directory, "fd")).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // gone meanwhile
            }

            foreach (var descriptor in descriptors)
            {
                string? target;
                try
                {
                    target = new FileInfo(descriptor).LinkTarget;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (target is not null && wanted.TryGetValue(target, out var inode))
                {
                    (holders.TryGetValue(inode, out var pids) ? pids : holders[inode] = []).Add(pid);
                }
            }
        }

        return holders;
    }
}
