using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Sallyport.Net;

namespace Sallyport.Tests;

public class SeqTests
{
    [Fact]
    public void OrderWrapsAtTheTopOfTheSpace()
    {
        // 0x10 is 32 bytes past 0xFFFFFFF0 once the counter wraps, so it is the later one.
        Assert.True(Seq.Lt(0xFFFFFFF0, 0x10));
        Assert.True(Seq.Le(0xFFFFFFF0, 0x10));
        Assert.True(Seq.Gt(0x10, 0xFFFFFFF0));
        Assert.True(Seq.Ge(0x10, 0xFFFFFFF0));
        Assert.False(Seq.Gt(0xFFFFFFF0, 0x10));
        Assert.False(Seq.Lt(0x10, 0xFFFFFFF0));
    }

    [Fact]
    public void EqualValuesAreLeAndGeButNeitherLtNorGt()
    {
        Assert.True(Seq.Le(5, 5));
        Assert.True(Seq.Ge(5, 5));
        Assert.False(Seq.Lt(5, 5));
        Assert.False(Seq.Gt(5, 5));
    }

    [Fact]
    public void AWindowThatStartsBeforeTheWrapContainsValuesAfterIt()
    {
        Assert.True(Seq.InWindow(0x05, 0xFFFFFFF0, 100));
        Assert.True(Seq.InWindow(0xFFFFFFF0, 0xFFFFFFF0, 100));
        Assert.False(Seq.InWindow(0xFFFFFFEF, 0xFFFFFFF0, 100));
        Assert.False(Seq.InWindow(unchecked(0xFFFFFFF0 + 100), 0xFFFFFFF0, 100));
    }
}

public class ByteRingTests
{
    private static byte[] Pattern(int count, int seed = 0) => Enumerable.Range(0, count).Select(i => (byte)(i * 7 + seed)).ToArray();

    private static byte[] Peek(ByteRing ring)
    {
        var all = new byte[ring.Count];
        ring.CopyTo(0, all);
        return all;
    }

    [Fact]
    public void ContentSurvivesWrappingPastTheEndOfTheStorage()
    {
        var ring = new ByteRing(capacity: 100, initial: 10);
        var first = Pattern(8);
        var second = Pattern(8, seed: 100);
        ring.Append(first);
        ring.Discard(6);
        // Head is at 6 of 10: these 8 bytes have to split across the end.
        Assert.Equal(8, ring.Append(second));

        var expected = first[6..].Concat(second).ToArray();
        Assert.Equal(expected, Peek(ring));
        // An offset read that itself starts before the wrap and ends after it.
        var middle = new byte[4];
        ring.CopyTo(1, middle);
        Assert.Equal(expected[1..5], middle);
    }

    [Fact]
    public void ReadReturnsWhatWasAppendedInOrderAcrossTheWrap()
    {
        var ring = new ByteRing(capacity: 100, initial: 10);
        ring.Append(Pattern(9));
        ring.Discard(9);
        var data = Pattern(10, seed: 3);
        ring.Append(data);

        var into = new byte[10];
        Assert.Equal(10, ring.Read(into));
        Assert.Equal(data, into);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void GrowthFromTheInitialSizeToTheCapacityKeepsContentAndOrder()
    {
        var ring = new ByteRing(capacity: 300_000);
        var all = Pattern(250_000, seed: 5);
        // Many small appends, so the 64 KB storage is outgrown more than once.
        for (var at = 0; at < all.Length; at += 5000) Assert.Equal(5000, ring.Append(all.AsSpan(at, 5000)));

        Assert.Equal(all.Length, ring.Count);
        Assert.Equal(all, Peek(ring));
    }

    [Fact]
    public void GrowthWhileWrappedKeepsContentAndOrder()
    {
        var ring = new ByteRing(capacity: 1000, initial: 16);
        var kept = new List<byte>();
        var a = Pattern(14);
        ring.Append(a);
        ring.Discard(10);
        kept.AddRange(a[10..]);
        // Head is at 10: this wraps, then needs more than 16 bytes in total, so the storage is replaced mid-wrap.
        var b = Pattern(40, seed: 9);
        ring.Append(b);
        kept.AddRange(b);

        Assert.Equal(kept.ToArray(), Peek(ring));
    }

    [Fact]
    public void FreeIsComputedAgainstTheCapacityNotTheCurrentStorage()
    {
        var ring = new ByteRing(capacity: 1000, initial: 10);
        Assert.Equal(1000, ring.Free);
        Assert.Equal(1000, ring.Capacity);

        ring.Append(Pattern(600));
        Assert.Equal(400, ring.Free);

        // Only what fits is taken: the caller sees the short count and keeps the rest.
        Assert.Equal(400, ring.Append(Pattern(500)));
        Assert.Equal(0, ring.Free);
        Assert.Equal(1000, ring.Count);

        ring.Discard(250);
        Assert.Equal(250, ring.Free);
    }

    [Fact]
    public void AnInitialSizeAboveTheCapacityIsClampedToIt()
    {
        var ring = new ByteRing(capacity: 20, initial: 64 * 1024);
        Assert.Equal(20, ring.Append(Pattern(30)));
        Assert.Equal(Pattern(20), Peek(ring));
    }
}

public class TcpWireTests
{
    private static readonly IPAddress Source = IPAddress.Parse("10.8.0.2"), Destination = IPAddress.Parse("93.184.215.14");

    private static bool Parse(byte[] packet, out TcpSegment segment)
    {
        segment = default;
        return Ipv4.TryRead(packet, out var header) && Tcp.TryRead(packet, header, out segment);
    }

    [Fact]
    public void ASynWithItsThreeOptionsRoundTrips()
    {
        var packet = Tcp.Build(Source, Destination, 77, 50000, 443, 0xFFFFFFF0, 0, TcpFlags.Syn, 65535, [], mss: 1360, windowScale: 6, sackPermitted: true);

        Assert.True(Ipv4.TryRead(packet, out var header));
        Assert.Equal(Source, header.Source);
        Assert.Equal(Destination, header.Destination);
        Assert.Equal(0, Ipv4.TransportChecksum(packet, header.HeaderLength, Ipv4.Tcp));
        Assert.Equal(0, Ipv4.Checksum(packet.AsSpan(0, header.HeaderLength)));
        Assert.True(Tcp.TryRead(packet, header, out var segment));
        Assert.Equal(50000, segment.SourcePort);
        Assert.Equal(443, segment.DestinationPort);
        Assert.Equal(0xFFFFFFF0, segment.Seq);
        Assert.Equal(TcpFlags.Syn, segment.Flags);
        Assert.Equal(65535, segment.Window);
        Assert.Equal((ushort)1360, segment.Mss);
        Assert.Equal((byte)6, segment.WindowScale);
        Assert.True(segment.SackPermitted);
        Assert.Equal(0, segment.Payload.Count);
    }

    [Fact]
    public void FourSackBlocksAndAPayloadRoundTrip()
    {
        (uint, uint)[] blocks = [(1000, 2000), (3000, 4000), (0xFFFFFF00, 0x00000100), (9000, 9001)];
        byte[] payload = [1, 2, 3, 4, 5];
        var packet = Tcp.Build(Source, Destination, 1, 50000, 443, 11, 22, TcpFlags.Ack | TcpFlags.Psh, 1234, payload, sack: blocks);

        Assert.True(Parse(packet, out var segment));
        Assert.Equal(11u, segment.Seq);
        Assert.Equal(22u, segment.Ack);
        Assert.Equal(TcpFlags.Ack | TcpFlags.Psh, segment.Flags);
        Assert.Equal(1234, segment.Window);
        Assert.Equal(payload, segment.Payload.ToArray());
        Assert.Equal(blocks, TcpRig.SackBlocksOf(packet));
        Assert.Null(segment.Mss);
        Assert.Null(segment.WindowScale);
        Assert.False(segment.SackPermitted);
    }

