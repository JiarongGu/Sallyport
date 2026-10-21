using System.Buffers.Binary;

namespace Sallyport.Windows.Routing;

/// <summary>Datagrams in pieces: a later piece carries no ports, so it follows the decision its first piece got; one
/// from the tunnel that comes before its first is held a moment for it.</summary>
public sealed partial class Router
{
    // A datagram's pieces leave within microseconds of each other, and through the tunnel within milliseconds.
    private const long PieceLifeMs = 30_000, AheadLifeMs = 2_000;
    private const int MostAhead = 64;

    // What the first piece of a datagram Windows sent in pieces was decided: its later pieces carry no ports.
    private readonly Dictionary<(uint, uint, byte, ushort), (FlowDecision Decision, long Since)> _pieces = [];
    private readonly Dictionary<(UInt128, UInt128, uint), (FlowDecision Decision, long Since)> _pieces6 = [];
    private readonly List<(int Lane, uint Source, byte Protocol, ushort Id, byte[] Packet, long At)> _ahead = [];

    // A later piece of a datagram Windows sent in pieces: it follows its first.
    private bool DecidePiece(ReadOnlySpan<byte> frame)
    {
        var link = LinkLength(frame);
        var ip = frame[link..];
        if (!_pieces.TryGetValue(PieceKey(ip), out var set)) { LonePieces++; return false; }
        switch (set.Decision.Verdict)
        {
            case Verdict.Direct: DirectPackets++; return false;
            case Verdict.Unowned: UnownedPackets++; return false;
            case Verdict.Blocked: Block(set.Decision.Reason, frame[..link], ip, tcpAt: -1); return true;
        }
        var lane = set.Decision.Lane;
        if (WhileDown(set.Decision.Pid, lane) is { } passed) return passed;
        var nat = NatOf(lane);
        if (nat is null) { HeldWhileDown++; return true; }
        var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        if (total > ip.Length) { HeldBack++; return true; }
        var copy = ip[..total].ToArray();
        nat.OutboundPiece(copy);
        PiecesRouted++;
        if (set.Decision.Tally is { } tally) Interlocked.Add(ref tally._bytesOut, total);
        ToTunnel(nat, copy);
        return true;
    }

    // Pieces held for a first piece of the tunnel that has gone: the next tunnel's numbers name other datagrams.
    private void ForgetAhead(int lane)
    {
        lock (_injectLock) PiecesAheadLost += _ahead.RemoveAll(a => a.Lane == lane);
    }

    private void HoldAhead(int lane, byte[] packet)
    {
        var now = Environment.TickCount64;
        PiecesAheadLost += _ahead.RemoveAll(a => now - a.At > AheadLifeMs);
        if (_ahead.Count >= MostAhead)
        {
            _ahead.RemoveAt(0);
            PiecesAheadLost++;
        }
        _ahead.Add((lane, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)), packet[9], BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)), packet, now));
        PiecesAhead++;
    }

    private void ReleaseAhead(int lane, byte[] first)
    {
        var source = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(12));
        var id = BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(4));
        foreach (var held in _ahead.Where(a => a.Lane == lane && a.Source == source && a.Protocol == first[9] && a.Id == id).ToList())
        {
            _ahead.Remove(held);
            if (NatOf(lane)?.Inbound(held.Packet, out var mapping) == InboundRefusal.None && Inject(Nat.Frame(mapping!, held.Packet))) HandedUp++;
            else PiecesAheadLost++;
        }
    }

    private static (uint, uint, byte, ushort) PieceKey(ReadOnlySpan<byte> ip) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]), BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]), ip[9], BinaryPrimitives.ReadUInt16BigEndian(ip[4..]));
}
