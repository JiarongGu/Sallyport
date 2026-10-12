using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Sallyport.Net;

/// <summary>A received IPv4 packet's header, as far as this client needs it.</summary>
internal readonly record struct Ipv4Header(byte Protocol, IPAddress Source, IPAddress Destination, int HeaderLength, int TotalLength);

/// <summary>IPv4 packets as the tunnel carries them: built for what this client sends, read for what comes back.</summary>
internal static class Ipv4
{
    public const int HeaderLength = 20;
    public const byte Tcp = 6, Udp = 17;

    public static byte[] UdpPacket(IPAddress source, ushort sourcePort, IPAddress destination, ushort destinationPort, ReadOnlySpan<byte> payload, ushort id)
    {
        var packet = new byte[HeaderLength + 8 + payload.Length];
        WriteHeader(packet, Udp, source, destination, id);
        var udp = packet.AsSpan(HeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(udp, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)(8 + payload.Length));
        payload.CopyTo(udp[8..]);
        var sum = TransportChecksum(packet, HeaderLength, Udp);
        // In UDP a zero checksum means "none", so a sum that computes to zero is sent as all ones.
        BinaryPrimitives.WriteUInt16BigEndian(udp[6..], sum == 0 ? (ushort)0xffff : sum);
        return packet;
    }

    /// <summary>Writes a 20-byte header over the start of <paramref name="packet"/>, whose length is the total length.</summary>
    public static void WriteHeader(Span<byte> packet, byte protocol, IPAddress source, IPAddress destination, ushort id)
    {
        packet[0] = 0x45;
        packet[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet[4..], id);
        // Don't-fragment: the tunnel's own MTU is the only fragmentation that should happen, and it is the server's.
        packet[6] = 0x40;
        packet[7] = 0;
        packet[8] = 64;
        packet[9] = protocol;
        packet[10] = packet[11] = 0;
        source.TryWriteBytes(packet[12..16], out _);
        destination.TryWriteBytes(packet[16..20], out _);
        BinaryPrimitives.WriteUInt16BigEndian(packet[10..], Checksum(packet[..HeaderLength]));
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, out Ipv4Header header)
    {
        header = default;
        if (packet.Length < HeaderLength || packet[0] >> 4 != 4) return false;
        var headerLength = (packet[0] & 0x0f) * 4;
        var total = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (headerLength < HeaderLength || total < headerLength || total > packet.Length) return false;
        // A fragment cannot be read on its own; this client never asks for any (DF), so one arriving is dropped.
        if ((BinaryPrimitives.ReadUInt16BigEndian(packet[6..]) & 0x3fff) != 0) return false;
        header = new Ipv4Header(packet[9], new IPAddress(packet[12..16]), new IPAddress(packet[16..20]), headerLength, total);
        return true;
    }

    /// <summary>The UDP datagram in <paramref name="packet"/>, or false when it is not one, or its checksum is wrong.</summary>
    public static bool TryReadUdp(byte[] packet, out Ipv4Header header, out ushort sourcePort, out ushort destinationPort, out ArraySegment<byte> payload)
    {
        sourcePort = destinationPort = 0;
        payload = default;
        if (!TryRead(packet, out header) || header.Protocol != Udp || header.TotalLength < header.HeaderLength + 8) return false;
        var udp = packet.AsSpan(header.HeaderLength, header.TotalLength - header.HeaderLength);
        var length = BinaryPrimitives.ReadUInt16BigEndian(udp[4..]);
        if (length < 8 || length > udp.Length) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(udp[6..]) != 0 && TransportChecksum(packet.AsSpan(0, header.HeaderLength + length), header.HeaderLength, Udp) != 0)
            return false;
        sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
        destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp[2..]);
        payload = new ArraySegment<byte>(packet, header.HeaderLength + 8, length - 8);
        return true;
    }

    /// <summary>The ones'-complement checksum over the pseudo-header (both addresses, the protocol, the segment's length)
    /// and the segment — the same for UDP and TCP. Over a segment that carries its checksum, a correct one sums to 0.</summary>
    public static ushort TransportChecksum(ReadOnlySpan<byte> packet, int headerLength, byte protocol)
    {
        var segment = packet[headerLength..];
        var sum = Sum(packet[12..20]) + protocol + (uint)segment.Length + Sum(segment);
        return Fold(sum);
    }

    public static ushort Checksum(ReadOnlySpan<byte> data) => Fold(Sum(data));

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        return sum;
    }

    private static ushort Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    public static bool IsIpv4(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork;
}