    [Fact]
    public void MoreThanFourSackBlocksAreTruncatedToFour()
    {
        (uint, uint)[] blocks = [(10, 20), (30, 40), (50, 60), (70, 80), (90, 100)];
        var packet = Tcp.Build(Source, Destination, 1, 50000, 443, 0, 0, TcpFlags.Ack, 100, [], sack: blocks);

        Assert.True(Parse(packet, out _));
        Assert.Equal(blocks[..4], TcpRig.SackBlocksOf(packet));
    }

    [Fact]
    public void AFlippedPayloadByteFailsTheChecksum()
    {
        var packet = Tcp.Build(Source, Destination, 1, 50000, 443, 0, 0, TcpFlags.Ack, 100, [10, 20, 30, 40], mss: 1360, sack: [(1, 2)]);
        Assert.True(Parse(packet, out _));

        packet[^1] ^= 0x01;

        Assert.True(Ipv4.TryRead(packet, out var header));
        Assert.False(Tcp.TryRead(packet, header, out _));
    }

    [Fact]
    public void AFlippedHeaderByteFailsTheChecksum()
    {
        var packet = Tcp.Build(Source, Destination, 1, 50000, 443, 5, 0, TcpFlags.Ack, 100, [1]);
        packet[Ipv4.HeaderLength + 7] ^= 0x80;

        Assert.True(Ipv4.TryRead(packet, out var header));
        Assert.False(Tcp.TryRead(packet, header, out _));
    }

    [Fact]
    public void AnOddLengthPayloadStillChecksums()
    {
        // The checksum pads an odd trailing byte with a zero; a three-byte payload exercises that.
        var packet = Tcp.Build(Source, Destination, 1, 50000, 443, 5, 0, TcpFlags.Ack, 100, [0xff, 0xfe, 0xfd]);
        Assert.True(Parse(packet, out var segment));
        Assert.Equal(new byte[] { 0xff, 0xfe, 0xfd }, segment.Payload.ToArray());
    }

    [Fact]
    public void ThreeSackBlocksWithAllThreeSynOptionsFillTheFortyOptionBytesExactly()
    {
        // 4 + 4 + 4 + (4 + 24) = 40: the largest option area a 4-bit data offset can describe.
        (uint, uint)[] blocks = [(1000, 2000), (3000, 4000), (5000, 6000)];
        var packet = Tcp.Build(Source, Destination, 1, 50000, 443, 1, 2, TcpFlags.Syn | TcpFlags.Ack, 100, [], mss: 1360, windowScale: 6, sackPermitted: true, sack: blocks);

        Assert.Equal(60, packet.Length - Ipv4.HeaderLength);
        Assert.True(Parse(packet, out var segment));
        Assert.Equal((ushort)1360, segment.Mss);
        Assert.Equal((byte)6, segment.WindowScale);
        Assert.True(segment.SackPermitted);
        Assert.Equal(blocks, TcpRig.SackBlocksOf(packet));
    }

    [Fact]
    public void OptionsPastFortyBytesAreRefusedRatherThanWrittenCorrupt()
    {
        // 4 + 4 + 4 + 36 = 48 option bytes: a 17-word header, which the 4-bit data offset would wrap to 1. No packet this
        // client sends carries all of them; Build refuses the combination instead of emitting a header that lies.
        (uint, uint)[] blocks = [(1000, 2000), (3000, 4000), (5000, 6000), (7000, 8000)];
        Assert.Throws<ArgumentException>(() =>
            Tcp.Build(Source, Destination, 1, 50000, 443, 1, 2, TcpFlags.Syn | TcpFlags.Ack, 100, [], mss: 1360, windowScale: 6, sackPermitted: true, sack: blocks));
    }
}

/// <summary>The packets a connection sent, each already proven to be valid IPv4 carrying valid TCP.</summary>
internal sealed record SentPacket(byte[] Raw, TcpSegment Seg)
{
    public bool IsPureAck => Seg.Flags == TcpFlags.Ack && Seg.Payload.Count == 0;
    public bool HasPayload => Seg.Payload.Count > 0;
    public byte[] Bytes => Seg.Payload.ToArray();
    public (uint Left, uint Right)[] Sack => TcpRig.SackBlocksOf(Raw);
}

internal sealed class FakeSink : IPacketSink
{
    private ushort _id;
    private readonly List<SentPacket> _pending = [];
    private readonly List<SentPacket> _all = [];
    // A writer's continuation sends from the pool while the test drains on its own thread: both under one lock, or a
    // packet sent between the copy and the clear would be lost to the test.
    private readonly Lock _gate = new();

    /// <summary>Everything ever sent, for counting; <see cref="Drain"/> hands out only what is new.</summary>
    public List<SentPacket> All { get { lock (_gate) return [.. _all]; } }

    public void Send(byte[] packet)
    {
        // A packet that does not parse, or whose checksums are wrong, fails the test that provoked it — whichever test.
        Assert.True(Ipv4.TryRead(packet, out var header), "the connection emitted a packet that is not valid IPv4");
        Assert.Equal(packet.Length, header.TotalLength);
        Assert.Equal(TcpRig.Local, header.Source);
        Assert.Equal(TcpRig.Remote, header.Destination);
        Assert.True(Tcp.TryRead(packet, header, out var segment), "the connection emitted a packet that is not valid TCP");
        var sent = new SentPacket(packet, segment);
        lock (_gate)
        {
            _all.Add(sent);
            _pending.Add(sent);
        }
    }

    public ushort NextIpId()
    {
        lock (_gate) return ++_id;
    }

    public List<SentPacket> Drain()
    {
        lock (_gate)
        {
            var taken = _pending.ToList();
            _pending.Clear();
            return taken;
        }
    }
}

/// <summary>A connection, a manual clock and a scripted peer: the peer is whatever the test feeds in.</summary>
internal sealed class TcpRig
{
    public static readonly IPAddress Local = IPAddress.Parse("10.8.0.2"), Remote = IPAddress.Parse("93.184.215.14");
    public const ushort LocalPort = 50000, RemotePort = 443;
    /// <summary>A handshake round trip of 200 ms gives srtt 200 and rttvar 100, so the RTO is 200 + 4 × 100 (RFC 6298).</summary>
    public const int RtoAfterHandshake = 600;
    public const int Capacity = TcpConnection.ReceiveCapacity;

    public long Now = 1_000_000;
    public readonly FakeSink Sink = new();
    public readonly TcpConnection Conn;
    public uint Iss, PeerIss;

    public TcpRig(uint peerIss = 7000)
    {
        PeerIss = peerIss;
        Conn = new TcpConnection(Sink, () => Now, Local, LocalPort, Remote, RemotePort);
    }

    /// <summary>The first byte we will send / the first byte we expect.</summary>
    public uint SndBase => Iss + 1;
    public uint RcvBase => PeerIss + 1;

    public static byte[] Bytes(int count, int seed = 0) => Enumerable.Range(0, count).Select(i => (byte)(i * 7 + seed)).ToArray();

    public SentPacket Open()
    {
        Conn.Open();
        var syn = Assert.Single(Sink.Drain());
        Iss = syn.Seg.Seq;
        return syn;
    }

    public TcpSegment Peer(uint seq, uint ack, TcpFlags flags, ushort window, byte[]? payload = null, ushort? mss = null, byte? scale = null, bool sack = false)
        => new(RemotePort, LocalPort, seq, ack, flags, window, mss, scale, new ArraySegment<byte>(payload ?? []), sack);

    public TcpSegment SynAck(ushort window, ushort? mss, byte? scale, bool sack)
        => Peer(PeerIss, Iss + 1, TcpFlags.Syn | TcpFlags.Ack, window, mss: mss, scale: scale, sack: sack);

    /// <summary>Opens and completes the handshake after <paramref name="rttMs"/>; returns what we sent in answer to the SYN-ACK.</summary>
    public List<SentPacket> Establish(ushort window = 65535, ushort? mss = 1360, byte? scale = null, bool sack = true, int rttMs = 200)
    {
        Open();
        Now += rttMs;
        Conn.OnSegment(SynAck(window, mss, scale, sack));
        Assert.True(Conn.Connected.IsCompletedSuccessfully);
        return Sink.Drain();
    }

