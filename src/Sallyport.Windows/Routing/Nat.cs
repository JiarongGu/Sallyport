using System.Buffers.Binary;

namespace Sallyport.Windows.Routing;

/// <summary>
/// One local endpoint of a selected program as the tunnel carries it: its own address and port, the tunnel port that
/// stands for them, the peers it has sent to, and the link header a reply is handed up under.
/// </summary>
public sealed class NatMapping
{
    internal NatMapping(byte protocol, uint localAddress, ushort localPort, ushort tunnelPort)
    {
        Protocol = protocol;
        LocalAddress = localAddress;
        LocalPort = localPort;
        TunnelPort = tunnelPort;
    }

    /// <summary>The process its socket's routed packets were last counted for, so what comes back is counted for it too.</summary>
    internal ProcessRouted? Tally;

    public byte Protocol { get; }
    public uint LocalAddress { get; }
    public ushort LocalPort { get; }
    public ushort TunnelPort { get; }
    internal HashSet<(uint Address, ushort Port)> Peers { get; } = [];
    /// <summary>The outbound frame's link header with its two addresses swapped: a reply's, from the gateway to this
    /// machine.</summary>
    internal byte[] ReplyLink { get; set; } = [];
    public long PacketsOut { get; internal set; }
    public long PacketsIn { get; internal set; }
    /// <summary>Environment.TickCount64 of its last packet either way.</summary>
    public long LastUsed { get; internal set; } = Environment.TickCount64;
}

/// <summary>Why the tunnel's packet was not handed up. <see cref="PieceAhead"/>: a later piece of a datagram that came
/// before the first, which alone carries the ports that name its mapping.</summary>
public enum InboundRefusal { None, NotIpv4, NotToTunnel, NotTcpOrUdp, PieceAhead, NoMapping, UnknownPeer, Malformed }

/// <summary>
/// Moves a selected program's packets between this machine's address and the tunnel's (docs/design/router.md §5).
/// Outbound, the source becomes the tunnel's address and a tunnel port — the program's own port where it is free, so a
/// peer-to-peer game keeps the port it announced (the server's NAT preserves ports, measured: game-routing amendment 1).
/// One mapping per local endpoint whatever peers it talks to: endpoint-independent, what peer-to-peer needs. Inbound,
/// only a packet from a peer the mapping has sent to is turned back; anything else is refused (no hosting yet). The
/// pieces of a fragmented datagram follow their first piece's mapping, and an ICMP error follows the mapping of the
/// packet it quotes.
/// </summary>
public sealed class Nat(uint tunnelAddress, ushort mss = Nat.DefaultMss, int mtu = Nat.DefaultMtu)
{
    /// <summary>The MSS a routed TCP SYN is clamped to, either way: 1360 leaves room under a 1500-byte path for the outer
    /// IPv4/UDP headers, the data channel's framing and its 16-byte tag.</summary>
    public const ushort DefaultMss = 1360;

    /// <summary>The largest packet the tunnel carries in one outer UDP datagram on a 1500-byte path: 1500 less the outer
    /// IPv4 (20) and UDP (8) headers and the data channel's own 24 (opcode and peer id 4, packet id 4, tag 16).</summary>
    public const int DefaultMtu = 1500 - 20 - 8 - 24;

    private const int FirstDynamicPort = 49152;
    private const byte Icmp = 1;
    // How long the first piece of a datagram from the tunnel names the mapping its later pieces follow. Windows waits
    // 60 s to reassemble; a datagram's pieces come within milliseconds of each other.
    private const long PieceLifeMs = 30_000;
    private const int MostPieceSets = 1024;

    private readonly Lock _lock = new();
    private readonly Dictionary<(byte, uint, ushort), NatMapping> _byLocal = [];
    private readonly Dictionary<(byte, ushort), NatMapping> _byTunnelPort = [];
    private readonly Dictionary<(uint Source, byte Protocol, ushort Id), (NatMapping Mapping, long Since)> _inboundPieces = [];
    private int _nextPort = FirstDynamicPort;

    public uint TunnelAddress { get; } = tunnelAddress;

    /// <summary>The largest packet sent into the tunnel whole (<see cref="DefaultMtu"/>).</summary>
    public int Mtu { get; } = mtu;

    /// <summary>The largest MSS a SYN from the tunnel offered before it was clamped: whether the server clamps it
    /// itself (OpenVPN's mssfix) or leaves it to this side.</summary>
    public ushort LargestPeerMss { get; private set; }

    public int Count { get { lock (_lock) return _byLocal.Count; } }

