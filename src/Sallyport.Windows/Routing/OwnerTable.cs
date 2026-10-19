using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sallyport.Windows.Routing;

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
    private const uint AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    // MIB_TCPROW_OWNER_PID: dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid — six DWORDs.
    internal const int TcpRowSize = 24;
    // MIB_UDPROW_OWNER_PID: dwLocalAddr, dwLocalPort, dwOwningPid — three DWORDs.
    internal const int UdpRowSize = 12;

    private readonly Dictionary<(uint, ushort, uint, ushort), int> _tcp = [];
    private readonly Dictionary<(uint, ushort), int> _udp = [];
    private byte[] _buffer = new byte[64 * 1024];

    public int Refreshes { get; private set; }

    /// <summary>How long the last refresh took — both tables read and indexed.</summary>
    public TimeSpan LastRefresh { get; private set; }

    public void Refresh()
    {
        var started = Stopwatch.GetTimestamp();
        var tcp = Read(TCP_TABLE_OWNER_PID_ALL, tcp: true);
        IndexTcp(_buffer.AsSpan(0, tcp), _tcp);
        var udp = Read(UDP_TABLE_OWNER_PID, tcp: false);
        IndexUdp(_buffer.AsSpan(0, udp), _udp);
        Refreshes++;
        LastRefresh = Stopwatch.GetElapsedTime(started);
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

    private static int Rows(ReadOnlySpan<byte> table, int rowSize) =>
        table.Length < 4 ? 0 : (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table), (uint)((table.Length - 4) / rowSize));

    private static ushort Port(ReadOnlySpan<byte> field) => BinaryPrimitives.ReadUInt16BigEndian(field);

    private unsafe int Read(int tableClass, bool tcp)
    {
        while (true)
        {
            var size = (uint)_buffer.Length;
            uint result;
            fixed (byte* p = _buffer)
                result = tcp ? GetExtendedTcpTable(p, ref size, false, AF_INET, tableClass, 0) : GetExtendedUdpTable(p, ref size, false, AF_INET, tableClass, 0);
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