    /// <summary>The peer's data. By default it acknowledges nothing new: <paramref name="ack"/> is the first byte we sent.</summary>
    public void Data(uint seq, byte[] data, uint? ack = null, ushort window = 65535, TcpFlags flags = TcpFlags.Ack)
        => Conn.OnSegment(Peer(seq, ack ?? SndBase, flags, window, data));

    public void Acknowledge(uint ack, ushort window = 65535)
        => Conn.OnSegment(Peer(RcvBase, ack, TcpFlags.Ack, window));

    public void Fin(uint? ack = null) => Conn.OnSegment(Peer(RcvBase, ack ?? SndBase, TcpFlags.Fin | TcpFlags.Ack, 65535));

    public void Advance(long ms) => AdvanceTo(Now + ms);

    public void AdvanceTo(long now)
    {
        Now = now;
        Conn.OnTick(Now);
    }

    public async Task Write(byte[] data)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Conn.WriteAsync(data, cts.Token);
    }

    public async Task<byte[]> Read(int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var got = new byte[count];
        var have = 0;
        while (have < count)
        {
            var n = await Conn.ReadAsync(got.AsMemory(have), cts.Token);
            Assert.True(n > 0, "the stream ended before the bytes the test expected");
            have += n;
        }
        return got;
    }

    public async Task<int> ReadOnce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await Conn.ReadAsync(new byte[64 * 1024], cts.Token);
    }

    public static void AssertFailedWith(IOException error, SocketError code)
    {
        var inner = Assert.IsType<SocketException>(error.InnerException);
        Assert.Equal(code, inner.SocketErrorCode);
    }

    /// <summary>The SACK blocks in a packet's options. <see cref="Tcp.TryRead"/> does not expose them, so the option bytes
    /// are walked here: kind 5, length 2 + 8n, then n pairs of big-endian (left, right).</summary>
    public static (uint Left, uint Right)[] SackBlocksOf(byte[] packet)
    {
        var tcp = packet.AsSpan(Ipv4.HeaderLength);
        var end = (tcp[12] >> 4) * 4;
        var blocks = new List<(uint, uint)>();
        for (var at = Tcp.HeaderLength; at < end;)
        {
            var kind = tcp[at];
            if (kind == 0) break;
            if (kind == 1) { at++; continue; }
            var size = tcp[at + 1];
            if (kind == 5)
            {
                Assert.Equal(0, (size - 2) % 8);
                for (var i = 0; i < (size - 2) / 8; i++)
                    blocks.Add((BinaryPrimitives.ReadUInt32BigEndian(tcp[(at + 2 + 8 * i)..]), BinaryPrimitives.ReadUInt32BigEndian(tcp[(at + 6 + 8 * i)..])));
            }
            at += size;
        }
        return [.. blocks];
    }

    /// <summary>The payloads of data segments laid out by sequence number, which must tile from <paramref name="start"/>
    /// with no gap or overlap: what a receiver would reassemble.</summary>
    public static byte[] Reassemble(IEnumerable<SentPacket> packets, uint start)
    {
        var bytes = new List<byte>();
        var next = start;
        foreach (var p in packets.Where(p => p.HasPayload))
        {
            Assert.Equal(next, p.Seg.Seq);
            bytes.AddRange(p.Bytes);
            next += (uint)p.Seg.Payload.Count;
        }
        return [.. bytes];
    }
}

public class TcpHandshakeTests
{
    [Fact]
    public void OpenSendsExactlyOneSynWithOurOptions()
    {
        var rig = new TcpRig();
        rig.Conn.Open();

        var syn = Assert.Single(rig.Sink.All).Seg;
        Assert.Equal(TcpFlags.Syn, syn.Flags);
        Assert.Equal((ushort)1360, syn.Mss);
        Assert.Equal((byte)6, syn.WindowScale);
        Assert.True(syn.SackPermitted);
        Assert.Equal(TcpRig.LocalPort, syn.SourcePort);
        Assert.Equal(TcpRig.RemotePort, syn.DestinationPort);
        Assert.Equal(0u, syn.Ack);
        Assert.Equal(0, syn.Payload.Count);
        // A SYN's own window is never scaled: the 4 MB we can take shows as the field's maximum.
        Assert.Equal(65535, syn.Window);
        Assert.False(rig.Conn.Connected.IsCompleted);
    }

    [Fact]
    public void OpenTwiceStillSendsOneSyn()
    {
        var rig = new TcpRig();
        rig.Conn.Open();
        rig.Conn.Open();
        Assert.Single(rig.Sink.All);
    }

    [Fact]
    public void ASynAckCompletesTheHandshakeAndIsAnsweredWithAnAck()
    {
        var rig = new TcpRig(peerIss: 123_456);
        var syn = rig.Open();
        rig.Now += 50;

        rig.Conn.OnSegment(rig.SynAck(window: 65535, mss: 1400, scale: 7, sack: true));

        Assert.True(rig.Conn.Connected.IsCompletedSuccessfully);
        var ack = Assert.Single(rig.Sink.Drain()).Seg;
        Assert.Equal(TcpFlags.Ack, ack.Flags);
        Assert.Equal(123_457u, ack.Ack);
        Assert.Equal(syn.Seg.Seq + 1, ack.Seq);
        Assert.Equal(0, ack.Payload.Count);
    }

    [Fact]
    public async Task APeerMssAboveOursIsClampedToOurs()
    {
        var rig = new TcpRig();
        rig.Establish(mss: 1400);

        await rig.Write(TcpRig.Bytes(3000));

        Assert.All(rig.Sink.Drain(), p => Assert.True(p.Seg.Payload.Count <= TcpConnection.OurMss));
    }

    [Fact]
    public async Task APeerWithoutAnMssOptionGetsTheDefaultOf536()
    {
        var rig = new TcpRig();
        rig.Establish(mss: null);

        await rig.Write(TcpRig.Bytes(2000));

        Assert.Equal([536, 536, 536, 392], rig.Sink.Drain().Select(p => p.Seg.Payload.Count));
    }

    [Fact]
    public async Task ARefusalFaultsConnectedWithConnectionRefused()
    {
        var rig = new TcpRig();
        rig.Open();

        rig.Conn.OnSegment(rig.Peer(0, rig.Iss + 1, TcpFlags.Rst | TcpFlags.Ack, 0));

        var error = await Assert.ThrowsAsync<SocketException>(() => rig.Conn.Connected);
        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
        // Nothing to reset: there is no connection on the other side.
        Assert.Empty(rig.Sink.Drain());
        Assert.True(rig.Conn.Expired(rig.Now));
    }

    [Fact]
    public void AResetThatDoesNotAcknowledgeOurSynIsIgnored()
    {
        var rig = new TcpRig();
        rig.Open();

        rig.Conn.OnSegment(rig.Peer(0, rig.Iss + 999, TcpFlags.Rst | TcpFlags.Ack, 0));
        rig.Conn.OnSegment(rig.Peer(0, 0, TcpFlags.Rst, 0));

        Assert.False(rig.Conn.Connected.IsCompleted);
        Assert.False(rig.Conn.Expired(rig.Now));
    }

    [Fact]
    public void ASynAckForAnotherSequenceNumberIsIgnored()
    {
        var rig = new TcpRig();
        rig.Open();

        rig.Conn.OnSegment(rig.Peer(rig.PeerIss, rig.Iss + 5, TcpFlags.Syn | TcpFlags.Ack, 65535, mss: 1360));

        Assert.False(rig.Conn.Connected.IsCompleted);
        Assert.Empty(rig.Sink.Drain());
    }

