using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sallyport.Windows.Routing;

/// <summary>A process's TCP connections and UDP sockets, as the owner tables list them.</summary>
public readonly record struct ProcessSockets(int Tcp, int Udp);

/// <summary>What the owner tables say of a flow: one process owns it, none does, or more than one could.</summary>
public enum Ownership { Owned, Unowned, Ambiguous }

/// <summary>
/// Which process owns a flow, from Windows' owner tables as they stood at the last <see cref="Refresh"/>. TCP rows carry
/// both ends, so a TCP flow matches exactly; UDP rows carry only the local end (MIB_UDPROW_OWNER_PID), so a UDP flow
/// matches its local address and port, then a socket bound to every address on that port. A snapshot cannot see a
/// socket that opened after it, nor one that closed before it — the design's reason a miss is refreshed, and a miss
/// after that counted rather than guessed (docs/design/router.md, change 2).
/// </summary>
public sealed partial class OwnerTable
{
    private const uint AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    // MIB_TCPROW_OWNER_PID: dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid — six DWORDs.
    internal const int TcpRowSize = 24;
    // MIB_UDPROW_OWNER_PID: dwLocalAddr, dwLocalPort, dwOwningPid — three DWORDs.
    internal const int UdpRowSize = 12;
    // MIB_TCP6ROW_OWNER_PID: ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId,
    // dwRemotePort, dwState, dwOwningPid.
    internal const int Tcp6RowSize = 56;
    // MIB_UDP6ROW_OWNER_PID: ucLocalAddr[16], dwLocalScopeId, dwLocalPort, dwOwningPid.
    internal const int Udp6RowSize = 28;

    private readonly Dictionary<(uint, ushort, uint, ushort), int> _tcp = [];
    private readonly HashSet<(uint, ushort)> _tcpLocal = [];
    private readonly Dictionary<(uint, ushort), int> _udp = [];
    private readonly Dictionary<(UInt128, ushort, UInt128, ushort), int> _tcp6 = [];
    private readonly HashSet<(UInt128, ushort)> _tcp6Local = [];
    private readonly Dictionary<(UInt128, ushort), int> _udp6 = [];
    private byte[] _buffer = new byte[64 * 1024];

    public int Refreshes { get; private set; }

    /// <summary>How long the last refresh took — all four tables read and indexed.</summary>
    public TimeSpan LastRefresh { get; private set; }

    public void Refresh()
    {
        var started = Stopwatch.GetTimestamp();
        var tcp = Read(TCP_TABLE_OWNER_PID_ALL, tcp: true, AF_INET);
        IndexTcp(_buffer.AsSpan(0, tcp), _tcp);
        _tcpLocal.Clear();
        foreach (var (local, port, _, _) in _tcp.Keys) _tcpLocal.Add((local, port));
        var udp = Read(UDP_TABLE_OWNER_PID, tcp: false, AF_INET);
        IndexUdp(_buffer.AsSpan(0, udp), _udp);
        // IPv6 only to find a selected program's packets and keep them from leaving (they are not routed yet).
        var tcp6 = Read(TCP_TABLE_OWNER_PID_ALL, tcp: true, AF_INET6);
        IndexTcp6(_buffer.AsSpan(0, tcp6), _tcp6);
        _tcp6Local.Clear();
        foreach (var (local, port, _, _) in _tcp6.Keys) _tcp6Local.Add((local, port));
        var udp6 = Read(UDP_TABLE_OWNER_PID, tcp: false, AF_INET6);
        IndexUdp6(_buffer.AsSpan(0, udp6), _udp6);
        Refreshes++;
        LastRefresh = Stopwatch.GetElapsedTime(started);
    }

