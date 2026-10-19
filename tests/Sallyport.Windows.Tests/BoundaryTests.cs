using System.Buffers.Binary;
using System.Net;
using Sallyport.Windows.Routing;

namespace Sallyport.Windows.Tests;

/// <summary>Gate 5's packet boundaries (docs/design/router.md §6): pieces, ICMP errors, MSS both ways, the router's own
/// replies, IPv6 read past its extension headers.</summary>
public sealed class BoundaryTests
{
    private static readonly IPAddress Home = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress Tunnel = IPAddress.Parse("10.8.0.6");
    private static readonly IPAddress Server = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Hop = IPAddress.Parse("198.51.100.1");

    private static readonly byte[] Link =
        [0x02, 0xAA, 0xAA, 0xAA, 0xAA, 0x01, 0x02, 0xBB, 0xBB, 0xBB, 0xBB, 0x02, 0x08, 0x00];

    private static uint Address(IPAddress a) => BinaryPrimitives.ReadUInt32LittleEndian(a.GetAddressBytes());

    private static byte[] Udp(IPAddress from, ushort fromPort, IPAddress to, ushort toPort, int payload, ushort id = 0x1234, bool dontFragment = false)
    {
        var ip = new byte[20 + 8 + payload];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), id);
        if (dontFragment) ip[6] = 0x40;
        ip[8] = 64;
        ip[9] = Flow.Udp;
        from.GetAddressBytes().CopyTo(ip, 12);
        to.GetAddressBytes().CopyTo(ip, 16);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(20), fromPort);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(22), toPort);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(24), (ushort)(8 + payload));
        for (var i = 0; i < payload; i++) ip[28 + i] = (byte)(i * 7 + 3);
        ip[26] = 0xFF;
        Checksum.WriteIpv4Header(ip, 20);
        Checksum.WriteTransport(ip, 20);
        return ip;
    }

    private static byte[] Syn(IPAddress from, ushort fromPort, IPAddress to, ushort toPort, byte flags, ushort mss)
    {
        var ip = new byte[20 + 24];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
        ip[6] = 0x40;
        ip[8] = 64;
        ip[9] = Flow.Tcp;
        from.GetAddressBytes().CopyTo(ip, 12);
        to.GetAddressBytes().CopyTo(ip, 16);
        var tcp = ip.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, fromPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], toPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], 0x01020304);
        tcp[12] = 6 << 4;
        tcp[13] = flags;
        tcp[20] = 2;
        tcp[21] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[22..], mss);
        Checksum.WriteIpv4Header(ip, 20);
        Checksum.WriteTransport(ip, 20);
        return ip;
    }

    // The pieces put back together, as the far end's stack would.
    private static byte[] Reassemble(IEnumerable<byte[]> pieces)
    {
        var all = pieces.OrderBy(p => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)) & 0x1FFF).ToList();
        var payload = all.SelectMany(p => p.Skip(20)).ToArray();
        var whole = new byte[20 + payload.Length];
        all[0].AsSpan(0, 20).CopyTo(whole);
        payload.CopyTo(whole, 20);
        BinaryPrimitives.WriteUInt16BigEndian(whole.AsSpan(2), (ushort)whole.Length);
        whole[6] = whole[7] = 0;
        Checksum.WriteIpv4Header(whole, 20);
        return whole;
    }

    [Fact]
    public void A_datagram_split_into_pieces_goes_back_together_whole()
    {
        var datagram = Udp(Home, 50000, Server, 53, 3000);

        var pieces = Replies.Split(datagram, 1448);

        Assert.All(pieces, p => Assert.True(p.Length <= 1448));
        Assert.All(pieces[..^1], p => Assert.Equal(0x20, p[6] & 0x20));
        Assert.Equal(0, pieces[^1][6] & 0x20);
        Assert.All(pieces, p => Assert.Equal(0, Checksum.Fold(Checksum.Sum(p.AsSpan(0, 20)))));
        Assert.Equal(datagram, Reassemble(pieces));
    }

    [Fact]
    public void A_piece_split_again_keeps_more_fragments_on_its_last_part()
    {
        var first = Replies.Split(Udp(Home, 50000, Server, 53, 3000), 1500)[0];

        var parts = Replies.Split(first, 1448);

        Assert.All(parts, p => Assert.Equal(0x20, p[6] & 0x20));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(parts[0].AsSpan(6)) & 0x1FFF);
    }

    [Fact]
    public void A_datagram_Windows_sent_in_pieces_leaves_from_the_tunnel_with_a_checksum_that_holds()
    {
        var nat = new Nat(Address(Tunnel));
        var pieces = Replies.Split(Udp(Home, 50000, Server, 53, 3000), 1500);

        Assert.NotNull(nat.Outbound(pieces[0], Link));
        foreach (var later in pieces.Skip(1)) nat.OutboundPiece(later);

        var whole = Reassemble(pieces);
        Assert.Equal(Tunnel, new IPAddress(whole.AsSpan(12, 4)));
        Assert.Equal(50000, BinaryPrimitives.ReadUInt16BigEndian(whole.AsSpan(20)));
        Assert.True(Checksum.Verifies(whole));
    }

    [Fact]
    public void Pieces_from_the_tunnel_follow_their_first_and_one_ahead_of_it_waits()
    {
        var nat = new Nat(Address(Tunnel));
        nat.Outbound(Udp(Home, 50000, Server, 53, 40), Link);
        var pieces = Replies.Split(Udp(Server, 53, Tunnel, 50000, 3959, id: 0x7777), 1500);

        Assert.Equal(InboundRefusal.PieceAhead, nat.Inbound(pieces[2].ToArray(), out _));
        Assert.Equal(InboundRefusal.None, nat.Inbound(pieces[0], out var mapping));
        Assert.Equal(InboundRefusal.None, nat.Inbound(pieces[1], out var second));
        Assert.Equal(InboundRefusal.None, nat.Inbound(pieces[2], out _));
        Assert.Same(mapping, second);

        var whole = Reassemble(pieces);
        Assert.Equal(Home, new IPAddress(whole.AsSpan(16, 4)));
        Assert.Equal(50000, BinaryPrimitives.ReadUInt16BigEndian(whole.AsSpan(22)));
        Assert.True(Checksum.Verifies(whole));
    }

    [Fact]
    public void An_ICMP_error_about_a_routed_packet_is_turned_back_to_the_program()
    {
        var nat = new Nat(Address(Tunnel));
        var original = Udp(Home, 50000, Server, 33434, 12);
        var sent = original.ToArray();
        nat.Outbound(sent, Link);
        var error = Icmp(3, 3, Hop, Tunnel, sent);

        Assert.Equal(InboundRefusal.None, nat.Inbound(error, out _));

        Assert.Equal(Home, new IPAddress(error.AsSpan(16, 4)));
        var quote = error.AsSpan(28);
        Assert.Equal(original.AsSpan(0, 28).ToArray(), quote.ToArray());   // the program's own packet, checksums and all
        Assert.Equal(0, Checksum.Fold(Checksum.Sum(error.AsSpan(20))));
        Assert.Equal(0, Checksum.Fold(Checksum.Sum(error.AsSpan(0, 20))));
    }

    [Fact]
    public void An_ICMP_error_about_a_packet_to_another_peer_or_no_error_at_all_is_refused()
    {
        var nat = new Nat(Address(Tunnel));
        var sent = Udp(Home, 50000, Server, 33434, 12);
        nat.Outbound(sent, Link);
        var elsewhere = sent.ToArray();
        Hop.GetAddressBytes().CopyTo(elsewhere, 16);

        Assert.Equal(InboundRefusal.UnknownPeer, nat.Inbound(Icmp(3, 3, Hop, Tunnel, elsewhere), out _));
        Assert.Equal(InboundRefusal.NotTcpOrUdp, nat.Inbound(Icmp(0, 0, Server, Tunnel, sent), out _));
    }

    private static byte[] Icmp(byte type, byte code, IPAddress from, IPAddress to, byte[] about)
    {
        var quote = about.AsSpan(0, Math.Min(about.Length, 28));
        var ip = new byte[20 + 8 + quote.Length];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
        ip[8] = 64;
        ip[9] = 1;
        from.GetAddressBytes().CopyTo(ip, 12);
        to.GetAddressBytes().CopyTo(ip, 16);
        ip[20] = type;
        ip[21] = code;
        quote.CopyTo(ip.AsSpan(28));
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(22), Checksum.Fold(Checksum.Sum(ip.AsSpan(20))));
        Checksum.WriteIpv4Header(ip, 20);
        return ip;
    }

    [Fact]
    public void A_SYN_from_the_tunnel_is_clamped_and_what_it_offered_is_kept()
    {
        var nat = new Nat(Address(Tunnel));
        nat.Outbound(Syn(Home, 50001, Server, 443, 0x02, 1460), Link);
        var synAck = Syn(Server, 443, Tunnel, 50001, 0x12, 1460);

        Assert.Equal(InboundRefusal.None, nat.Inbound(synAck, out _));

        Assert.Equal(Nat.DefaultMss, BinaryPrimitives.ReadUInt16BigEndian(synAck.AsSpan(20 + 22)));
        Assert.Equal(1460, nat.LargestPeerMss);
        Assert.True(Checksum.Verifies(synAck));
    }

    [Fact]
    public void Fragmentation_needed_quotes_the_packet_and_names_the_tunnels_size()
    {
        var big = Udp(Home, 50000, Server, 19302, 1472, dontFragment: true);

        var reply = Replies.FragmentationNeeded(big, Nat.DefaultMtu);

        Assert.Equal(Server, new IPAddress(reply.AsSpan(12, 4)));
        Assert.Equal(Home, new IPAddress(reply.AsSpan(16, 4)));
        Assert.Equal((byte)3, reply[20]);
        Assert.Equal((byte)4, reply[21]);
        Assert.Equal(Nat.DefaultMtu, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(26)));
        Assert.Equal(big.AsSpan(0, 28).ToArray(), reply.AsSpan(28).ToArray());
        Assert.Equal(0, Checksum.Fold(Checksum.Sum(reply.AsSpan(20))));
        Assert.Equal(0, Checksum.Fold(Checksum.Sum(reply.AsSpan(0, 20))));
    }

    [Fact]
    public void A_reset_answers_the_SYN_it_refuses()
    {
        var syn = Syn(Home, 50001, Server, 443, 0x02, 1460);

        var reset = Replies.Reset(syn);

        Assert.Equal(Server, new IPAddress(reset.AsSpan(12, 4)));
        Assert.Equal(Home, new IPAddress(reset.AsSpan(16, 4)));
        Assert.Equal(443, BinaryPrimitives.ReadUInt16BigEndian(reset.AsSpan(20)));
        Assert.Equal(50001, BinaryPrimitives.ReadUInt16BigEndian(reset.AsSpan(22)));
        Assert.Equal(0x01020305u, BinaryPrimitives.ReadUInt32BigEndian(reset.AsSpan(28)));
        Assert.Equal((byte)0x14, reset[33]);
        Assert.True(Checksum.Verifies(reset));
    }

    [Fact]
    public void An_IPv6_SYN_is_read_past_its_extension_headers_and_its_reset_holds()
    {
        var frame = Ipv6Syn(hopByHop: true);

        Assert.Equal(FrameKind.Flow, Frames.TryReadOutbound6(frame, out var flow, out var transport, out var fragmentId));
        Assert.Equal(Flow.Tcp, flow.Protocol);
        Assert.Equal(IPAddress.Parse("2001:db8::20"), flow.Local);
        Assert.Equal(IPAddress.Parse("2001:db8:1::7"), flow.Remote);
        Assert.Equal(50001, flow.LocalPort);
        Assert.Equal(443, flow.RemotePort);
        Assert.Equal(48, transport);
        Assert.Null(fragmentId);

        var reset = Replies.Reset6(frame.AsSpan(14), transport);
        Assert.Equal(IPAddress.Parse("2001:db8:1::7"), new IPAddress(reset.AsSpan(8, 16)));
        Assert.Equal((byte)0x14, reset[40 + 13]);
        // The sum over the pseudo-header and the segment folds to zero when the checksum holds.
        Assert.Equal(0, Checksum.Fold(Checksum.Sum(reset.AsSpan(40), Checksum.Sum(reset.AsSpan(8, 32)) + Flow.Tcp + 20u)));
    }

    [Fact]
    public void An_IPv6_piece_after_the_first_has_no_ports_and_says_so()
    {
        var first = Ipv6Syn(hopByHop: false, fragment: (0, 0xABCD));
        var later = Ipv6Syn(hopByHop: false, fragment: (185, 0xABCD));

        Assert.Equal(FrameKind.Flow, Frames.TryReadOutbound6(first, out _, out _, out var firstId));
        Assert.Equal(0xABCDu, firstId);
        Assert.Equal(FrameKind.Fragment, Frames.TryReadOutbound6(later, out var flow, out _, out var laterId));
        Assert.Equal(0xABCDu, laterId);
        Assert.Equal(IPAddress.Parse("2001:db8:1::7"), flow.Remote);
    }

    [Fact]
    public void ICMPv6_is_not_a_flow()
    {
        var frame = Ipv6Syn(hopByHop: false);
        frame[14 + 6] = 58;
        Assert.Equal(FrameKind.OtherIpv6, Frames.TryReadOutbound6(frame, out _, out _, out _));
    }

    // An Ethernet frame around an IPv6 TCP SYN, with a hop-by-hop header or a fragment header before it if asked.
    private static byte[] Ipv6Syn(bool hopByHop, (ushort Offset, uint Id)? fragment = null)
    {
        var extra = (hopByHop ? 8 : 0) + (fragment is null ? 0 : 8);
        var frame = new byte[14 + 40 + extra + 20];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x86DD);
        var ip = frame.AsSpan(14);
        ip[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(ip[4..], (ushort)(extra + 20));
        ip[7] = 64;
        IPAddress.Parse("2001:db8::20").GetAddressBytes().CopyTo(ip[8..]);
        IPAddress.Parse("2001:db8:1::7").GetAddressBytes().CopyTo(ip[24..]);
        var at = 40;
        ref var next = ref ip[6];
        if (hopByHop)
        {
            next = 0;
            next = ref ip[at];
            at += 8;
        }
        if (fragment is { } f)
        {
            next = 44;
            next = ref ip[at];
            BinaryPrimitives.WriteUInt16BigEndian(ip[(at + 2)..], (ushort)((f.Offset << 3) | 1));
            BinaryPrimitives.WriteUInt32BigEndian(ip[(at + 4)..], f.Id);
            at += 8;
        }
        next = Flow.Tcp;
        var tcp = ip[at..];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, 50001);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], 443);
        tcp[12] = 5 << 4;
        tcp[13] = 0x02;
        return frame;
    }

    [Fact]
    public void IPv6_owner_rows_index_as_their_ends()
    {
        var tcp = new byte[4 + OwnerTable.Tcp6RowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(tcp, 1);
        var row = tcp.AsSpan(4);
        IPAddress.Parse("2001:db8::20").GetAddressBytes().CopyTo(row);
        BinaryPrimitives.WriteUInt16BigEndian(row[20..], 50001);
        IPAddress.Parse("2001:db8:1::7").GetAddressBytes().CopyTo(row[24..]);
        BinaryPrimitives.WriteUInt16BigEndian(row[44..], 443);
        BinaryPrimitives.WriteInt32LittleEndian(row[52..], 4242);
        var udp = new byte[4 + OwnerTable.Udp6RowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(udp, 1);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(4 + 20), 3478);
        BinaryPrimitives.WriteInt32LittleEndian(udp.AsSpan(4 + 24), 4343);

        var tcpIndex = new Dictionary<(UInt128, ushort, UInt128, ushort), int>();
        var udpIndex = new Dictionary<(UInt128, ushort), int>();
        OwnerTable.IndexTcp6(tcp, tcpIndex);
        OwnerTable.IndexUdp6(udp, udpIndex);

        var local = BinaryPrimitives.ReadUInt128BigEndian(IPAddress.Parse("2001:db8::20").GetAddressBytes());
        var remote = BinaryPrimitives.ReadUInt128BigEndian(IPAddress.Parse("2001:db8:1::7").GetAddressBytes());
        Assert.Equal(4242, tcpIndex[(local, 50001, remote, 443)]);
        Assert.Equal(4343, udpIndex[(UInt128.Zero, 3478)]);
    }

    [Fact]
    public void A_checksum_carried_over_a_change_equals_one_computed_whole()
    {
        var packet = Udp(Home, 50000, Server, 53, 100);
        var before = packet.AsSpan(12, 4).ToArray().Concat(packet.AsSpan(20, 2).ToArray()).ToArray();
        var old = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(26));
        Tunnel.GetAddressBytes().CopyTo(packet, 12);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 61000);
        var after = packet.AsSpan(12, 4).ToArray().Concat(packet.AsSpan(20, 2).ToArray()).ToArray();

        var adjusted = Checksum.Adjust(old, before, after, udp: true);
        Checksum.WriteTransport(packet, 20);

        Assert.Equal(BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(26)), adjusted);
    }
}