    [Fact]
    public async Task SynRetransmissionsDoubleFromOneSecondThenTheConnectionTimesOut()
    {
        var rig = new TcpRig();
        var syn = rig.Open();
        var start = rig.Now;

        // Due 1 s, then 2 s, 4 s and 8 s after the previous: 1, 3, 7 and 15 s from the open.
        long due = 1000, gap = 1000;
        for (var resend = 1; resend <= 4; resend++)
        {
            rig.AdvanceTo(start + due - 1);
            Assert.Empty(rig.Sink.Drain());

            rig.AdvanceTo(start + due);
            var resent = Assert.Single(rig.Sink.Drain()).Seg;
            Assert.Equal(TcpFlags.Syn, resent.Flags);
            Assert.Equal(syn.Seg.Seq, resent.Seq);
            Assert.Equal((ushort)1360, resent.Mss);
            Assert.Equal(resend, rig.Conn.Retransmits);
            gap *= 2;
            due += gap;
        }
        Assert.False(rig.Conn.Connected.IsCompleted);

        // The fifth silence is the end: the next expiry is 16 s on, and it gives up rather than sending a sixth.
        rig.AdvanceTo(start + due - 1);
        Assert.False(rig.Conn.Connected.IsCompleted);
        rig.AdvanceTo(start + due);

        var error = await Assert.ThrowsAsync<SocketException>(() => rig.Conn.Connected);
        Assert.Equal(SocketError.TimedOut, error.SocketErrorCode);
        Assert.Equal(5, rig.Sink.All.Count(p => p.Seg.Flags == TcpFlags.Syn));
        Assert.Equal(5, rig.Sink.All.Count);
        Assert.Equal(31_000, due);
    }

    [Fact]
    public async Task ASynAckAfterARetransmittedSynStillConnects()
    {
        var rig = new TcpRig();
        rig.Open();
        rig.AdvanceTo(rig.Now + 1000);
        Assert.Single(rig.Sink.Drain());

        rig.Conn.OnSegment(rig.SynAck(65535, 1360, null, true));

        await rig.Conn.Connected;
        Assert.Equal(TcpFlags.Ack, Assert.Single(rig.Sink.Drain()).Seg.Flags);
    }
}

public class TcpReceiveTests
{
    [Fact]
    public async Task ThreeInOrderSegmentsAreReadInOrderAndAcknowledgedByOneAck()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        byte[] a = TcpRig.Bytes(500), b = TcpRig.Bytes(700, 1), c = TcpRig.Bytes(300, 2);
        var acksBefore = rig.Conn.AcksSent;

        rig.Data(rig.RcvBase, a);
        rig.Data(rig.RcvBase + 500, b);
        rig.Data(rig.RcvBase + 1200, c);

        // Nothing is acknowledged until the tunnel's queue is drained.
        Assert.Empty(rig.Sink.Drain());
        Assert.Equal(1500, rig.Conn.BytesReceived);
        Assert.Equal(a.Concat(b).Concat(c), await rig.Read(1500));
        Assert.Empty(rig.Sink.Drain());

        rig.Conn.FlushAck();
        var ack = Assert.Single(rig.Sink.Drain());
        Assert.True(ack.IsPureAck);
        Assert.Equal(rig.RcvBase + 1500, ack.Seg.Ack);
        Assert.Equal(rig.SndBase, ack.Seg.Seq);
        Assert.Equal(acksBefore + 1, rig.Conn.AcksSent);

