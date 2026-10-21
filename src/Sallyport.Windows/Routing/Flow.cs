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

    /// <summary>For a log or a measurement's output: a public address masked (<see cref="Addresses.Masked"/>).</summary>
    public override string ToString() => $"{(Protocol == Tcp ? "TCP" : "UDP")} {Addresses.Masked(Local)}:{LocalPort} -> {Addresses.Masked(Remote)}:{RemotePort}";
}

/// <summary>Addresses as anything written down may show them (AGENTS.md rule 2): a public one with its last part masked
/// — <c>A.B.C.x</c>, or an IPv6 one's first three groups and <c>x</c> — and one of this side's own networks as it is,
/// which names nobody.</summary>
public static class Addresses
{
    public static string Masked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes switch
            {
                [10, ..] or [127, ..] or [0, ..] or [172, >= 16 and <= 31, ..] or [192, 168, ..] or [169, 254, ..] or [100, >= 64 and <= 127, ..]
                    or [>= 224, ..] => address.ToString(),
                _ => $"{bytes[0]}.{bytes[1]}.{bytes[2]}.x",
            };
        // Link-local (fe80::/10), unique local (fc00::/7), loopback and unspecified are this side's own.
        if (address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC || IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6None.Equals(address))
            return address.ToString();
        return $"{bytes[0] << 8 | bytes[1]:x}:{bytes[2] << 8 | bytes[3]:x}:{bytes[4] << 8 | bytes[5]:x}::x";
    }
}

/// <summary>An IPv6 TCP or UDP packet's five-tuple, from this machine's side; addresses read big-endian from their 16
/// bytes, as both the packet and Windows' owner tables (MIB_TCP6ROW_OWNER_PID) hold them.</summary>
public readonly record struct Flow6(byte Protocol, UInt128 LocalAddress, ushort LocalPort, UInt128 RemoteAddress, ushort RemotePort)
{
    public IPAddress Local => Address(LocalAddress);

    public IPAddress Remote => Address(RemoteAddress);

    public static IPAddress Address(UInt128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    public override string ToString() => $"{(Protocol == Flow.Tcp ? "TCP" : "UDP")} [{Addresses.Masked(Local)}]:{LocalPort} -> [{Addresses.Masked(Remote)}]:{RemotePort}";
}

/// <summary>What an Ethernet frame is, as far as attributing it goes.</summary>
public enum FrameKind { Flow, Ipv6, OtherIpv4, OtherIpv6, Fragment, NotIp, Malformed }

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
        // The whole transport header, which the router reads past the ports (TCP's flags at 13).
        if (ip.Length < headerLength + (protocol == Flow.Tcp ? 20 : 8)) return FrameKind.Malformed;

        var transport = ip[headerLength..];
        flow = new(protocol,
            BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]),
            BinaryPrimitives.ReadUInt16BigEndian(transport),
            BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]),
            BinaryPrimitives.ReadUInt16BigEndian(transport[2..]));
        return FrameKind.Flow;
    }

    /// <summary>
    /// Reads an IPv6 frame Windows is sending, past the extension headers that can precede TCP or UDP: its five-tuple
    /// (<see cref="FrameKind.Flow"/>, <paramref name="transport"/> where the TCP/UDP header starts within the IP packet),
    /// or a fragment after the first (<see cref="FrameKind.Fragment"/>, addresses only). <paramref name="fragmentId"/> is
    /// the Fragment header's identification when the packet is a piece of one.
    /// </summary>
    public static FrameKind TryReadOutbound6(ReadOnlySpan<byte> frame, out Flow6 flow, out int transport, out uint? fragmentId)
    {
        flow = default;
        transport = 0;
        fragmentId = null;
        var link = frame.Length >= 14 && BinaryPrimitives.ReadUInt16BigEndian(frame[12..]) == 0x8100 ? 18 : 14;
        if (frame.Length < link + 40) return FrameKind.Malformed;
        var ip = frame[link..];
        if (ip[0] >> 4 != 6) return FrameKind.Malformed;
        var local = BinaryPrimitives.ReadUInt128BigEndian(ip[8..]);
        var remote = BinaryPrimitives.ReadUInt128BigEndian(ip[24..]);
        var next = ip[6];
        var at = 40;
        var laterPiece = false;
        while (true)
        {
            if (next is Flow.Tcp or Flow.Udp) break;
            if (at + 8 > ip.Length) return FrameKind.Malformed;
            switch (next)
            {
                case 0 or 43 or 60:   // hop-by-hop, routing, destination options: length in 8-byte units, less the first
                    next = ip[at];
                    at += (ip[at + 1] + 1) * 8;
                    break;
                case 44:              // fragment: 8 bytes; offset in the high 13 bits of bytes 2-3
                    fragmentId = BinaryPrimitives.ReadUInt32BigEndian(ip[(at + 4)..]);
                    laterPiece = (BinaryPrimitives.ReadUInt16BigEndian(ip[(at + 2)..]) & 0xFFF8) != 0;
                    next = ip[at];
                    at += 8;
                    if (laterPiece)
                    {
                        flow = new(next, local, 0, remote, 0);
                        return FrameKind.Fragment;
                    }
                    break;
                case 51:              // authentication: length in 4-byte units, less two
                    next = ip[at];
                    at += (ip[at + 1] + 2) * 4;
                    break;
                default:              // ICMPv6 (Windows' own neighbour discovery among it), ESP, no next header
                    flow = new(next, local, 0, remote, 0);
                    return FrameKind.OtherIpv6;
            }
        }
        if (at + 4 > ip.Length) return FrameKind.Malformed;
        transport = at;
        flow = new(next, local, BinaryPrimitives.ReadUInt16BigEndian(ip[at..]), remote, BinaryPrimitives.ReadUInt16BigEndian(ip[(at + 2)..]));
        return FrameKind.Flow;
    }
}
