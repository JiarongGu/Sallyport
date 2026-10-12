using System.Buffers.Binary;
using System.Net;

namespace Sallyport.Net;

[Flags]
internal enum TcpFlags : byte { Fin = 1, Syn = 2, Rst = 4, Psh = 8, Ack = 16 }

/// <summary>A received TCP segment: what the connection acts on, with the options this client reads.</summary>
internal readonly record struct TcpSegment(
    ushort SourcePort, ushort DestinationPort, uint Seq, uint Ack, TcpFlags Flags, ushort Window,
    ushort? Mss, byte? WindowScale, ArraySegment<byte> Payload, bool SackPermitted = false)
{
    public bool Has(TcpFlags flag) => (Flags & flag) != 0;
}

/// <summary>TCP segments inside IPv4 packets (RFC 9293): built for what this client sends, checked and read for what
/// comes back.</summary>
internal static class Tcp
{
    public const int HeaderLength = 20;

    /// <summary>The most SACK blocks one segment carries: 40 bytes of options hold four, with no timestamps.</summary>
    public const int MaxSackBlocks = 4;

    public static byte[] Build(IPAddress source, IPAddress destination, ushort ipId, ushort sourcePort, ushort destinationPort,
        uint seq, uint ack, TcpFlags flags, ushort window, ReadOnlySpan<byte> payload, ushort? mss = null, byte? windowScale = null,
        bool sackPermitted = false, ReadOnlySpan<(uint Left, uint Right)> sack = default)
    {
        // Each option is padded with NOPs to a 4-byte boundary: MSS (4), window scale (3 + 1), SACK-permitted (2 + 2),
        // SACK blocks (2 + 8 each, + 2).
        sack = sack[..Math.Min(sack.Length, MaxSackBlocks)];
        var options = (mss is null ? 0 : 4) + (windowScale is null ? 0 : 4) + (sackPermitted ? 4 : 0) + (sack.Length > 0 ? 4 + 8 * sack.Length : 0);
        var tcpLength = HeaderLength + options;
        var packet = new byte[Ipv4.HeaderLength + tcpLength + payload.Length];
        Ipv4.WriteHeader(packet, Ipv4.Tcp, source, destination, ipId);
        var tcp = packet.AsSpan(Ipv4.HeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], ack);
        tcp[12] = (byte)(tcpLength / 4 << 4);
        tcp[13] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], window);
        var at = HeaderLength;
        if (mss is { } m)
        {
            tcp[at] = 2; tcp[at + 1] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(tcp[(at + 2)..], m);
            at += 4;
        }
        if (windowScale is { } shift)
        {
            tcp[at] = 1; tcp[at + 1] = 3; tcp[at + 2] = 3; tcp[at + 3] = shift;
            at += 4;
        }
        if (sackPermitted)
        {
            tcp[at] = 1; tcp[at + 1] = 1; tcp[at + 2] = 4; tcp[at + 3] = 2;
            at += 4;
        }
        if (sack.Length > 0)
        {
            tcp[at] = 1; tcp[at + 1] = 1; tcp[at + 2] = 5; tcp[at + 3] = (byte)(2 + 8 * sack.Length);
            at += 4;
            foreach (var (left, right) in sack)
            {
                BinaryPrimitives.WriteUInt32BigEndian(tcp[at..], left);
                BinaryPrimitives.WriteUInt32BigEndian(tcp[(at + 4)..], right);
                at += 8;
            }
        }
        payload.CopyTo(tcp[at..]);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], Ipv4.TransportChecksum(packet, Ipv4.HeaderLength, Ipv4.Tcp));
        return packet;
    }

    /// <summary>The segment in <paramref name="packet"/>, or false when it is malformed or its checksum is wrong.</summary>
    public static bool TryRead(byte[] packet, in Ipv4Header header, out TcpSegment segment)
    {
        segment = default;
        var length = header.TotalLength - header.HeaderLength;
        if (header.Protocol != Ipv4.Tcp || length < HeaderLength) return false;
        if (Ipv4.TransportChecksum(packet.AsSpan(0, header.TotalLength), header.HeaderLength, Ipv4.Tcp) != 0) return false;
        var tcp = packet.AsSpan(header.HeaderLength, length);
        var dataOffset = (tcp[12] >> 4) * 4;
        if (dataOffset < HeaderLength || dataOffset > length) return false;
        ushort? mss = null;
        byte? scale = null;
        var sackPermitted = false;
        for (var at = HeaderLength; at < dataOffset;)
        {
            var kind = tcp[at];
            if (kind == 0) break;
            if (kind == 1) { at++; continue; }
            if (at + 1 >= dataOffset) break;
            var size = tcp[at + 1];
            if (size < 2 || at + size > dataOffset) break;
            if (kind == 2 && size == 4) mss = BinaryPrimitives.ReadUInt16BigEndian(tcp[(at + 2)..]);
            else if (kind == 3 && size == 3) scale = tcp[at + 2];
            else if (kind == 4 && size == 2) sackPermitted = true;
            at += size;
        }
        segment = new TcpSegment(
            BinaryPrimitives.ReadUInt16BigEndian(tcp), BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]), BinaryPrimitives.ReadUInt32BigEndian(tcp[8..]),
            (TcpFlags)(tcp[13] & 0x1f), BinaryPrimitives.ReadUInt16BigEndian(tcp[14..]), mss, scale,
            new ArraySegment<byte>(packet, header.HeaderLength + dataOffset, length - dataOffset), sackPermitted);
        return true;
    }
}

