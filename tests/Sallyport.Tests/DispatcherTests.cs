using System.Buffers.Binary;
using Sallyport.Net;

namespace Sallyport.Tests;

/// <summary>Which side of a shared channel a packet from the tunnel is for (docs/design/window.md, step 3).</summary>
public class DispatcherTests
{
    private const uint Here = 0x0A080006;      // 10.8.0.6, the tunnel's address
    private const uint Server = 0xCB007107;    // 203.0.113.7

    private static byte[] Ip(byte protocol, uint from, uint to, int transportLength, ushort fragment = 0)
    {
        var ip = new byte[20 + transportLength];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(6), fragment);
        ip[8] = 64;
        ip[9] = protocol;
        BinaryPrimitives.WriteUInt32BigEndian(ip.AsSpan(12), from);
        BinaryPrimitives.WriteUInt32BigEndian(ip.AsSpan(16), to);
        return ip;
    }

    private static byte[] Ports(byte protocol, ushort from, ushort to, uint destination = Here, ushort fragment = 0)
    {
        var ip = Ip(protocol, Server, destination, protocol == 6 ? 20 : 8, fragment);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(20), from);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(22), to);
        return ip;
    }

    // An ICMP error quoting a packet this side sent: its IPv4 header and its first 8 bytes, the ports among them.
    private static byte[] IcmpError(byte type, byte quotedProtocol, ushort quotedSourcePort)
    {
        var ip = Ip(1, Server, Here, 8 + 28);
        ip[20] = type;
        var quote = ip.AsSpan(28);
        quote[0] = 0x45;
        quote[9] = quotedProtocol;
        BinaryPrimitives.WriteUInt32BigEndian(quote[12..], Here);
        BinaryPrimitives.WriteUInt32BigEndian(quote[16..], Server);
        BinaryPrimitives.WriteUInt16BigEndian(quote[20..], quotedSourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(quote[22..], 443);
        return ip;
    }

    [Theory]
    [InlineData(6, 40960, TunnelDispatcher.Destination.Proxy)]
    [InlineData(6, 49151, TunnelDispatcher.Destination.Proxy)]
    [InlineData(17, 45000, TunnelDispatcher.Destination.Proxy)]
    [InlineData(6, 49152, TunnelDispatcher.Destination.Router)]
    [InlineData(17, 50000, TunnelDispatcher.Destination.Router)]
    [InlineData(6, 40959, TunnelDispatcher.Destination.Router)]
    public void TCP_and_UDP_go_by_their_destination_port(byte protocol, int port, TunnelDispatcher.Destination expected) =>
        Assert.Equal(expected, TunnelDispatcher.Classify(Ports(protocol, 443, (ushort)port), Here));

    [Theory]
    [InlineData(3, 6, 41000, TunnelDispatcher.Destination.Proxy)]     // fragmentation needed, for a proxy connection
    [InlineData(11, 17, 41000, TunnelDispatcher.Destination.Proxy)]   // time exceeded, for the proxy's DNS
    [InlineData(3, 17, 52000, TunnelDispatcher.Destination.Router)]   // port unreachable, for a routed program
    public void An_ICMP_error_goes_by_the_source_port_it_quotes(byte type, byte protocol, int port, TunnelDispatcher.Destination expected) =>
        Assert.Equal(expected, TunnelDispatcher.Classify(IcmpError(type, protocol, (ushort)port), Here));

    [Fact]
    public void An_echo_reply_and_other_protocols_go_to_the_router() =>
        Assert.Equal(TunnelDispatcher.Destination.Router, TunnelDispatcher.Classify(Ip(1, Server, Here, 8), Here));

    [Fact]
    public void A_piece_after_the_first_goes_to_the_router_its_first_by_its_port()
    {
        // More fragments, offset 0: the first piece, ports and all.
        Assert.Equal(TunnelDispatcher.Destination.Proxy, TunnelDispatcher.Classify(Ports(17, 53, 41000, fragment: 0x2000), Here));
        // Offset 185 (1480 bytes): no ports in it, whatever its bytes say.
        Assert.Equal(TunnelDispatcher.Destination.Router, TunnelDispatcher.Classify(Ports(17, 53, 41000, fragment: 185), Here));
    }

    [Fact]
    public void What_is_not_addressed_to_the_tunnel_or_does_not_read_is_for_no_one()
    {
        Assert.Equal(TunnelDispatcher.Destination.NotForUs, TunnelDispatcher.Classify(Ports(6, 443, 41000, destination: 0x0A080007), Here));
        Assert.Equal(TunnelDispatcher.Destination.NotForUs, TunnelDispatcher.Classify(new byte[12], Here));
        Assert.Equal(TunnelDispatcher.Destination.NotForUs, TunnelDispatcher.Classify(Ip(6, Server, Here, 2), Here));
    }
}
