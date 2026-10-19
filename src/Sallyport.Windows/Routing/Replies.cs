using System.Buffers.Binary;

namespace Sallyport.Windows.Routing;

/// <summary>
/// Packets the router makes itself (docs/design/router.md §6): the answer a selected program gets where its packet
/// cannot go — a reset for a connection it may not open, "fragmentation needed" for a datagram too big for the tunnel
/// that may not be split — and the pieces of one that may.
/// </summary>
internal static class Replies
{
    private const byte Icmp = 1;

    /// <summary>A reset from the peer a SYN was sent to (IPv4): the program's connect fails at once, as against a
    /// closed port, rather than waiting out its retries — a program that has another address to try goes on to it.</summary>
    public static byte[] Reset(ReadOnlySpan<byte> syn)
    {
        var headerLength = (syn[0] & 0x0F) * 4;
        var reset = new byte[20 + 20];
        reset[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(reset.AsSpan(2), (ushort)reset.Length);
        reset[8] = 64;
        reset[9] = Flow.Tcp;
        syn.Slice(16, 4).CopyTo(reset.AsSpan(12));
        syn.Slice(12, 4).CopyTo(reset.AsSpan(16));
        ResetSegment(syn[headerLength..BinaryPrimitives.ReadUInt16BigEndian(syn[2..])], reset.AsSpan(20));
        Checksum.WriteIpv4Header(reset, 20);
        Checksum.WriteTransport(reset, 20);
        return reset;
    }

    /// <summary>As <see cref="Reset(ReadOnlySpan{byte})"/>, for an IPv6 SYN whose TCP header starts at
    /// <paramref name="transport"/>.</summary>
    public static byte[] Reset6(ReadOnlySpan<byte> syn, int transport)
    {
        var reset = new byte[40 + 20];
        reset[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(reset.AsSpan(4), 20);
        reset[6] = Flow.Tcp;
        reset[7] = 64;
        syn.Slice(24, 16).CopyTo(reset.AsSpan(8));
        syn.Slice(8, 16).CopyTo(reset.AsSpan(24));
        ResetSegment(syn[transport..(40 + BinaryPrimitives.ReadUInt16BigEndian(syn[4..]))], reset.AsSpan(40));
        Checksum.WriteTcp6(reset);
        return reset;
    }

    // RST+ACK, acknowledging the SYN (one sequence number) and whatever data it carried.
    private static void ResetSegment(ReadOnlySpan<byte> syn, Span<byte> reset)
    {
        BinaryPrimitives.WriteUInt16BigEndian(reset, BinaryPrimitives.ReadUInt16BigEndian(syn[2..]));
        BinaryPrimitives.WriteUInt16BigEndian(reset[2..], BinaryPrimitives.ReadUInt16BigEndian(syn));
        var data = syn.Length - (syn[12] >> 4) * 4;
        BinaryPrimitives.WriteUInt32BigEndian(reset[8..], BinaryPrimitives.ReadUInt32BigEndian(syn[4..]) + 1 + (uint)Math.Max(data, 0));
        reset[12] = 5 << 4;
        reset[13] = 0x14;
    }

    /// <summary>ICMP "fragmentation needed" (type 3, code 4) for an IPv4 packet sent with don't-fragment that is bigger
    /// than <paramref name="mtu"/>, from its destination, quoting its header and first 8 bytes as RFC 792 asks. Windows
    /// lowers its path MTU to that destination, and TCP resends in smaller segments (RFC 1191).</summary>
    public static byte[] FragmentationNeeded(ReadOnlySpan<byte> packet, int mtu)
    {
        var headerLength = (packet[0] & 0x0F) * 4;
        var quote = packet[..Math.Min(headerLength + 8, BinaryPrimitives.ReadUInt16BigEndian(packet[2..]))];
        var reply = new byte[20 + 8 + quote.Length];
        reply[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), (ushort)reply.Length);
        reply[8] = 64;
        reply[9] = Icmp;
        packet.Slice(16, 4).CopyTo(reply.AsSpan(12));
        packet.Slice(12, 4).CopyTo(reply.AsSpan(16));
        var icmp = reply.AsSpan(20);
        icmp[0] = 3;
        icmp[1] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(icmp[6..], (ushort)mtu);
        quote.CopyTo(icmp[8..]);
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], Checksum.Fold(Checksum.Sum(icmp)));
        Checksum.WriteIpv4Header(reply, 20);
        return reply;
    }

    /// <summary>
    /// An IPv4 packet (a whole datagram or itself a piece of one) split into pieces of at most <paramref name="mtu"/>
    /// bytes: each carries the header with its own length, its offset added to the original's, and more-fragments set
    /// on all but the last unless the original had it. Its 20-byte header only: options are not copied.
    /// </summary>
    public static List<byte[]> Split(ReadOnlySpan<byte> packet, int mtu)
    {
        const int Header = 20;
        var total = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var field = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        var offset = field & 0x1FFF;
        var more = (field & 0x2000) != 0;
        var payload = packet[Header..total];
        // Offsets count 8-byte units, so every piece but the last carries a multiple of 8.
        var room = (mtu - Header) & ~7;
        var pieces = new List<byte[]>((payload.Length + room - 1) / room);
        for (var at = 0; at < payload.Length; at += room)
        {
            var size = Math.Min(room, payload.Length - at);
            var last = at + size == payload.Length;
            var piece = new byte[Header + size];
            packet[..Header].CopyTo(piece);
            payload.Slice(at, size).CopyTo(piece.AsSpan(Header));
            BinaryPrimitives.WriteUInt16BigEndian(piece.AsSpan(2), (ushort)piece.Length);
            BinaryPrimitives.WriteUInt16BigEndian(piece.AsSpan(6), (ushort)((field & 0x4000) | (more || !last ? 0x2000 : 0) | (offset + at / 8)));
            Checksum.WriteIpv4Header(piece, Header);
            pieces.Add(piece);
        }
        return pieces;
    }
}
