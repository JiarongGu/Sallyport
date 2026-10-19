using System.Buffers.Binary;

namespace Sallyport.Windows.Routing;

/// <summary>The Internet checksum (RFC 1071) where the router rewrites a packet. Computed whole wherever the packet is
/// all here, rather than adjusted: a packet taken on the send path may carry a partial checksum Windows left for the
/// adapter to finish (offload). Adjusted only where it is not all here.</summary>
internal static class Checksum
{
    public static uint Sum(ReadOnlySpan<byte> data, uint sum = 0)
    {
        var i = 0;
        for (; i + 1 < data.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
        if (i < data.Length) sum += (uint)data[i] << 8;
        return sum;
    }

    public static ushort Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>The IPv4 header's checksum (bytes 10-11), over its <paramref name="headerLength"/> bytes.</summary>
    public static void WriteIpv4Header(Span<byte> ip, int headerLength)
    {
        ip[10] = ip[11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], Fold(Sum(ip[..headerLength])));
    }

    /// <summary>A TCP or UDP checksum over the IPv4 pseudo-header (addresses, protocol, length) and the segment. A UDP
    /// checksum of 0 means "none was computed" and stays so; one that computes to 0 is sent as all ones.</summary>
    public static void WriteTransport(Span<byte> ip, int headerLength)
    {
        var protocol = ip[9];
        var segment = ip[headerLength..BinaryPrimitives.ReadUInt16BigEndian(ip[2..])];
        var at = protocol == Flow.Tcp ? 16 : 6;
        if (protocol == Flow.Udp && segment[6] == 0 && segment[7] == 0) return;
        segment[at] = segment[at + 1] = 0;
        var sum = Sum(segment, Sum(ip.Slice(12, 8)) + protocol + (uint)segment.Length);
        var value = Fold(sum);
        BinaryPrimitives.WriteUInt16BigEndian(segment[at..], protocol == Flow.Udp && value == 0 ? (ushort)0xFFFF : value);
    }

    /// <summary>A checksum carried over a change of some of the 16-bit words it covers (RFC 1624, eqn. 3), for a packet
    /// not all here: a fragment's transport checksum covers the whole datagram, and an ICMP error quotes only the start
    /// of one. A UDP checksum of 0 stays 0.</summary>
    public static ushort Adjust(ushort checksum, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, bool udp)
    {
        if (udp && checksum == 0) return 0;
        uint sum = (ushort)~checksum;
        for (var i = 0; i + 1 < before.Length; i += 2)
            sum += (ushort)~BinaryPrimitives.ReadUInt16BigEndian(before[i..]) + (uint)BinaryPrimitives.ReadUInt16BigEndian(after[i..]);
        var value = Fold(sum);
        return udp && value == 0 ? (ushort)0xFFFF : value;
    }

    /// <summary>A TCP checksum over the IPv6 pseudo-header (addresses, upper-layer length, next header) for a packet
    /// with no extension headers — one the router builds.</summary>
    public static void WriteTcp6(Span<byte> ip)
    {
        var segment = ip[40..(40 + BinaryPrimitives.ReadUInt16BigEndian(ip[4..]))];
        segment[16] = segment[17] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(segment[16..], Fold(Sum(segment, Sum(ip.Slice(8, 32)) + Flow.Tcp + (uint)segment.Length)));
    }

    /// <summary>Whether a whole IPv4 packet's header and transport checksums verify — for tests and counters.</summary>
    public static bool Verifies(ReadOnlySpan<byte> ip)
    {
        var headerLength = (ip[0] & 0x0F) * 4;
        if (Fold(Sum(ip[..headerLength])) != 0) return false;
        var segment = ip[headerLength..BinaryPrimitives.ReadUInt16BigEndian(ip[2..])];
        if (ip[9] == Flow.Udp && segment[6] == 0 && segment[7] == 0) return true;
        return Fold(Sum(segment, Sum(ip.Slice(12, 8)) + ip[9] + (uint)segment.Length)) == 0;
    }
}