        // Owed once: a second flush has nothing to say.
        rig.Conn.FlushAck();
        Assert.Empty(rig.Sink.Drain());
        Assert.Equal(4, rig.Conn.SegmentsIn);
        Assert.Equal(0, rig.Conn.SegmentsOutOfOrder);
    }

    [Fact]
    public async Task ASegmentBeforeItsPredecessorIsAcknowledgedAtOnceWithASackBlock()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        byte[] a = TcpRig.Bytes(1000), b = TcpRig.Bytes(1000, 9);

        rig.Data(rig.RcvBase + 1000, b);

        var dup = Assert.Single(rig.Sink.Drain());
        Assert.True(dup.IsPureAck);
        Assert.Equal(rig.RcvBase, dup.Seg.Ack);
        Assert.Equal([(rig.RcvBase + 1000, rig.RcvBase + 2000)], dup.Sack);
        Assert.Equal(1, rig.Conn.SegmentsOutOfOrder);

        rig.Data(rig.RcvBase, a);
        Assert.Equal(a.Concat(b), await rig.Read(2000));
        rig.Conn.FlushAck();
        var ack = Assert.Single(rig.Sink.Drain());
        Assert.Equal(rig.RcvBase + 2000, ack.Seg.Ack);
        Assert.Empty(ack.Sack);
        Assert.Equal(1, rig.Conn.SegmentsOutOfOrder);
    }

    [Fact]
    public async Task OverlappingEarlySegmentsHoldNoMoreThanTheWindow()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        // Each starts one byte past the last, so each is new to the reassembly and almost all of it a repeat: 5,000 of
        // them would hold 6.5 MB if each were kept whole.
        for (var i = 1; i <= 5000; i++) rig.Data(rig.RcvBase + (uint)i, TcpRig.Bytes(1300, i));
        rig.Sink.Drain();
        Assert.InRange(rig.Conn.EarlyBytes, 1, TcpRig.Capacity);

        // The hole filled, what was held is read out whole and in order, and nothing is held after.
        rig.Data(rig.RcvBase, TcpRig.Bytes(1));
        Assert.Equal(TcpRig.Bytes(1).Concat(TcpRig.Bytes(1300, 1)), await rig.Read(1301));
        Assert.Equal(0, rig.Conn.EarlyBytes);
    }

    [Fact]
    public void WithTwoHolesTheFirstSackBlockHoldsTheLatestArrival()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        // Hole at [0,1000) and another at [2000,3000).
        rig.Data(rig.RcvBase + 3000, TcpRig.Bytes(1000));
        var afterFirst = Assert.Single(rig.Sink.Drain());
        rig.Data(rig.RcvBase + 1000, TcpRig.Bytes(1000));
        var afterSecond = Assert.Single(rig.Sink.Drain());

        Assert.Equal([(rig.RcvBase + 3000, rig.RcvBase + 4000)], afterFirst.Sack);
        // RFC 2018: the block with the segment that triggered this ACK goes first, the older one after it.
        Assert.Equal([(rig.RcvBase + 1000, rig.RcvBase + 2000), (rig.RcvBase + 3000, rig.RcvBase + 4000)], afterSecond.Sack);
        Assert.Equal(rig.RcvBase, afterSecond.Seg.Ack);

        // And when the higher range is the newer arrival it leads instead.
        rig.Data(rig.RcvBase + 5000, TcpRig.Bytes(500));
        var afterThird = Assert.Single(rig.Sink.Drain());
        Assert.Equal((rig.RcvBase + 5000, rig.RcvBase + 5500), afterThird.Sack[0]);
        Assert.Equal(3, afterThird.Sack.Length);
        Assert.Equal(3, rig.Conn.SegmentsOutOfOrder);
    }

    [Fact]
    public void AnEarlySegmentThatTouchesAnotherJoinsItsBlock()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        rig.Data(rig.RcvBase + 1000, TcpRig.Bytes(1000));
        rig.Data(rig.RcvBase + 3000, TcpRig.Bytes(1000));
        rig.Sink.Drain();

        rig.Data(rig.RcvBase + 2000, TcpRig.Bytes(1000));

        var ack = Assert.Single(rig.Sink.Drain());
        Assert.Equal([(rig.RcvBase + 1000, rig.RcvBase + 4000)], ack.Sack);
    }

    [Fact]
    public async Task AnEarlySegmentResentWithOtherBoundariesWidensItsBlockRatherThanAddingOne()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        var stream = TcpRig.Bytes(2500);
        rig.Data(rig.RcvBase + 1000, stream[1000..2000]);
        rig.Sink.Drain();

        // The peer resegmented: [1500, 2500) overlaps what is held.
        rig.Data(rig.RcvBase + 1500, stream[1500..]);

        Assert.Equal([(rig.RcvBase + 1000, rig.RcvBase + 2500)], Assert.Single(rig.Sink.Drain()).Sack);
        rig.Data(rig.RcvBase, stream[..1000]);
        Assert.Equal(stream, await rig.Read(2500));
    }

    [Fact]
    public void AnEarlySegmentSpanningSeveralHeldRangesJoinsThemAll()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        rig.Data(rig.RcvBase + 1000, TcpRig.Bytes(500));
        rig.Data(rig.RcvBase + 2000, TcpRig.Bytes(500));
        rig.Data(rig.RcvBase + 3000, TcpRig.Bytes(500));
        rig.Sink.Drain();

        rig.Data(rig.RcvBase + 1200, TcpRig.Bytes(2500));

        Assert.Equal([(rig.RcvBase + 1000, rig.RcvBase + 3700)], Assert.Single(rig.Sink.Drain()).Sack);
    }

    [Fact]
    public async Task FillingTheFirstOfTwoHolesLeavesOnlyTheRangePastTheSecondInTheSackBlocks()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        var stream = TcpRig.Bytes(4000);
        rig.Data(rig.RcvBase + 1000, stream[1000..2000]);
        rig.Data(rig.RcvBase + 3000, stream[3000..]);
        rig.Sink.Drain();

        rig.Data(rig.RcvBase, stream[..1000]);
        rig.Conn.FlushAck();

        var ack = Assert.Single(rig.Sink.Drain());
        Assert.Equal(rig.RcvBase + 2000, ack.Seg.Ack);
        Assert.Equal([(rig.RcvBase + 3000, rig.RcvBase + 4000)], ack.Sack);

        rig.Data(rig.RcvBase + 2000, stream[2000..3000]);
        rig.Conn.FlushAck();
        var last = Assert.Single(rig.Sink.Drain());
        Assert.Equal(rig.RcvBase + 4000, last.Seg.Ack);
        Assert.Empty(last.Sack);
        Assert.Equal(stream, await rig.Read(4000));
    }

    [Fact]
    public async Task AFinHeldPastAHoleEndsTheStreamOnceTheHoleFillsAndNothingIsSackedAfter()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        var stream = TcpRig.Bytes(2000);
        rig.Data(rig.RcvBase + 1000, stream[1000..], flags: TcpFlags.Ack | TcpFlags.Fin);
        Assert.Equal([(rig.RcvBase + 1000, rig.RcvBase + 2000)], Assert.Single(rig.Sink.Drain()).Sack);

        rig.Data(rig.RcvBase, stream[..1000]);
        rig.Conn.FlushAck();

        var ack = Assert.Single(rig.Sink.Drain());
        // The FIN takes a sequence number of its own.
        Assert.Equal(rig.RcvBase + 2001, ack.Seg.Ack);
        Assert.Empty(ack.Sack);
        Assert.Equal(stream, await rig.Read(2000));
        Assert.Equal(0, await rig.ReadOnce());
    }

    [Fact]
    public void NoSackBlocksAreSentWhenThePeerDidNotOfferSack()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7, sack: false);

        rig.Data(rig.RcvBase + 1000, TcpRig.Bytes(1000));

        var dup = Assert.Single(rig.Sink.Drain());
        Assert.Equal(rig.RcvBase, dup.Seg.Ack);
        Assert.Empty(dup.Sack);
    }

    [Fact]
    public async Task OutOfOrderReassemblyWorksAcrossTheSequenceWrap()
    {
        var rig = new TcpRig(peerIss: 0xFFFFFFF0);
        rig.Establish(scale: 7);
        byte[] a = TcpRig.Bytes(100), b = TcpRig.Bytes(100, 1), c = TcpRig.Bytes(100, 2);

        // RcvBase is 0xFFFFFFF1, so the segments at +100 and +200 sit past the wrap, and must sort after the one before it.
        rig.Data(rig.RcvBase + 200, c);
        rig.Data(rig.RcvBase + 100, b);
        var ack = Assert.Single(rig.Sink.Drain().Skip(1));
        Assert.Equal(unchecked(rig.RcvBase + 100), ack.Sack[0].Left);
        Assert.Equal(unchecked(rig.RcvBase + 300), ack.Sack[0].Right);
        Assert.Single(ack.Sack);

        rig.Data(rig.RcvBase, a);
        Assert.Equal(a.Concat(b).Concat(c), await rig.Read(300));
        rig.Conn.FlushAck();
        Assert.Equal(unchecked(rig.RcvBase + 300), Assert.Single(rig.Sink.Drain()).Seg.Ack);
    }

    [Fact]
    public void ARepeatedSegmentIsAcknowledgedAtOnce()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        rig.Data(rig.RcvBase, TcpRig.Bytes(1000));
        rig.Conn.FlushAck();
        rig.Sink.Drain();

        rig.Data(rig.RcvBase, TcpRig.Bytes(1000));

        var ack = Assert.Single(rig.Sink.Drain());
        Assert.True(ack.IsPureAck);
        Assert.Equal(rig.RcvBase + 1000, ack.Seg.Ack);
        Assert.Equal(1, rig.Conn.SegmentsRepeated);
        Assert.Equal(0, rig.Conn.SegmentsOutOfOrder);
        Assert.Equal(1000, rig.Conn.BytesReceived);
    }

    [Fact]
    public async Task ASegmentOverlappingAcknowledgedBytesKeepsOnlyItsNewTail()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        var stream = TcpRig.Bytes(1500);
        rig.Data(rig.RcvBase, stream[..1000]);

        rig.Data(rig.RcvBase + 500, stream[500..]);

        Assert.Equal(stream, await rig.Read(1500));
        Assert.Equal(0, rig.Conn.SegmentsRepeated);
        Assert.Equal(1500, rig.Conn.BytesReceived);
    }

    [Fact]
    public void ARepeatedEarlySegmentIsKeptOnceAndAnsweredEachTime()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        rig.Data(rig.RcvBase + 1000, TcpRig.Bytes(500));
        rig.Data(rig.RcvBase + 1000, TcpRig.Bytes(500));

        // The same early segment twice: kept once, and each arrival is still answered.
        Assert.Equal(2, rig.Conn.SegmentsOutOfOrder);
        Assert.Equal(2, rig.Sink.Drain().Count(p => p.IsPureAck));
    }

    [Fact]
    public void WithoutPeerWindowScalingTheAdvertisedWindowStaysInTheUnscaledField()
    {
        var plain = new TcpRig();
        plain.Establish(scale: null);
        plain.Data(plain.RcvBase, TcpRig.Bytes(100_000));
        plain.Conn.FlushAck();

        var scaled = new TcpRig();
        scaled.Establish(scale: 7);
        scaled.Data(scaled.RcvBase, TcpRig.Bytes(100_000));
        scaled.Conn.FlushAck();

        var flush = Assert.Single(plain.Sink.Drain());
        // Unscaled, the field cannot say more than 65535, however much room there is.
        Assert.Equal(65535, flush.Seg.Window);
        Assert.All(plain.Sink.All, p => Assert.True(p.Seg.Window <= 65535));
        // Scaled by our 6, it states the room that is actually left.
        Assert.Equal((TcpConnection.ReceiveCapacity - 100_000) >> 6, Assert.Single(scaled.Sink.Drain()).Seg.Window);
    }

    [Fact]
    public async Task AWindowUpdateIsSentOnceReadingHasFreedEnoughRoom()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        var big = TcpRig.Bytes(2 * 1024 * 1024);
        rig.Data(rig.RcvBase, big);
        rig.Conn.FlushAck();
        var full = Assert.Single(rig.Sink.Drain());
        Assert.Equal((TcpConnection.ReceiveCapacity - big.Length) >> 6, full.Seg.Window);

        // A small read is not worth a packet; draining more than a quarter of the buffer is.
        await rig.Read(1000);
        Assert.Empty(rig.Sink.Drain());
        await rig.Read(1024 * 1024);
        var update = Assert.Single(rig.Sink.Drain());
        Assert.True(update.IsPureAck);
        Assert.True(update.Seg.Window > full.Seg.Window);
    }

    [Fact]
    public async Task AnUnscaledWindowThatShrankIsAnnouncedWhenReadingReopensIt()
    {
        // The step is a quarter of what the FIELD can say. Measured as a quarter of the 4 MB buffer it would be 1 MB, which an
        // unscaled window (at most 65535) can never grow by — the reopened window would go unannounced and the peer would
        // have to probe its way out. This pins the fix for the over-announcing below without trading it for that.
        var rig = new TcpRig();
        rig.Establish(scale: null);
        var filled = TcpConnection.ReceiveCapacity - 10_000;
        rig.Data(rig.RcvBase, TcpRig.Bytes(filled));
        // The burst's ACK is what tells the peer the window shrank to 10,000; without it there is nothing to reopen.
        rig.Conn.FlushAck();
        Assert.Equal(10_000, rig.Sink.Drain().Single().Seg.Window);

        await rig.Read(filled);

        var sent = rig.Sink.Drain();
        var update = Assert.Single(sent);
        Assert.True(update.IsPureAck);
        Assert.Equal(ushort.MaxValue, update.Seg.Window);
    }

    [Fact]
    public async Task ReadingDoesNotSendAWindowUpdateWhenTheWindowFieldCannotGrow()
    {
        // Unscaled, the field already says 65535 while 4 MB is free: reading frees nothing it could say.
        var rig = new TcpRig();
        rig.Establish(scale: null);
        rig.Data(rig.RcvBase, TcpRig.Bytes(1000));
        rig.Sink.Drain();

        await rig.Read(1000);

        Assert.Empty(rig.Sink.Drain());
    }
}

