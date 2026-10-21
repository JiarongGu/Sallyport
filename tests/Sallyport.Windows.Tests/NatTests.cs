using System.Buffers.Binary;
using System.Net;
using Sallyport.Windows.Routing;

namespace Sallyport.Windows.Tests;

public sealed class NatTests
{
    private static readonly IPAddress Home = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress OtherHome = IPAddress.Parse("192.168.1.21");
    private static readonly IPAddress Tunnel = IPAddress.Parse("10.8.0.6");
    private static readonly IPAddress Server = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Peer = IPAddress.Parse("198.51.100.9");

    private static readonly byte[] Link =
        [0x02, 0xAA, 0xAA, 0xAA, 0xAA, 0x01, 0x02, 0xBB, 0xBB, 0xBB, 0xBB, 0x02, 0x08, 0x00];   // to the gateway, from here

    private static uint Address(IPAddress a) => BinaryPrimitives.ReadUInt32LittleEndian(a.GetAddressBytes());

    private static byte[] Udp(IPAddress from, ushort fromPort, IPAddress to, ushort toPort, bool checksum = true)
    {
        var ip = new byte[20 + 8 + 5];
        Header(ip, Flow.Udp, from, to);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(20), fromPort);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(22), toPort);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(24), 8 + 5);
        "hello"u8.CopyTo(ip.AsSpan(28));
        Checksum.WriteIpv4Header(ip, 20);
        if (checksum)
        {
            ip[26] = 0xFF;   // anything but 0, so a checksum is computed
            Checksum.WriteTransport(ip, 20);
        }
        return ip;
    }

    // A TCP segment with one option: MSS, as a SYN carries it.
    private static byte[] Tcp(IPAddress from, ushort fromPort, IPAddress to, ushort toPort, byte flags, ushort mss)
    {
        var ip = new byte[20 + 24];
        Header(ip, Flow.Tcp, from, to);
        var tcp = ip.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, fromPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], toPort);
        tcp[12] = 6 << 4;
        tcp[13] = flags;
        tcp[20] = 2;
        tcp[21] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[22..], mss);
        Checksum.WriteIpv4Header(ip, 20);
        Checksum.WriteTransport(ip, 20);
        return ip;
    }

    private static void Header(Span<byte> ip, byte protocol, IPAddress from, IPAddress to)
    {
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ip.Length);
        ip[6] = 0x40;   // don't fragment
        ip[8] = 64;
        ip[9] = protocol;
        from.GetAddressBytes().CopyTo(ip[12..]);
        to.GetAddressBytes().CopyTo(ip[16..]);
    }

    private static (IPAddress, ushort, IPAddress, ushort) Ends(byte[] ip) =>
        (new IPAddress(ip.AsSpan(12, 4)), BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(20)),
         new IPAddress(ip.AsSpan(16, 4)), BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(22)));

    [Fact]
    public void A_UDP_datagram_leaves_from_the_tunnel_and_its_reply_comes_back_home()
    {
        var nat = new Nat(Address(Tunnel));
        var outbound = Udp(Home, 50000, Server, 3478);

        var mapping = nat.Outbound(outbound, Link);

        Assert.NotNull(mapping);
        Assert.Equal((Tunnel, (ushort)50000, Server, (ushort)3478), Ends(outbound));
        Assert.True(Checksum.Verifies(outbound));

        var reply = Udp(Server, 3478, Tunnel, 50000);
        Assert.Equal(InboundRefusal.None, nat.Inbound(reply, out var back));
        Assert.Same(mapping, back);
        Assert.Equal((Server, (ushort)3478, Home, (ushort)50000), Ends(reply));
        Assert.True(Checksum.Verifies(reply));

        var frame = Nat.Frame(back!, reply);
        Assert.Equal(Link[6..12], frame[..6]);     // to this machine
        Assert.Equal(Link[..6], frame[6..12]);     // from the gateway
        Assert.Equal(reply, frame[14..]);
    }

    [Fact]
    public void One_local_endpoint_keeps_one_tunnel_port_for_every_peer()
    {
        var nat = new Nat(Address(Tunnel));
        var first = nat.Outbound(Udp(Home, 50000, Server, 3478), Link);
        var second = nat.Outbound(Udp(Home, 50000, Peer, 7000), Link);

        Assert.Same(first, second);
        Assert.Equal(InboundRefusal.None, nat.Inbound(Udp(Peer, 7000, Tunnel, 50000), out _));
        Assert.Equal(InboundRefusal.None, nat.Inbound(Udp(Server, 3478, Tunnel, 50000), out _));
    }

    [Theory]
    [InlineData(50000, true)]
    [InlineData(20000, true)]
    [InlineData(45000, false)]
    [InlineData(80, false)]
    public void A_programs_own_port_is_kept_unless_it_is_the_proxys_or_well_known(int port, bool kept)
    {
        // The proxy shares the tunnel on 40960-49151 (Sallyport.Net.TunnelPorts): a routed program never takes one of those.
        var mapping = new Nat(Address(Tunnel)).Outbound(Udp(Home, (ushort)port, Server, 3478), Link)!;

        Assert.Equal(kept, mapping.TunnelPort == port);
        Assert.False(Sallyport.Net.TunnelPorts.IsProxy(mapping.TunnelPort));
        Assert.True(mapping.TunnelPort >= 1024);
    }

    [Fact]
    public void A_port_already_taken_in_the_tunnel_is_replaced_by_a_dynamic_one()
    {
        var nat = new Nat(Address(Tunnel));
        var first = nat.Outbound(Udp(Home, 50000, Server, 3478), Link)!;
        var other = Udp(OtherHome, 50000, Server, 3478);
        var second = nat.Outbound(other, Link)!;

        Assert.Equal(50000, first.TunnelPort);
        Assert.NotEqual(50000, second.TunnelPort);
        Assert.InRange(second.TunnelPort, 49152, 65535);
        Assert.Equal(second.TunnelPort, Ends(other).Item2);
        Assert.True(Checksum.Verifies(other));
    }

    [Fact]
    public void Only_a_peer_the_mapping_has_sent_to_is_let_back_in()
    {
        var nat = new Nat(Address(Tunnel));
        nat.Outbound(Udp(Home, 50000, Server, 3478), Link);

        Assert.Equal(InboundRefusal.UnknownPeer, nat.Inbound(Udp(Peer, 3478, Tunnel, 50000), out _));
        Assert.Equal(InboundRefusal.NoMapping, nat.Inbound(Udp(Server, 3478, Tunnel, 50001), out _));
        Assert.Equal(InboundRefusal.NotToTunnel, nat.Inbound(Udp(Server, 3478, Home, 50000), out _));
        var later = Udp(Server, 3478, Tunnel, 50000);
        later[7] = 0xB9;   // a piece at offset 185 × 8, whose first never came
        Assert.Equal(InboundRefusal.PieceAhead, nat.Inbound(later, out _));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(17)]
    public void A_TCP_packet_too_short_for_its_header_is_refused_not_thrown_on(int segment)
    {
        // From the very peer the mapping sent to: once its ports matched, the rest of the header was read regardless.
        var nat = new Nat(Address(Tunnel));
        nat.Outbound(Tcp(Home, 50001, Server, 443, 0x02, 1360), Link);
        var shortTcp = new byte[20 + segment];
        Header(shortTcp, Flow.Tcp, Server, Tunnel);
        BinaryPrimitives.WriteUInt16BigEndian(shortTcp.AsSpan(20), 443);
        BinaryPrimitives.WriteUInt16BigEndian(shortTcp.AsSpan(22), 50001);
        Checksum.WriteIpv4Header(shortTcp, 20);

        Assert.Equal(InboundRefusal.Malformed, nat.Inbound(shortTcp, out var mapping));
        Assert.Null(mapping);
    }

    [Fact]
    public void A_TCP_header_whose_offset_runs_past_the_packet_is_refused()
    {
        var nat = new Nat(Address(Tunnel));
        nat.Outbound(Tcp(Home, 50001, Server, 443, 0x02, 1360), Link);
        var reply = Tcp(Server, 443, Tunnel, 50001, 0x12, 1360);
        reply[20 + 12] = 15 << 4;   // 60 bytes of header in a 24-byte segment
        Assert.Equal(InboundRefusal.Malformed, nat.Inbound(reply, out _));
    }

    [Fact]
    public void A_damaged_reply_is_refused_rather_than_given_a_checksum_that_holds()
    {
        var nat = new Nat(Address(Tunnel));
        nat.Outbound(Udp(Home, 50000, Server, 3478), Link);
        var reply = Udp(Server, 3478, Tunnel, 50000);
        reply[^1] ^= 0x20;   // one bit of the payload, past the server

        Assert.Equal(InboundRefusal.BadChecksum, nat.Inbound(reply, out _));
        Assert.Equal(InboundRefusal.None, nat.Inbound(Udp(Server, 3478, Tunnel, 50000, checksum: false), out _));
    }

    [Fact]
    public void A_last_piece_of_a_few_bytes_follows_its_first()
    {
        var nat = new Nat(Address(Tunnel));
        nat.Outbound(Udp(Home, 50000, Server, 3478), Link);
        // The first piece: more fragments, offset 0 (its checksum is the whole datagram's, carried over, not checked).
        var first = Udp(Server, 3478, Tunnel, 50000);
        first[6] = 0x20;
        BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(4), 0x4242);
        Checksum.WriteIpv4Header(first, 20);
        Assert.Equal(InboundRefusal.None, nat.Inbound(first, out var mapping));

        // The last: three bytes, at offset 2 × 8.
        var last = new byte[20 + 3];
        Header(last, Flow.Udp, Server, Tunnel);
        BinaryPrimitives.WriteUInt16BigEndian(last.AsSpan(4), 0x4242);
        BinaryPrimitives.WriteUInt16BigEndian(last.AsSpan(6), 2);
        Checksum.WriteIpv4Header(last, 20);
        Assert.Equal(InboundRefusal.None, nat.Inbound(last, out var followed));
        Assert.Same(mapping, followed);
        Assert.Equal(Home, new IPAddress(last.AsSpan(16, 4)));
    }

    [Fact]
    public void A_UDP_datagram_without_a_checksum_keeps_none()
    {
        var nat = new Nat(Address(Tunnel));
        var outbound = Udp(Home, 50000, Server, 3478, checksum: false);
        nat.Outbound(outbound, Link);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(outbound.AsSpan(26)));
        Assert.True(Checksum.Verifies(outbound));
    }

    [Theory]
    [InlineData(0x02, 1460, 1360)]   // SYN above the clamp: lowered
    [InlineData(0x12, 1460, 1360)]   // SYN-ACK: lowered too
    [InlineData(0x02, 1200, 1200)]   // already below: kept
    [InlineData(0x10, 1460, 1460)]   // not a SYN: the option is not an MSS to negotiate
    public void A_SYN_is_clamped_to_what_the_tunnel_carries(byte flags, ushort mss, ushort expected)
    {
        var nat = new Nat(Address(Tunnel));
        var syn = Tcp(Home, 50001, Server, 443, flags, mss);
        nat.Outbound(syn, Link);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt16BigEndian(syn.AsSpan(20 + 22)));
        Assert.True(Checksum.Verifies(syn));
    }

    [Fact]
    public void A_partial_checksum_left_for_offload_is_replaced_by_a_whole_one()
    {
        var nat = new Nat(Address(Tunnel));
        var outbound = Udp(Home, 50000, Server, 3478);
        BinaryPrimitives.WriteUInt16BigEndian(outbound.AsSpan(26), 0x1234);   // what offload leaves: the pseudo-header's sum
        outbound[10] = outbound[11] = 0;                                      // and no header checksum at all
        nat.Outbound(outbound, Link);
        Assert.True(Checksum.Verifies(outbound));
    }
}
