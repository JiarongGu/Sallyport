using System.Buffers.Binary;
using System.Net;

namespace Sallyport.Observer;

public sealed record EtwSchema(string Protocol, bool Receive, int AddressBytes, string AddressProperty, string PortProperty)
{
    // These IDs come from this machine's manifest provider, not the classic kernel logger's type numbers. Which property
    // holds the REMOTE end is measured, not read from the manifest's messages, which say the opposite for TCP: every TCP
    // event — sent (10), received (11), connection attempted (12) — names the remote daddr and this machine saddr (curl to
    // 34.117.59.81:443, 2026-10-19), while a UDP receive (43) names the remote saddr (nslookup's reply from 216.239.32.10).
    // The IPv6 events are taken to follow their IPv4 twins; IPv6 is not routed yet, so they are recorded, not relied on.
    public static EtwSchema? Find(ushort id) => id switch
    {
        10 => new("TCP", false, 4, "daddr", "dport"),
        11 => new("TCP", true, 4, "daddr", "dport"),
        // A connection attempt names its remote before any data: a connect that is never answered is seen only here.
        12 => new("TCP", false, 4, "daddr", "dport"),
        26 => new("TCP", false, 16, "daddr", "dport"),
        27 => new("TCP", true, 16, "daddr", "dport"),
        28 => new("TCP", false, 16, "daddr", "dport"),
        42 => new("UDP", false, 4, "daddr", "dport"),
        43 => new("UDP", true, 4, "saddr", "sport"),
        58 => new("UDP", false, 16, "daddr", "dport"),
        59 => new("UDP", true, 16, "saddr", "sport"),
        _ => null
    };

    public NetworkObservation Decode(ReadOnlySpan<byte> pid, ReadOnlySpan<byte> address, ReadOnlySpan<byte> port, long timestamp)
    {
        // PID is a four-byte ULONG; addresses and the two-byte port retain network byte order.
        if (pid.Length != 4 || address.Length != AddressBytes || port.Length != 2)
            throw new InvalidDataException("Unsupported network property size.");
        return new(Protocol, new IPAddress(address), BinaryPrimitives.ReadUInt16BigEndian(port),
            BinaryPrimitives.ReadUInt32LittleEndian(pid), timestamp, "etw");
    }
}