public class TcpSendTests
{
    [Fact]
    public async Task SendingStaysWithinThePeerWindowAndEachAckReleasesMore()
    {
        // 2 × 1360, unscaled. The initial congestion window is 10 × 1360, so the peer's window is the binding one.
        var rig = new TcpRig();
        rig.Establish(window: 2720, mss: 1360, scale: null);
        var data = TcpRig.Bytes(10_000);

        await rig.Write(data);

        var all = new List<SentPacket>();
        var rounds = new List<int[]>();
        var round = rig.Sink.Drain();
        var acked = rig.SndBase;
        while (round.Count > 0)
        {
            Assert.All(round, p => Assert.Equal(TcpFlags.Ack | TcpFlags.Psh, p.Seg.Flags));
            rounds.Add(round.Select(p => p.Seg.Payload.Count).ToArray());
            all.AddRange(round);
            acked += (uint)round.Sum(p => p.Seg.Payload.Count);
            rig.Acknowledge(acked, window: 2720);
            round = rig.Sink.Drain();
        }

        Assert.Equal([[1360, 1360], [1360, 1360], [1360, 1360], [1360, 480]], rounds);
        Assert.Equal(rig.SndBase + 10_000, acked);
        Assert.Equal(data, TcpRig.Reassemble(all, rig.SndBase));
        Assert.Equal(0, rig.Conn.Retransmits);
    }

    [Fact]
    public async Task TheCongestionWindowStartsAtTenSegments()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        await rig.Write(TcpRig.Bytes(30_000));

