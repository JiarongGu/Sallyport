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
}

/// <summary>Why the tunnel's packet was not handed up.</summary>
public enum InboundRefusal { None, NotIpv4, NotToTunnel, NotTcpOrUdp, Fragment, NoMapping, UnknownPeer, Malformed }

/// <summary>
/// Moves a selected program's packets between this machine's address and the tunnel's (docs/design/router.md §5).
/// Outbound, the source becomes the tunnel's address and a tunnel port — the program's own port where it is free, so a
/// peer-to-peer game keeps the port it announced (the server's NAT preserves ports, measured: game-routing amendment 1).
/// One mapping per local endpoint whatever peers it talks to: endpoint-independent, what peer-to-peer needs. Inbound,
/// only a packet from a peer the mapping has sent to is turned back; anything else is refused (no hosting yet).
/// </summary>
public sealed class Nat(uint tunnelAddress, ushort mss = Nat.DefaultMss)
{
    /// <summary>The MSS a routed TCP SYN is clamped to. The server's MTU is not measured yet (gate 5): 1360 leaves room
    /// under a 1500-byte path for the outer IPv4/UDP headers, the data channel's framing and its 16-byte tag.</summary>
    public const ushort DefaultMss = 1360;

    private const int FirstDynamicPort = 49152;

    private readonly Lock _lock = new();
    private readonly Dictionary<(byte, uint, ushort), NatMapping> _byLocal = [];
    private readonly Dictionary<(byte, ushort), NatMapping> _byTunnelPort = [];
    private int _nextPort = FirstDynamicPort;

    public uint TunnelAddress { get; } = tunnelAddress;

    public int Count { get { lock (_lock) return _byLocal.Count; } }

    /// <summary>Rewrites an outbound IPv4 TCP/UDP packet in place for the tunnel; <paramref name="link"/> is the frame's
    /// link header, remembered for replies. Null when no tunnel port is free — the packet must then go nowhere.</summary>
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
        }

        BinaryPrimitives.WriteUInt32LittleEndian(ip[12..], TunnelAddress);
        BinaryPrimitives.WriteUInt16BigEndian(segment, mapping.TunnelPort);
        if (protocol == Flow.Tcp) ClampMss(segment);
        Checksum.WriteIpv4Header(ip, headerLength);
        Checksum.WriteTransport(ip, headerLength);
        return mapping;
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
        if (protocol is not (Flow.Tcp or Flow.Udp)) return InboundRefusal.NotTcpOrUdp;
        // Offset or more-fragments: a piece of a datagram, not one that can be turned back on its own.
        if ((BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x3FFF) != 0) return InboundRefusal.Fragment;

        var segment = ip[headerLength..];
        var peer = (BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]), BinaryPrimitives.ReadUInt16BigEndian(segment));
        lock (_lock)
        {
            if (!_byTunnelPort.TryGetValue((protocol, BinaryPrimitives.ReadUInt16BigEndian(segment[2..])), out mapping)) return InboundRefusal.NoMapping;
            if (!mapping.Peers.Contains(peer)) { mapping = null; return InboundRefusal.UnknownPeer; }
            mapping.PacketsIn++;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(ip[16..], mapping.LocalAddress);
        BinaryPrimitives.WriteUInt16BigEndian(segment[2..], mapping.LocalPort);
        Checksum.WriteIpv4Header(ip, headerLength);
        Checksum.WriteTransport(ip, headerLength);
        return InboundRefusal.None;
    }

    /// <summary>A reply frame for <paramref name="ip"/>: the mapping's link header, then the packet.</summary>
    public static byte[] Frame(NatMapping mapping, ReadOnlySpan<byte> ip)
    {
        var frame = new byte[mapping.ReplyLink.Length + ip.Length];
        mapping.ReplyLink.CopyTo(frame, 0);
        ip.CopyTo(frame.AsSpan(mapping.ReplyLink.Length));
        return frame;
    }

    private ushort? Allocate(byte protocol, ushort preferred)
    {
        if (!_byTunnelPort.ContainsKey((protocol, preferred))) return preferred;
        for (var tried = 0; tried < 65536 - FirstDynamicPort; tried++)
        {
            var port = (ushort)_nextPort;
            _nextPort = _nextPort == 65535 ? FirstDynamicPort : _nextPort + 1;
            if (!_byTunnelPort.ContainsKey((protocol, port))) return port;
        }
        return null;
    }

    private static byte[] Swapped(ReadOnlySpan<byte> link)
    {
        var reply = link.ToArray();
        link[6..12].CopyTo(reply);
        link[..6].CopyTo(reply.AsSpan(6));
        return reply;
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