    /// <summary>As <see cref="Owner(in Flow, out int)"/>, for IPv6; a UDP socket on every address is bound to ::.</summary>
    public Ownership Owner(in Flow6 flow, out int pid)
    {
        if (flow.Protocol == Flow.Tcp)
        {
            var found = _tcp6.TryGetValue((flow.LocalAddress, flow.LocalPort, flow.RemoteAddress, flow.RemotePort), out var row);
            return Answer(found, row, out pid);
        }
        var specific = _udp6.TryGetValue((flow.LocalAddress, flow.LocalPort), out var specificPid);
        var wildcard = _udp6.TryGetValue((UInt128.Zero, flow.LocalPort), out var wildcardPid);
        if (specific && wildcard && specificPid != wildcardPid) { pid = 0; return Ownership.Ambiguous; }
        return specific ? Answer(true, specificPid, out pid) : Answer(wildcard, wildcardPid, out pid);
    }

    /// <summary>The process that owns the flow in the snapshot. Two processes on one UDP port — a socket on the
    /// packet's address and one on every address, or two sharing a port — make it <see cref="Ownership.Ambiguous"/>:
    /// either could have sent it.</summary>
    public Ownership Owner(in Flow flow, out int pid)
    {
        if (flow.Protocol == Flow.Tcp)
        {
            var found = _tcp.TryGetValue((flow.LocalAddress, flow.LocalPort, flow.RemoteAddress, flow.RemotePort), out var row);
            return Answer(found, row, out pid);
        }
        var specific = _udp.TryGetValue((flow.LocalAddress, flow.LocalPort), out var specificPid);
        var wildcard = _udp.TryGetValue((0u, flow.LocalPort), out var wildcardPid);
        if (specific && wildcard && specificPid != wildcardPid) { pid = 0; return Ownership.Ambiguous; }
        return specific ? Answer(true, specificPid, out pid) : Answer(wildcard, wildcardPid, out pid);
    }

    /// <summary>Each process's sockets in the snapshot: its TCP connections (rows with a far end; a listening socket has
    /// none) and its UDP sockets, IPv4 and IPv6 together.</summary>
    public IReadOnlyDictionary<int, ProcessSockets> SocketsByProcess() => CountByProcess(_tcp.Keys.Select(k => (k.Item3 != 0, _tcp[k])),
        _tcp6.Keys.Select(k => (k.Item3 != UInt128.Zero, _tcp6[k])), _udp.Values.Concat(_udp6.Values));

    internal static Dictionary<int, ProcessSockets> CountByProcess(IEnumerable<(bool Connected, int Pid)> tcp, IEnumerable<(bool Connected, int Pid)> tcp6,
        IEnumerable<int> udp)
    {
        var counts = new Dictionary<int, ProcessSockets>();
        foreach (var (connected, pid) in tcp.Concat(tcp6))
            if (connected && pid > 0) counts[pid] = counts.GetValueOrDefault(pid) with { Tcp = counts.GetValueOrDefault(pid).Tcp + 1 };
        foreach (var pid in udp)
            if (pid > 0) counts[pid] = counts.GetValueOrDefault(pid) with { Udp = counts.GetValueOrDefault(pid).Udp + 1 };
        return counts;
    }

    /// <summary>Every TCP row on a local port, for saying why a flow was not found: its ends and its process.</summary>
    public IEnumerable<string> TcpRowsOn(ushort localPort) => _tcp.Where(r => r.Key.Item2 == localPort)
        .Select(r => $"{new System.Net.IPAddress(r.Key.Item1)}:{r.Key.Item2} -> {new System.Net.IPAddress(r.Key.Item3)}:{r.Key.Item4} pid {r.Value}");

    /// <summary>Whether a socket still holds this local end in the snapshot — what keeps an idle mapping alive.</summary>
    public bool Holds(byte protocol, uint localAddress, ushort localPort) => protocol == Flow.Tcp
        ? _tcpLocal.Contains((localAddress, localPort))
        : _udp.ContainsKey((localAddress, localPort)) || _udp.ContainsKey((0u, localPort));

    public bool Holds(byte protocol, UInt128 localAddress, ushort localPort) => protocol == Flow.Tcp
        ? _tcp6Local.Contains((localAddress, localPort))
        : _udp6.ContainsKey((localAddress, localPort)) || _udp6.ContainsKey((UInt128.Zero, localPort));

    private static Ownership Answer(bool found, int row, out int pid)
    {
        pid = found && row != Shared ? row : 0;
        return !found ? Ownership.Unowned : row == Shared ? Ownership.Ambiguous : Ownership.Owned;
    }

