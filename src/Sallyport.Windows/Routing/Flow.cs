using System.Buffers.Binary;
using System.Net;

namespace Sallyport.Windows.Routing;

/// <summary>
/// An IPv4 TCP or UDP packet's five-tuple, from this machine's side. Addresses are kept as their four bytes in network
/// order read into a uint, as Windows' owner tables (MIB_TCPROW_OWNER_PID, MIB_UDPROW_OWNER_PID) hold them, so a
/// packet and a table row compare without conversion; ports are numbers.
/// </summary>
public readonly record struct Flow(byte Protocol, uint LocalAddress, ushort LocalPort, uint RemoteAddress, ushort RemotePort)
{
    public const byte Tcp = 6, Udp = 17;

    public IPAddress Local => new(LocalAddress);

    public IPAddress Remote => new(RemoteAddress);

    public override string ToString() => $"{(Protocol == Tcp ? "TCP" : "UDP")} {Local}:{LocalPort} -> {Remote}:{RemotePort}";
}

/// <summary>What an Ethernet frame is, as far as attributing it goes.</summary>
public enum FrameKind { Flow, Ipv6, OtherIpv4, Fragment, NotIp, Malformed }

public static class Frames
{
    /// <summary>
    /// Reads a frame Windows is sending: its five-tuple when it is IPv4 TCP or UDP with its ports in this fragment.
    /// A fragment after the first carries no ports, so it cannot be attributed on its own.
    /// </summary>
    public static FrameKind TryReadOutbound(ReadOnlySpan<byte> frame, out Flow flow)
    {
        flow = default;
        if (frame.Length < 14) return FrameKind.Malformed;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
        var offset = 14;
        if (etherType == 0x8100)
        {
            // An 802.1Q tag: four bytes, then the real EtherType.
            if (frame.Length < 18) return FrameKind.Malformed;
            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[16..]);
            offset = 18;
        }
        if (etherType == 0x86DD) return FrameKind.Ipv6;
        if (etherType != 0x0800) return FrameKind.NotIp;

        var ip = frame[offset..];
        if (ip.Length < 20 || ip[0] >> 4 != 4) return FrameKind.Malformed;
        var headerLength = (ip[0] & 0x0F) * 4;
        if (headerLength < 20 || ip.Length < headerLength) return FrameKind.Malformed;
        var protocol = ip[9];
        if (protocol is not (Flow.Tcp or Flow.Udp)) return FrameKind.OtherIpv4;
        // Fragment offset: the low 13 bits of bytes 6-7, in 8-byte units.
        if ((BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x1FFF) != 0) return FrameKind.Fragment;
        if (ip.Length < headerLength + 4) return FrameKind.Malformed;

        var transport = ip[headerLength..];
        flow = new(protocol,
            BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]),
            BinaryPrimitives.ReadUInt16BigEndian(transport),
            BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]),
            BinaryPrimitives.ReadUInt16BigEndian(transport[2..]));
        return FrameKind.Flow;
    }
}