    /// <summary>Rewrites an outbound IPv4 TCP/UDP packet — a whole datagram or the first piece of one — in place for the
    /// tunnel; <paramref name="link"/> is the frame's link header, remembered for replies. Null when no tunnel port is
    /// free: the packet must then go nowhere.</summary>
    public NatMapping? Outbound(Span<byte> ip, ReadOnlySpan<byte> link)
    {
        var headerLength = (ip[0] & 0x0F) * 4;
        var protocol = ip[9];
        var segment = ip[headerLength..];
        var localAddress = BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]);
        var localPort = BinaryPrimitives.ReadUInt16BigEndian(segment);
        var peer = (BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]), BinaryPrimitives.ReadUInt16BigEndian(segment[2..]));

        NatMapping? mapping;
        lock (_lock)
        {
            if (!_byLocal.TryGetValue((protocol, localAddress, localPort), out mapping))
            {
                if (Allocate(protocol, localPort) is not { } tunnelPort) return null;
                mapping = new NatMapping(protocol, localAddress, localPort, tunnelPort) { ReplyLink = Swapped(link) };
                _byLocal[(protocol, localAddress, localPort)] = mapping;
                _byTunnelPort[(protocol, tunnelPort)] = mapping;
            }
            mapping.Peers.Add(peer);
            mapping.PacketsOut++;
            mapping.LastUsed = Environment.TickCount64;
        }

        if (MorePieces(ip))
        {
            // The checksum covers the whole datagram, most of it in later pieces: carried over the change instead.
            Span<byte> before = stackalloc byte[6];
            ip.Slice(12, 4).CopyTo(before);
            segment[..2].CopyTo(before[4..]);
            BinaryPrimitives.WriteUInt32LittleEndian(ip[12..], TunnelAddress);
            BinaryPrimitives.WriteUInt16BigEndian(segment, mapping.TunnelPort);
            AdjustTransport(protocol, segment, before, ip.Slice(12, 4), segment[..2]);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ip[12..], TunnelAddress);
            BinaryPrimitives.WriteUInt16BigEndian(segment, mapping.TunnelPort);
            if (protocol == Flow.Tcp) ClampMss(segment);
            Checksum.WriteTransport(ip, headerLength);
        }
        Checksum.WriteIpv4Header(ip, headerLength);
        return mapping;
    }

    /// <summary>Rewrites a later piece of an outbound datagram whose first piece went through <see cref="Outbound"/>:
    /// its source address only, the ports and the checksum being in the first.</summary>
    public void OutboundPiece(Span<byte> ip)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(ip[12..], TunnelAddress);
        Checksum.WriteIpv4Header(ip, (ip[0] & 0x0F) * 4);
    }

    /// <summary>Rewrites a packet from the tunnel in place back to the program's own address and port.</summary>
    public InboundRefusal Inbound(Span<byte> ip, out NatMapping? mapping)
    {
        mapping = null;
        if (ip.Length < 20 || ip[0] >> 4 != 4) return InboundRefusal.NotIpv4;
        var headerLength = (ip[0] & 0x0F) * 4;
        var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        if (headerLength < 20 || total > ip.Length || total < headerLength + 8) return InboundRefusal.Malformed;
        if (BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]) != TunnelAddress) return InboundRefusal.NotToTunnel;
        var protocol = ip[9];
        var source = BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]);
        var id = BinaryPrimitives.ReadUInt16BigEndian(ip[4..]);
        if (LaterPiece(ip))
        {
            lock (_lock)
            {
                if (!_inboundPieces.TryGetValue((source, protocol, id), out var set)) return InboundRefusal.PieceAhead;
                mapping = set.Mapping;
                mapping.PacketsIn++;
                mapping.LastUsed = Environment.TickCount64;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(ip[16..], mapping.LocalAddress);
            Checksum.WriteIpv4Header(ip, headerLength);
            return InboundRefusal.None;
        }
        if (protocol == Icmp) return InboundError(ip, headerLength, total, out mapping);
        if (protocol is not (Flow.Tcp or Flow.Udp)) return InboundRefusal.NotTcpOrUdp;

        var segment = ip[headerLength..total];
        var peer = (source, BinaryPrimitives.ReadUInt16BigEndian(segment));
        var more = MorePieces(ip);
        lock (_lock)
        {
            if (!_byTunnelPort.TryGetValue((protocol, BinaryPrimitives.ReadUInt16BigEndian(segment[2..])), out mapping)) return InboundRefusal.NoMapping;
            if (!mapping.Peers.Contains(peer)) { mapping = null; return InboundRefusal.UnknownPeer; }
            mapping.PacketsIn++;
            mapping.LastUsed = Environment.TickCount64;
            if (more) RememberPieces((source, protocol, id), mapping);
        }

        if (more)
        {
            Span<byte> before = stackalloc byte[6];
            ip.Slice(16, 4).CopyTo(before);
            segment.Slice(2, 2).CopyTo(before[4..]);
            BinaryPrimitives.WriteUInt32LittleEndian(ip[16..], mapping.LocalAddress);
            BinaryPrimitives.WriteUInt16BigEndian(segment[2..], mapping.LocalPort);
            AdjustTransport(protocol, segment, before, ip.Slice(16, 4), segment.Slice(2, 2));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ip[16..], mapping.LocalAddress);
            BinaryPrimitives.WriteUInt16BigEndian(segment[2..], mapping.LocalPort);
            if (protocol == Flow.Tcp) ClampPeerMss(segment);
            Checksum.WriteTransport(ip, headerLength);
        }
        Checksum.WriteIpv4Header(ip, headerLength);
        return InboundRefusal.None;
    }

    // An ICMP error about a packet this side sent: destination unreachable (3), time exceeded (11) or parameter
    // problem (12), quoting that packet's IPv4 header and at least its first 8 bytes (RFC 792), so its ports. It is
    // turned back like a reply: only for a mapping whose peer the quoted packet went to.
    private InboundRefusal InboundError(Span<byte> ip, int headerLength, int total, out NatMapping? mapping)
    {
        mapping = null;
        var icmp = ip[headerLength..total];
        if (icmp[0] is not (3 or 11 or 12)) return InboundRefusal.NotTcpOrUdp;
        var quote = icmp[8..];
        if (quote.Length < 20 || quote[0] >> 4 != 4) return InboundRefusal.Malformed;
        var quotedHeader = (quote[0] & 0x0F) * 4;
        if (quotedHeader < 20 || quote.Length < quotedHeader + 8) return InboundRefusal.Malformed;
        var protocol = quote[9];
        if (protocol is not (Flow.Tcp or Flow.Udp) || LaterPiece(quote)) return InboundRefusal.NotTcpOrUdp;
        if (BinaryPrimitives.ReadUInt32LittleEndian(quote[12..]) != TunnelAddress) return InboundRefusal.NotToTunnel;
        var quoted = quote[quotedHeader..];
        var peer = (BinaryPrimitives.ReadUInt32LittleEndian(quote[16..]), BinaryPrimitives.ReadUInt16BigEndian(quoted[2..]));
        lock (_lock)
        {
            if (!_byTunnelPort.TryGetValue((protocol, BinaryPrimitives.ReadUInt16BigEndian(quoted)), out mapping)) return InboundRefusal.NoMapping;
            if (!mapping.Peers.Contains(peer)) { mapping = null; return InboundRefusal.UnknownPeer; }
            mapping.PacketsIn++;
            mapping.LastUsed = Environment.TickCount64;
        }

        Span<byte> before = stackalloc byte[6];
        quote.Slice(12, 4).CopyTo(before);
        quoted[..2].CopyTo(before[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(quote[12..], mapping.LocalAddress);
        BinaryPrimitives.WriteUInt16BigEndian(quoted, mapping.LocalPort);
        // The quoted packet's own transport checksum where the quote reaches it: UDP's is in its first 8 bytes, TCP's
        // at 16-17.
        AdjustTransport(protocol, quoted, before, quote.Slice(12, 4), quoted[..2]);
        Checksum.WriteIpv4Header(quote, quotedHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(ip[16..], mapping.LocalAddress);
        icmp[2] = icmp[3] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], Checksum.Fold(Checksum.Sum(icmp)));
        Checksum.WriteIpv4Header(ip, headerLength);
        return InboundRefusal.None;
    }

    private static bool MorePieces(ReadOnlySpan<byte> ip) => (ip[6] & 0x20) != 0;

    private static bool LaterPiece(ReadOnlySpan<byte> ip) => (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x1FFF) != 0;

    // A transport checksum carried over an address and port written since they were before: the first piece of a
    // datagram, or the packet an ICMP error quotes.
    private static void AdjustTransport(byte protocol, Span<byte> segment, ReadOnlySpan<byte> before, ReadOnlySpan<byte> address, ReadOnlySpan<byte> port)
    {
        var at = protocol == Flow.Tcp ? 16 : 6;
        if (segment.Length < at + 2) return;
        Span<byte> after = stackalloc byte[6];
        address.CopyTo(after);
        port.CopyTo(after[4..]);
        BinaryPrimitives.WriteUInt16BigEndian(segment[at..], Checksum.Adjust(BinaryPrimitives.ReadUInt16BigEndian(segment[at..]), before, after, protocol == Flow.Udp));
    }

    private void RememberPieces((uint, byte, ushort) key, NatMapping mapping)
    {
        var now = Environment.TickCount64;
        if (_inboundPieces.Count >= MostPieceSets)
            foreach (var old in _inboundPieces.Where(p => now - p.Value.Since > PieceLifeMs).Select(p => p.Key).ToList()) _inboundPieces.Remove(old);
        if (_inboundPieces.Count < MostPieceSets) _inboundPieces[key] = (mapping, now);
    }

    /// <summary>The peer of the protocol's mapping that has sent most — a game's server, by its own traffic.</summary>
    public uint? BusiestPeer(byte protocol)
    {
        lock (_lock)
            return _byLocal.Values.Where(m => m.Protocol == protocol && m.Peers.Count > 0).MaxBy(m => m.PacketsOut)?.Peers.First().Address;
    }

    /// <summary>Lets go of mappings idle since <paramref name="idleSince"/> (a TickCount64) that
    /// <paramref name="stillHeld"/> does not keep — their tunnel ports become free, their peers forgotten.</summary>
    public int Expire(long idleSince, Func<NatMapping, bool> stillHeld)
    {
        lock (_lock)
        {
            var gone = _byLocal.Where(m => m.Value.LastUsed < idleSince && !stillHeld(m.Value)).ToList();
            foreach (var (key, mapping) in gone)
            {
                _byLocal.Remove(key);
                _byTunnelPort.Remove((mapping.Protocol, mapping.TunnelPort));
            }
            var now = Environment.TickCount64;
            foreach (var old in _inboundPieces.Where(p => now - p.Value.Since > PieceLifeMs || gone.Any(g => g.Value == p.Value.Mapping)).Select(p => p.Key).ToList())
                _inboundPieces.Remove(old);
            return gone.Count;
        }
    }

    /// <summary>A reply frame for <paramref name="ip"/>: the mapping's link header, then the packet.</summary>
    public static byte[] Frame(NatMapping mapping, ReadOnlySpan<byte> ip)
    {
        var frame = new byte[mapping.ReplyLink.Length + ip.Length];
        mapping.ReplyLink.CopyTo(frame, 0);
        ip.CopyTo(frame.AsSpan(mapping.ReplyLink.Length));
        return frame;
    }

    // The program's own port where it is free and the router's to give: never one of the proxy's on a shared channel
    // (Sallyport.Net.TunnelPorts), nor a well-known one.
    private ushort? Allocate(byte protocol, ushort preferred)
    {
        if (preferred >= 1024 && !Sallyport.Net.TunnelPorts.IsProxy(preferred) && !_byTunnelPort.ContainsKey((protocol, preferred))) return preferred;
        for (var tried = 0; tried < 65536 - FirstDynamicPort; tried++)
        {
            var port = (ushort)_nextPort;
            _nextPort = _nextPort == 65535 ? FirstDynamicPort : _nextPort + 1;
            if (!_byTunnelPort.ContainsKey((protocol, port))) return port;
        }
        return null;
    }

    internal static byte[] Swapped(ReadOnlySpan<byte> link)
    {
        var reply = link.ToArray();
        link[6..12].CopyTo(reply);
        link[..6].CopyTo(reply.AsSpan(6));
        return reply;
    }

    // A SYN from the tunnel is clamped too: the MSS it offers is what Windows sends to it, and those segments must fit.
    private void ClampPeerMss(Span<byte> tcp)
    {
        if ((tcp[13] & 0x02) != 0 && Mss(tcp) is { } offered && offered > LargestPeerMss) LargestPeerMss = offered;
        ClampMss(tcp);
    }

    private static ushort? Mss(ReadOnlySpan<byte> tcp)
    {
        var end = Math.Min((tcp[12] >> 4) * 4, tcp.Length);
        for (var at = 20; at < end;)
        {
            var kind = tcp[at];
            if (kind == 0) return null;
            if (kind == 1) { at++; continue; }
            if (at + 1 >= end || tcp[at + 1] < 2) return null;
            if (kind == 2 && tcp[at + 1] == 4 && at + 4 <= end) return BinaryPrimitives.ReadUInt16BigEndian(tcp[(at + 2)..]);
            at += tcp[at + 1];
        }
        return null;
    }

    // A SYN's MSS option (kind 2, length 4) lowered to what the tunnel carries whole.
    private void ClampMss(Span<byte> tcp)
    {
        if ((tcp[13] & 0x02) == 0) return;
        var end = Math.Min((tcp[12] >> 4) * 4, tcp.Length);
        for (var at = 20; at < end;)
        {
            var kind = tcp[at];
            if (kind == 0) return;
            if (kind == 1) { at++; continue; }
            if (at + 1 >= end || tcp[at + 1] < 2) return;
            if (kind == 2 && tcp[at + 1] == 4 && at + 4 <= end)
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(tcp[(at + 2)..]) > mss) BinaryPrimitives.WriteUInt16BigEndian(tcp[(at + 2)..], mss);
                return;
            }
            at += tcp[at + 1];
        }
    }
}
