using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;

namespace Sallyport.Net;

/// <summary>One echo's outcome: who answered and how soon — the destination itself, or a hop on the way (a time
/// exceeded or an unreachable quoting it) — or nothing in time.</summary>
public readonly record struct EchoAnswer(IPAddress? From, double? Milliseconds, bool FromDestination);

/// <summary>
/// ICMP echoes sent through a tunnel as its own address, for the latency a channel shows (docs/design/window.md): an
/// identifier of its own, and every answer claimed by the channel's <see cref="TunnelDispatcher"/> before anything else
/// sees it — an echo reply (type 0) with that identifier, or a time exceeded (11) or unreachable (3) quoting one.
/// </summary>
public sealed class TunnelEcho
{
    private const byte Icmp = 1;
    private readonly uint _address;
    private readonly Func<byte[], ValueTask> _send;
    // Not this process's id: a routed program's own ping (its identifier the NAT's) must never be taken for one of these.
    private readonly ushort _id = (ushort)Random.Shared.Next(1, 65536);
    private readonly Lock _lock = new();
    // Each echo by its sequence, with the address it went to: only that address answers it as the destination.
    private readonly Dictionary<ushort, (long Sent, uint To, TaskCompletionSource<EchoAnswer> Answer)> _outstanding = [];
    private ushort _sequence;

    public TunnelEcho(IPAddress tunnelAddress, Func<byte[], ValueTask> send)
    {
        _address = BinaryPrimitives.ReadUInt32BigEndian(tunnelAddress.GetAddressBytes());
        _send = send;
    }

    /// <summary>One echo to <paramref name="destination"/>, <paramref name="ttl"/> hops to live, waited for at most
    /// <paramref name="timeout"/>: no answer in time is an answer of its own (no address, no time), never an error.</summary>
    public async Task<EchoAnswer> PingAsync(IPAddress destination, TimeSpan timeout, byte ttl = 64, CancellationToken ct = default)
    {
        // IPv4 (20) and an echo request (type 8): identifier, sequence, then 32 bytes of payload.
        var packet = new byte[20 + 8 + 32];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[8] = ttl;
        packet[9] = Icmp;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), _address);
        destination.GetAddressBytes().CopyTo(packet, 16);
        var icmp = packet.AsSpan(20);
        icmp[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(icmp[4..], _id);
        ushort sequence;
        var answer = new TaskCompletionSource<EchoAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            sequence = ++_sequence;
            _outstanding[sequence] = (Stopwatch.GetTimestamp(), BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16)), answer);
        }
        BinaryPrimitives.WriteUInt16BigEndian(icmp[6..], sequence);
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], Fold(Sum(icmp)));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Fold(Sum(packet.AsSpan(0, 20))));
        try
        {
            await _send(packet);
            return await answer.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException) { return new EchoAnswer(null, null, false); }
        finally
        {
            lock (_lock) _outstanding.Remove(sequence);
        }
    }

    /// <summary>Whether <paramref name="packet"/> answers one of these echoes; timed and handed to its waiter if so.</summary>
    public bool Claim(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 28 || packet[0] != 0x45 || packet[9] != Icmp) return false;
        int at;
        var fromDestination = packet[20] == 0;
        if (fromDestination) at = 24;
        // The quote: the echo's own IPv4 header (20), then its type, code, checksum, identifier and sequence.
        else if (packet[20] is 3 or 11 && packet.Length >= 28 + 20 + 8 && packet[28] == 0x45 && packet[28 + 9] == Icmp && packet[48] == 8) at = 52;
        else return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(packet[at..]) != _id) return false;
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
        (long Sent, uint To, TaskCompletionSource<EchoAnswer> Answer) waiting;
        lock (_lock)
        {
            // A reply to an echo from anyone but the address pinged is not the destination's answer: claimed (it is
            // ours), and not taken for one.
            if (!_outstanding.TryGetValue(sequence, out waiting)) return true;
            if (fromDestination && BinaryPrimitives.ReadUInt32BigEndian(packet[12..]) != waiting.To) return true;
            _outstanding.Remove(sequence);
        }
        waiting.Answer.TrySetResult(new EchoAnswer(new IPAddress(packet.Slice(12, 4)), Stopwatch.GetElapsedTime(waiting.Sent).TotalMilliseconds, fromDestination));
        return true;
    }

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
        return sum;
    }

    private static ushort Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