        var burst = rig.Sink.Drain();
        Assert.Equal(10, burst.Count);
        Assert.All(burst, p => Assert.Equal(1360, p.Seg.Payload.Count));
    }

    [Fact]
    public async Task AWriteBiggerThanTheSendBufferCompletesAsAcknowledgmentsFreeRoom()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        var data = TcpRig.Bytes(TcpConnection.SendCapacity + 50_000);

        var write = rig.Conn.WriteAsync(data).AsTask();
        Assert.False(write.IsCompleted);

        var all = new List<SentPacket>();
        var acked = rig.SndBase;
        for (var guard = 0; guard < 5000 && !write.IsCompleted; guard++)
        {
            var round = rig.Sink.Drain();
            all.AddRange(round);
            acked += (uint)round.Sum(p => p.Seg.Payload.Count);
            rig.Acknowledge(acked);
            // The blocked writer resumes on the thread pool once room is freed.
            if (round.Count == 0) await Task.Delay(1);
        }
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        while (rig.Sink.Drain() is { Count: > 0 } more)
        {
            all.AddRange(more);
            acked += (uint)more.Sum(p => p.Seg.Payload.Count);
            rig.Acknowledge(acked);
        }

        Assert.Equal(data, TcpRig.Reassemble(all, rig.SndBase));
    }

    [Fact]
    public async Task AnUnacknowledgedWriteIsResentFromTheOldestByteAfterTheRto()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        var data = TcpRig.Bytes(4080);
        await rig.Write(data);
        var original = rig.Sink.Drain();
        Assert.Equal(3, original.Count);

        rig.AdvanceTo(rig.Now + TcpRig.RtoAfterHandshake - 1);
        Assert.Empty(rig.Sink.Drain());
        Assert.Equal(0, rig.Conn.Retransmits);

        rig.Advance(1);
        var resent = Assert.Single(rig.Sink.Drain());
        Assert.Equal(rig.SndBase, resent.Seg.Seq);
        Assert.Equal(data[..1360], resent.Bytes);
        Assert.Equal(1, rig.Conn.Retransmits);

        // Go-back-N: once the first is acknowledged, the rest follows again from where the peer says it is.
        rig.Acknowledge(rig.SndBase + 1360);
        var rest = rig.Sink.Drain();
        Assert.Equal([rig.SndBase + 1360, rig.SndBase + 2720], rest.Select(p => p.Seg.Seq));
        Assert.Equal(data[1360..], TcpRig.Reassemble(rest, rig.SndBase + 1360));
    }

    [Fact]
    public async Task AnAckForEverythingSentBeforeATimeoutIsTakenAfterIt()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        var data = TcpRig.Bytes(4080);
        await rig.Write(data);
        Assert.Equal(3, rig.Sink.Drain().Count);

        // All three arrived; their ACK was lost, and the timeout resent the first.
        rig.Advance(TcpRig.RtoAfterHandshake);
        Assert.Single(rig.Sink.Drain());

        // The peer answers the repeat with an ACK of all three: past what was resent, never past what was sent.
        rig.Acknowledge(rig.SndBase + 4080);
        Assert.DoesNotContain(rig.Sink.Drain(), p => p.HasPayload);
        rig.Advance(60_000);
        Assert.Empty(rig.Sink.Drain());
        Assert.Equal(1, rig.Conn.Retransmits);
        await rig.Write([1, 2, 3]);
        Assert.Equal(rig.SndBase + 4080, Assert.Single(rig.Sink.Drain()).Seg.Seq);
    }

    [Fact]
    public async Task AnAckPastEverythingEverSentIsStillRefused()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        await rig.Write(TcpRig.Bytes(1000));
        rig.Sink.Drain();

        rig.Acknowledge(rig.SndBase + 1001);
        var answer = Assert.Single(rig.Sink.Drain());
        Assert.Equal(TcpFlags.Ack, answer.Seg.Flags);
        Assert.Equal(rig.SndBase + 1000, answer.Seg.Seq);
        rig.Advance(TcpRig.RtoAfterHandshake);
        Assert.Equal(1, rig.Conn.Retransmits);
    }

    [Fact]
    public async Task TheRtoDoublesOnEachExpiryUntilTheConnectionFailsWithATimeout()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        await rig.Write(TcpRig.Bytes(2000));
        rig.Sink.Drain();

        // 600 ms to start, doubling, held at the 60 s ceiling; ten resends, and the eleventh expiry is fatal.
        long[] gaps = [600, 1200, 2400, 4800, 9600, 19_200, 38_400, 60_000, 60_000, 60_000];
        for (var i = 0; i < gaps.Length; i++)
        {
            rig.Advance(gaps[i] - 1);
            Assert.Empty(rig.Sink.Drain());
            rig.Advance(1);
            var resent = Assert.Single(rig.Sink.Drain());
            Assert.Equal(rig.SndBase, resent.Seg.Seq);
            Assert.Equal(1360, resent.Seg.Payload.Count);
            Assert.Equal(i + 1, rig.Conn.Retransmits);
        }

        rig.Advance(59_999);
        Assert.Empty(rig.Sink.Drain());
        rig.Advance(1);

        var reset = Assert.Single(rig.Sink.Drain()).Seg;
        Assert.Equal(TcpFlags.Rst | TcpFlags.Ack, reset.Flags);
        var read = await Assert.ThrowsAsync<IOException>(() => rig.Read(1));
        TcpRig.AssertFailedWith(read, SocketError.TimedOut);
        var write = await Assert.ThrowsAsync<IOException>(() => rig.Write([1]));
        TcpRig.AssertFailedWith(write, SocketError.TimedOut);
        Assert.Equal(10, rig.Conn.Retransmits);
        Assert.True(rig.Conn.Expired(rig.Now));
    }

    [Fact]
    public async Task ThreeDuplicateAcksRetransmitTheFirstSegmentWithoutWaitingForTheRto()
    {
        var rig = new TcpRig();
        rig.Establish(window: 65535, scale: null);
        var data = TcpRig.Bytes(6800);
        await rig.Write(data);
        Assert.Equal(5, rig.Sink.Drain().Count);

        rig.Acknowledge(rig.SndBase, window: 65535);
        rig.Acknowledge(rig.SndBase, window: 65535);
        Assert.Empty(rig.Sink.Drain());
        Assert.Equal(0, rig.Conn.Retransmits);

        rig.Acknowledge(rig.SndBase, window: 65535);

        var resent = Assert.Single(rig.Sink.Drain());
        Assert.Equal(rig.SndBase, resent.Seg.Seq);
        Assert.Equal(data[..1360], resent.Bytes);
        Assert.Equal(1, rig.Conn.Retransmits);
    }

    [Fact]
    public async Task ADuplicateAckWhoseWindowChangedDoesNotCountTowardsThree()
    {
        var rig = new TcpRig();
        rig.Establish(window: 65535, scale: null);
        await rig.Write(TcpRig.Bytes(6800));
        rig.Sink.Drain();

        rig.Acknowledge(rig.SndBase, window: 65535);   // counts: 1
        rig.Acknowledge(rig.SndBase, window: 60000);   // a window update, not a duplicate
        rig.Acknowledge(rig.SndBase, window: 60000);   // counts: 2
        Assert.Empty(rig.Sink.Drain());
        Assert.Equal(0, rig.Conn.Retransmits);

        // The window update was skipped, not counted: it takes this third duplicate, and not a fourth.
        rig.Acknowledge(rig.SndBase, window: 60000);
        Assert.Equal(rig.SndBase, Assert.Single(rig.Sink.Drain()).Seg.Seq);
        Assert.Equal(1, rig.Conn.Retransmits);
    }

    [Fact]
    public async Task DuplicateAcksCarryingDataDoNotCount()
    {
        var rig = new TcpRig();
        rig.Establish(window: 65535, scale: null);
        await rig.Write(TcpRig.Bytes(6800));
        rig.Sink.Drain();

        for (var i = 0; i < 3; i++) rig.Data(rig.RcvBase + (uint)(i * 10), TcpRig.Bytes(10), ack: rig.SndBase, window: 65535);

        Assert.Equal(0, rig.Conn.Retransmits);
        Assert.DoesNotContain(rig.Sink.Drain(), p => p.HasPayload);
    }

    [Fact]
    public async Task AnAckThatAdvancesResetsTheDuplicateCount()
    {
        var rig = new TcpRig();
        rig.Establish(window: 65535, scale: null);
        await rig.Write(TcpRig.Bytes(6800));
        rig.Sink.Drain();

        rig.Acknowledge(rig.SndBase, window: 65535);
        rig.Acknowledge(rig.SndBase, window: 65535);
        rig.Acknowledge(rig.SndBase + 1360, window: 65535);
        rig.Acknowledge(rig.SndBase + 1360, window: 65535);
        rig.Acknowledge(rig.SndBase + 1360, window: 65535);

        Assert.Equal(0, rig.Conn.Retransmits);
        Assert.Empty(rig.Sink.Drain());
    }

    [Fact]
    public async Task AZeroWindowIsProbedWithAnOldSequenceNumberAndOpeningItReleasesTheData()
    {
        var rig = new TcpRig();
        rig.Establish(window: 0, mss: 1360, scale: null);
        var data = TcpRig.Bytes(3000);
        await rig.Write(data);
        Assert.Empty(rig.Sink.Drain());

        // The first tick arms the persist timer; the probe comes one RTO after it.
        rig.Advance(0);
        rig.Advance(TcpRig.RtoAfterHandshake - 1);
        Assert.Empty(rig.Sink.Drain());
        rig.Advance(1);

        var probe = Assert.Single(rig.Sink.Drain());
        Assert.Equal(TcpFlags.Ack, probe.Seg.Flags);
        Assert.Equal(0, probe.Seg.Payload.Count);
        Assert.Equal(rig.SndBase - 1, probe.Seg.Seq);
        Assert.Equal(rig.RcvBase, probe.Seg.Ack);

        // Backed off: the next one is twice as far.
        rig.Advance(2 * TcpRig.RtoAfterHandshake - 1);
        Assert.Empty(rig.Sink.Drain());
        rig.Advance(1);
        Assert.Equal(rig.SndBase - 1, Assert.Single(rig.Sink.Drain()).Seg.Seq);

        rig.Acknowledge(rig.SndBase, window: 65535);

        var released = rig.Sink.Drain();
        Assert.Equal([1360, 1360, 280], released.Select(p => p.Seg.Payload.Count));
        Assert.Equal(data, TcpRig.Reassemble(released, rig.SndBase));
        Assert.Equal(0, rig.Conn.Retransmits);
    }

    [Fact]
    public void NoProbeIsSentWhileTheWindowIsOpen()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        rig.Advance(0);
        rig.Advance(100_000);

        Assert.Empty(rig.Sink.Drain());
    }
}

public class TcpCloseTests
{
    [Fact]
    public async Task OurFinFollowsTheDataAndThePeersFinEndsTheStreamAfterItsBufferedBytes()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        var written = TcpRig.Bytes(100);

        await rig.Write(written);
        rig.Conn.ShutdownWrite();

        var sent = rig.Sink.Drain();
        Assert.Equal(2, sent.Count);
        Assert.Equal(written, sent[0].Bytes);
        var fin = sent[1].Seg;
        Assert.Equal(TcpFlags.Ack | TcpFlags.Fin, fin.Flags);
        Assert.Equal(rig.SndBase + 100, fin.Seq);
        Assert.Equal(0, fin.Payload.Count);

        // The peer acknowledges the FIN, which takes a sequence number, then sends some data and its own.
        rig.Acknowledge(rig.SndBase + 101);
        Assert.False(rig.Conn.Expired(rig.Now + 1_000_000));
        var tail = TcpRig.Bytes(50, 4);
        rig.Data(rig.RcvBase, tail, ack: rig.SndBase + 101);
        rig.Conn.OnSegment(rig.Peer(rig.RcvBase + 50, rig.SndBase + 101, TcpFlags.Fin | TcpFlags.Ack, 65535));

