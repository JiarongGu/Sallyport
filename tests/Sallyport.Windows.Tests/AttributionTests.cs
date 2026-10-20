using System.Buffers.Binary;
using System.Net;
using Sallyport.Windows.Routing;

namespace Sallyport.Windows.Tests;

public sealed class AttributionTests
{
    private static readonly IPAddress Here = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress There = IPAddress.Parse("203.0.113.7");

    // An Ethernet frame around an IPv4 header and the first four bytes of a transport header (its ports).
    private static byte[] Frame(byte protocol, ushort sourcePort, ushort destinationPort, ushort fragment = 0, bool vlan = false)
    {
        var link = vlan ? 18 : 14;
        var frame = new byte[link + 20 + 8];
        if (vlan)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x8100);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), 0x0800);
        }
        else BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);
        var ip = frame.AsSpan(link);
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[6..], fragment);
        ip[9] = protocol;
        Here.GetAddressBytes().CopyTo(ip[12..]);
        There.GetAddressBytes().CopyTo(ip[16..]);
        BinaryPrimitives.WriteUInt16BigEndian(ip[20..], sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(ip[22..], destinationPort);
        return frame;
    }

    [Fact]
    public void A_UDP_frame_reads_as_its_five_tuple()
    {
        Assert.Equal(FrameKind.Flow, Frames.TryReadOutbound(Frame(17, 50000, 3478), out var flow));
        Assert.Equal(Flow.Udp, flow.Protocol);
        Assert.Equal(Here, flow.Local);
        Assert.Equal(50000, flow.LocalPort);
        Assert.Equal(There, flow.Remote);
        Assert.Equal(3478, flow.RemotePort);
    }

    [Fact]
    public void A_tagged_TCP_frame_reads_past_its_tag()
    {
        Assert.Equal(FrameKind.Flow, Frames.TryReadOutbound(Frame(6, 50001, 443, vlan: true), out var flow));
        Assert.Equal((Flow.Tcp, 50001, 443), (flow.Protocol, flow.LocalPort, flow.RemotePort));
    }

    [Fact]
    public void Only_a_first_fragment_carries_ports()
    {
        // More-fragments set, offset 0: the first fragment, ports and all.
        Assert.Equal(FrameKind.Flow, Frames.TryReadOutbound(Frame(17, 50000, 3478, fragment: 0x2000), out _));
        Assert.Equal(FrameKind.Fragment, Frames.TryReadOutbound(Frame(17, 50000, 3478, fragment: 0x00B9), out _));
    }

    [Fact]
    public void Frames_that_are_not_IPv4_TCP_or_UDP_say_what_they_are()
    {
        Assert.Equal(FrameKind.OtherIpv4, Frames.TryReadOutbound(Frame(1, 0, 0), out _));
        var ipv6 = Frame(17, 1, 2);
        BinaryPrimitives.WriteUInt16BigEndian(ipv6.AsSpan(12), 0x86DD);
        Assert.Equal(FrameKind.Ipv6, Frames.TryReadOutbound(ipv6, out _));
        var arp = Frame(17, 1, 2);
        BinaryPrimitives.WriteUInt16BigEndian(arp.AsSpan(12), 0x0806);
        Assert.Equal(FrameKind.NotIp, Frames.TryReadOutbound(arp, out _));
        Assert.Equal(FrameKind.Malformed, Frames.TryReadOutbound(Frame(17, 1, 2)[..30], out _));
    }

    private static uint Address(IPAddress address) => BinaryPrimitives.ReadUInt32LittleEndian(address.GetAddressBytes());

    // A MIB table: dwNumEntries, then rows of DWORDs; ports in network order in a DWORD's low two bytes.
    private static byte[] Table(int rowSize, params uint[][] rows)
    {
        var table = new byte[4 + rows.Length * rowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var r = 0; r < rows.Length; r++)
            for (var f = 0; f < rows[r].Length; f++)
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 + r * rowSize + f * 4), rows[r][f]);
        return table;
    }

    private static uint Port(ushort port) => (uint)(((port & 0xFF) << 8) | (port >> 8));

    [Fact]
    public void A_TCP_flow_matches_its_row_exactly_and_TIME_WAIT_belongs_to_no_one()
    {
        var index = new Dictionary<(uint, ushort, uint, ushort), int>();
        OwnerTable.IndexTcp(Table(OwnerTable.TcpRowSize,
            [5, Address(Here), Port(50001), Address(There), Port(443), 4242],
            [11, Address(Here), Port(50002), Address(There), Port(443), 0]), index);

        Assert.Equal(4242, index[(Address(Here), 50001, Address(There), 443)]);
        Assert.False(index.ContainsKey((Address(Here), 50002, Address(There), 443)));
    }

    [Fact]
    public void Each_process_counts_its_connections_and_UDP_sockets_but_not_a_listener_or_a_shared_port()
    {
        var counts = OwnerTable.CountByProcess(
            [(true, 4242), (true, 4242), (false, 4242), (true, 0)],
            [(true, 4242)],
            [4242, 77, -1]);

        Assert.Equal(new ProcessSockets(3, 1), counts[4242]);
        Assert.Equal(new ProcessSockets(0, 1), counts[77]);
        // A TIME_WAIT row (PID 0) and a port two processes share (-1) are no one's.
        Assert.Equal(2, counts.Count);
    }

    [Fact]
    public void A_UDP_port_two_processes_share_is_marked_shared()
    {
        var index = new Dictionary<(uint, ushort), int>();
        OwnerTable.IndexUdp(Table(OwnerTable.UdpRowSize,
            [Address(Here), Port(50000), 100],
            [0, Port(50001), 200],
            [0, Port(50002), 300],
            [0, Port(50002), 301]), index);

        Assert.Equal(100, index[(Address(Here), 50000)]);
        Assert.Equal(200, index[(0u, 50001)]);
        Assert.Equal(-1, index[(0u, 50002)]);
    }

    [Fact]
    public void A_row_count_beyond_the_table_reads_only_the_table()
    {
        var table = Table(OwnerTable.UdpRowSize, [Address(Here), Port(50000), 100]);
        BinaryPrimitives.WriteUInt32LittleEndian(table, 9999);
        var index = new Dictionary<(uint, ushort), int>();
        OwnerTable.IndexUdp(table, index);
        Assert.Single(index);
    }

    [Fact]
    public void A_processes_round_trip_is_the_middle_of_its_latest_handshakes()
    {
        var routed = new ProcessRouted();
        Assert.Null(routed.Latency);

        foreach (var ms in new[] { 40.0, 300, 42, 41, 39 }) routed.Handshake(ms);

        Assert.Equal(41, routed.Latency);
    }

    [Fact]
    public void A_process_on_UDP_alone_takes_the_shortest_of_its_waits_for_a_reply()
    {
        var routed = new ProcessRouted();

        foreach (var ms in new[] { 80.0, 46, 120, 55 }) routed.Reply(ms);
        Assert.Equal(46, routed.Latency);

        // A handshake, once there is one, says the round trip better than a wait that holds the server's own time.
        routed.Handshake(44);
        Assert.Equal(44, routed.Latency);
    }

    [Fact]
    public void Only_a_processes_latest_round_trips_are_kept()
    {
        var routed = new ProcessRouted();
        for (var i = 0; i < 32; i++) routed.Handshake(500);
        for (var i = 0; i < 32; i++) routed.Handshake(30);

        Assert.Equal(30, routed.Latency);
    }
}
