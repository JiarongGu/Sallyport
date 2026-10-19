using System.Buffers.Binary;

namespace Sallyport.Windows.Routing;

/// <summary>The Internet checksum (RFC 1071) where the router rewrites a packet. Computed whole rather than adjusted:
/// a packet taken on the send path may carry a partial checksum Windows left for the adapter to finish (offload).</summary>
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