    // An index entry two processes' rows both claimed.
    private const int Shared = -1;

    // A table's row count first (DWORD), then its rows; ports are network order in a DWORD's low two bytes.
    internal static void IndexTcp(ReadOnlySpan<byte> table, Dictionary<(uint, ushort, uint, ushort), int> index)
    {
        index.Clear();
        var rows = Rows(table, TcpRowSize);
        for (var i = 0; i < rows; i++)
        {
            var row = table.Slice(4 + i * TcpRowSize, TcpRowSize);
            var pid = BinaryPrimitives.ReadInt32LittleEndian(row[20..]);
            // A row in TIME_WAIT belongs to no process (PID 0): its packets are the stack's own, not a program's.
            if (pid == 0) continue;
            index[(BinaryPrimitives.ReadUInt32LittleEndian(row[4..]), Port(row[8..]), BinaryPrimitives.ReadUInt32LittleEndian(row[12..]), Port(row[16..]))] = pid;
        }
    }

    internal static void IndexUdp(ReadOnlySpan<byte> table, Dictionary<(uint, ushort), int> index)
    {
        index.Clear();
        var rows = Rows(table, UdpRowSize);
        for (var i = 0; i < rows; i++)
        {
            var row = table.Slice(4 + i * UdpRowSize, UdpRowSize);
            var key = (BinaryPrimitives.ReadUInt32LittleEndian(row), Port(row[4..]));
            var pid = BinaryPrimitives.ReadInt32LittleEndian(row[8..]);
            index[key] = index.TryGetValue(key, out var earlier) && earlier != pid ? Shared : pid;
        }
    }

    internal static void IndexTcp6(ReadOnlySpan<byte> table, Dictionary<(UInt128, ushort, UInt128, ushort), int> index)
    {
        index.Clear();
        var rows = Rows(table, Tcp6RowSize);
        for (var i = 0; i < rows; i++)
        {
            var row = table.Slice(4 + i * Tcp6RowSize, Tcp6RowSize);
            var pid = BinaryPrimitives.ReadInt32LittleEndian(row[52..]);
            if (pid == 0) continue;
            index[(BinaryPrimitives.ReadUInt128BigEndian(row), Port(row[20..]), BinaryPrimitives.ReadUInt128BigEndian(row[24..]), Port(row[44..]))] = pid;
        }
    }

    internal static void IndexUdp6(ReadOnlySpan<byte> table, Dictionary<(UInt128, ushort), int> index)
    {
        index.Clear();
        var rows = Rows(table, Udp6RowSize);
        for (var i = 0; i < rows; i++)
        {
            var row = table.Slice(4 + i * Udp6RowSize, Udp6RowSize);
            var key = (BinaryPrimitives.ReadUInt128BigEndian(row), Port(row[20..]));
            var pid = BinaryPrimitives.ReadInt32LittleEndian(row[24..]);
            index[key] = index.TryGetValue(key, out var earlier) && earlier != pid ? Shared : pid;
        }
    }

    private static int Rows(ReadOnlySpan<byte> table, int rowSize) =>
        table.Length < 4 ? 0 : (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table), (uint)((table.Length - 4) / rowSize));

    private static ushort Port(ReadOnlySpan<byte> field) => BinaryPrimitives.ReadUInt16BigEndian(field);

    private unsafe int Read(int tableClass, bool tcp, uint family)
    {
        while (true)
        {
            var size = (uint)_buffer.Length;
            uint result;
            fixed (byte* p = _buffer)
                result = tcp ? GetExtendedTcpTable(p, ref size, false, family, tableClass, 0) : GetExtendedUdpTable(p, ref size, false, family, tableClass, 0);
            if (result == 0) return (int)size;
            if (result != ERROR_INSUFFICIENT_BUFFER) throw new System.ComponentModel.Win32Exception((int)result);
            // Sockets open between the size answer and the next read: leave room for them.
            _buffer = new byte[size + size / 4 + 4096];
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetExtendedTcpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetExtendedUdpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
}