/// <summary>Sequence-number order, which wraps at 2^32: a is before b when b is less than half the space ahead of it.</summary>
internal static class Seq
{
    public static bool Lt(uint a, uint b) => (int)(a - b) < 0;
    public static bool Le(uint a, uint b) => (int)(a - b) <= 0;
    public static bool Gt(uint a, uint b) => (int)(a - b) > 0;
    public static bool Ge(uint a, uint b) => (int)(a - b) >= 0;
    public static bool InWindow(uint value, uint start, int size) => Ge(value, start) && Lt(value, start + (uint)size);
}

internal sealed class SeqComparer : IComparer<uint>
{
    public static readonly SeqComparer Instance = new();
    public int Compare(uint a, uint b) => ((int)(a - b)).CompareTo(0);
}

/// <summary>A bounded byte queue: TCP's send buffer (bytes leave when acknowledged) and receive buffer (bytes leave
/// when read). Its storage grows toward <c>capacity</c> only as bytes actually queue, so a window can be advertised in
/// full without every short connection paying for it up front.</summary>
internal sealed class ByteRing(int capacity, int initial = 64 * 1024)
{
    private byte[] _buffer = new byte[Math.Min(initial, capacity)];
    private int _head, _count;

    public int Count => _count;
    public int Capacity => capacity;
    public int Free => capacity - _count;

    public int Append(ReadOnlySpan<byte> data)
    {
        var n = Math.Min(data.Length, Free);
        if (_count + n > _buffer.Length) Grow(_count + n);
        var tail = (_head + _count) % _buffer.Length;
        var first = Math.Min(n, _buffer.Length - tail);
        data[..first].CopyTo(_buffer.AsSpan(tail));
        data[first..n].CopyTo(_buffer);
        _count += n;
        return n;
    }

    /// <summary>Copies <paramref name="into"/>.Length bytes starting <paramref name="offset"/> bytes from the front.</summary>
    public void CopyTo(int offset, Span<byte> into)
    {
        var start = (_head + offset) % _buffer.Length;
        var first = Math.Min(into.Length, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(into);
        _buffer.AsSpan(0, into.Length - first).CopyTo(into[first..]);
    }

    private void Grow(int needed)
    {
        var larger = new byte[Math.Min(capacity, Math.Max(needed, _buffer.Length * 2))];
        CopyTo(0, larger.AsSpan(0, _count));
        _buffer = larger;
        _head = 0;
    }

    public void Discard(int count)
    {
        _head = (_head + count) % _buffer.Length;
        _count -= count;
    }

    public int Read(Span<byte> into)
    {
        var n = Math.Min(into.Length, _count);
        CopyTo(0, into[..n]);
        Discard(n);
        return n;
    }
}