        var finAck = Assert.Single(rig.Sink.Drain());
        Assert.True(finAck.IsPureAck);
        Assert.Equal(rig.RcvBase + 51, finAck.Seg.Ack);
        Assert.Equal(rig.SndBase + 101, finAck.Seg.Seq);
        Assert.Equal(tail, await rig.Read(50));
        Assert.Equal(0, await rig.ReadOnce());
        Assert.Equal(0, await rig.ReadOnce());
    }

    [Fact]
    public async Task AClosedConnectionIsKeptTenSecondsToAnswerARetransmittedFin()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        rig.Conn.ShutdownWrite();
        rig.Sink.Drain();
        rig.Acknowledge(rig.SndBase + 1);
        Assert.False(rig.Conn.Expired(rig.Now));

        rig.Fin(ack: rig.SndBase + 1);
        var closedAt = rig.Now;

        Assert.Equal(0, await rig.ReadOnce());
        Assert.False(rig.Conn.Expired(closedAt));
        Assert.False(rig.Conn.Expired(closedAt + 9_999));
        Assert.True(rig.Conn.Expired(closedAt + 10_000));

        // Their FIN again, because our ACK was lost: answered, and the ACK says the same thing.
        rig.Sink.Drain();
        rig.Now += 5000;
        rig.Fin(ack: rig.SndBase + 1);
        var again = Assert.Single(rig.Sink.Drain());
        Assert.True(again.IsPureAck);
        Assert.Equal(rig.RcvBase + 1, again.Seg.Ack);
    }

    [Fact]
    public async Task APeerFinFirstEndsTheReadAndOurLaterShutdownSendsOurFin()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        rig.Fin();

        Assert.Equal(0, await rig.ReadOnce());
        Assert.Empty(rig.Sink.Drain());
        rig.Conn.FlushAck();
        var ack = Assert.Single(rig.Sink.Drain());
        Assert.True(ack.IsPureAck);
        Assert.Equal(rig.RcvBase + 1, ack.Seg.Ack);
        Assert.False(rig.Conn.Expired(rig.Now + 1_000_000));

        rig.Conn.ShutdownWrite();

        var fin = Assert.Single(rig.Sink.Drain()).Seg;
        Assert.Equal(TcpFlags.Ack | TcpFlags.Fin, fin.Flags);
        Assert.Equal(rig.SndBase, fin.Seq);
        Assert.Equal(rig.RcvBase + 1, fin.Ack);

        rig.Acknowledge(rig.SndBase + 1);
        Assert.False(rig.Conn.Expired(rig.Now + 9_999));
        Assert.True(rig.Conn.Expired(rig.Now + 10_000));
    }

    [Fact]
    public async Task AFinSentWithAnAckOfOurFinClosesInOneSegment()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        await rig.Write(TcpRig.Bytes(10));
        rig.Conn.ShutdownWrite();
        rig.Sink.Drain();

        rig.Fin(ack: rig.SndBase + 11);

        Assert.Equal(0, await rig.ReadOnce());
        Assert.True(rig.Conn.Expired(rig.Now + 10_000));
    }

    [Fact]
    public async Task TheFinWaitsForDataThatTheWindowHasNotYetAllowed()
    {
        var rig = new TcpRig();
        rig.Establish(window: 2720, scale: null);
        var data = TcpRig.Bytes(4000);

        await rig.Write(data);
        rig.Conn.ShutdownWrite();

        var first = rig.Sink.Drain();
        Assert.Equal(2, first.Count);
        Assert.DoesNotContain(first, p => p.Seg.Has(TcpFlags.Fin));

        rig.Acknowledge(rig.SndBase + 2720, window: 2720);
        var last = rig.Sink.Drain();
        Assert.Equal([1280, 0], last.Select(p => p.Seg.Payload.Count));
        Assert.True(last[1].Seg.Has(TcpFlags.Fin));
        Assert.Equal(rig.SndBase + 4000, last[1].Seg.Seq);
        Assert.Equal(data, TcpRig.Reassemble(first.Concat(last), rig.SndBase));
    }

    [Fact]
    public async Task ALostFinIsRetransmittedAfterTheRto()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        await rig.Write(TcpRig.Bytes(10));
        rig.Conn.ShutdownWrite();
        rig.Sink.Drain();

        rig.Advance(TcpRig.RtoAfterHandshake);

        var resent = rig.Sink.Drain();
        Assert.Equal(2, resent.Count);
        Assert.Equal(rig.SndBase, resent[0].Seg.Seq);
        Assert.Equal(10, resent[0].Seg.Payload.Count);
        Assert.True(resent[1].Seg.Has(TcpFlags.Fin));
    }

    [Fact]
    public void DisposeWithoutShutdownResetsTheConnection()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        rig.Conn.Dispose();

        var reset = Assert.Single(rig.Sink.Drain()).Seg;
        Assert.Equal(TcpFlags.Rst | TcpFlags.Ack, reset.Flags);
        Assert.Equal(rig.SndBase, reset.Seq);
        Assert.Equal(rig.RcvBase, reset.Ack);
        Assert.True(rig.Conn.Expired(rig.Now));
    }

    [Fact]
    public void DisposeAfterShutdownLingersInsteadOfResetting()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        rig.Conn.ShutdownWrite();
        rig.Sink.Drain();

        rig.Conn.Dispose();

        Assert.Empty(rig.Sink.Drain());
        Assert.False(rig.Conn.Expired(rig.Now + 1_000_000));
    }

    [Fact]
    public void DisposeBeforeTheHandshakeEndsAbandonsItQuietly()
    {
        var rig = new TcpRig();
        rig.Open();

        rig.Conn.Dispose();

        // No connection exists on the far side to reset yet, so only the SYN was ever sent.
        Assert.Empty(rig.Sink.Drain());
        Assert.True(rig.Conn.Expired(rig.Now));
    }

    [Fact]
    public async Task AbortFailsReadsAndWritesAndSendsAReset()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        rig.Conn.Abort(SocketError.ConnectionAborted);

        Assert.Equal(TcpFlags.Rst | TcpFlags.Ack, Assert.Single(rig.Sink.Drain()).Seg.Flags);
        TcpRig.AssertFailedWith(await Assert.ThrowsAsync<IOException>(() => rig.Read(1)), SocketError.ConnectionAborted);
        TcpRig.AssertFailedWith(await Assert.ThrowsAsync<IOException>(() => rig.Write([1])), SocketError.ConnectionAborted);
    }

    [Fact]
    public async Task WritingAfterShutdownIsRefused()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);
        rig.Conn.ShutdownWrite();

        await Assert.ThrowsAsync<IOException>(() => rig.Write([1]));
    }

    [Fact]
    public async Task APeerResetInsideTheReceiveWindowFailsTheConnectionWithConnectionReset()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        rig.Conn.OnSegment(rig.Peer(rig.RcvBase, 0, TcpFlags.Rst, 0));

        TcpRig.AssertFailedWith(await Assert.ThrowsAsync<IOException>(() => rig.Read(1)), SocketError.ConnectionReset);
        TcpRig.AssertFailedWith(await Assert.ThrowsAsync<IOException>(() => rig.Write([1])), SocketError.ConnectionReset);
        // The peer already ended it: no reset of ours in answer.
        Assert.Empty(rig.Sink.Drain());
        Assert.True(rig.Conn.Expired(rig.Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(TcpRig.Capacity - 1)]
    public async Task APeerResetAnywhereInsideTheWindowIsBelieved(int offset)
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        rig.Conn.OnSegment(rig.Peer(rig.RcvBase + (uint)offset, 0, TcpFlags.Rst, 0));

        TcpRig.AssertFailedWith(await Assert.ThrowsAsync<IOException>(() => rig.Read(1)), SocketError.ConnectionReset);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(TcpRig.Capacity)]
    [InlineData(0x40000000)]
    public async Task APeerResetOutsideTheWindowIsIgnoredAndTheConnectionStaysUsable(int offset)
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        rig.Conn.OnSegment(rig.Peer(unchecked(rig.RcvBase + (uint)offset), 0, TcpFlags.Rst, 0));

        Assert.False(rig.Conn.Expired(rig.Now + 1_000_000));
        var data = TcpRig.Bytes(200);
        rig.Data(rig.RcvBase, data);
        Assert.Equal(data, await rig.Read(200));
        await rig.Write([1, 2, 3]);
        Assert.Equal([1, 2, 3], Assert.Single(rig.Sink.Drain(), p => p.HasPayload).Bytes);
    }

    [Fact]
    public void AnAckForBytesWeNeverSentIsAnsweredWithAnAckAndDropped()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        rig.Acknowledge(rig.SndBase + 5000);

        var answer = Assert.Single(rig.Sink.Drain());
        Assert.True(answer.IsPureAck);
        Assert.Equal(rig.SndBase, answer.Seg.Seq);
    }

    [Fact]
    public void ARepeatedSynAckIsAnsweredWithTheAckThatWasLost()
    {
        var rig = new TcpRig();
        rig.Establish(scale: 7);

        rig.Conn.OnSegment(rig.SynAck(65535, 1360, 7, true));

        var ack = Assert.Single(rig.Sink.Drain());
        Assert.True(ack.IsPureAck);
        Assert.Equal(rig.RcvBase, ack.Seg.Ack);
    }
}
