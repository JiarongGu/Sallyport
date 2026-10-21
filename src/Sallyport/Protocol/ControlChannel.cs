using System.Security.Cryptography;
using System.Threading.Channels;
using Sallyport.Protocol;

namespace Sallyport.Protocol;

/// <summary>
/// The control channel's reliability layer. Every packet that carries a message id is resent until the peer acknowledges
/// it, and what arrives is handed on in message-id order, exactly once. TLS runs on top of this (<see cref="ControlStream"/>),
/// which is why it has to behave as a byte stream that never loses, repeats or reorders.
/// </summary>
/// <remarks>Symmetric on purpose: which side sends the hard reset is the caller's business, so the tests can run a
/// server on the same class.</remarks>
internal sealed class ControlChannel : IAsyncDisposable
{
    /// <summary>The most TLS bytes put in one packet. With tls-crypt's 49 bytes of overhead and a full ack area a
    /// datagram stays under 1250 bytes, which UDP paths carry without fragmenting.</summary>
    public const int MaxPayload = 1100;
    /// <summary>Messages in flight past the oldest unacknowledged one. Small on purpose: a peer drops what lands outside
    /// its receive window, and every dropped packet costs a whole retransmit timeout.</summary>
    public const int SendWindow = 4;
    /// <summary>The first resend waits this long, each later one twice the last, up to eight times it.</summary>
    internal long FirstRetransmitMs { get; init; } = 2000;
    internal TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>How far past the next expected id an early packet is still kept rather than dropped.</summary>
    private const uint ReceiveWindow = 64;

    private sealed class Outgoing(uint id, Opcode opcode, byte[] payload)
    {
        public uint Id { get; } = id;
        public Opcode Opcode { get; } = opcode;
        public byte[] Payload { get; } = payload;
        public long DueAt;
        public int Sends;
    }

    private readonly ITransport _transport;
    private readonly TlsCrypt _crypt;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<ControlPacket> _inbound = Channel.CreateUnbounded<ControlPacket>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<byte[]> _data;
    private readonly List<Outgoing> _outgoing = [];
    private readonly SortedDictionary<uint, ControlPacket> _early = [];
    private readonly List<uint> _acksOwed = [];
    // The long-form packet id's time half: when this sequence of replay ids began.
    private readonly uint _time = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private uint _replay, _nextId, _expected;
    private int _dropped;
    private long _dataOverflowed;
    private Task _receiving = Task.CompletedTask, _ticking = Task.CompletedTask;

    public ControlChannel(ITransport transport, TlsCrypt crypt)
    {
        _transport = transport;
        _crypt = crypt;
        // Bounded and lossy on purpose: a data packet nobody reads in time is a dropped datagram, which IP already
        // expects, where an unbounded queue would grow without limit behind a slow reader. Counted, because the replay
        // window books the skipped ids as missing, and nothing else would say the loss was this process's own.
        _data = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096) { SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref _dataOverflowed));
    }

    public byte[] LocalSession { get; } = RandomNumberGenerator.GetBytes(ControlPacket.SessionIdLength);
    public byte[]? PeerSession { get; private set; }
    /// <summary>Packets thrown away: they failed tls-crypt, or named a session that is not this one.</summary>
    public int Dropped => Volatile.Read(ref _dropped);
    /// <summary>Packets delivered by message id, in order, exactly once. Completes, with the error if any, when the
    /// transport fails or the channel is disposed.</summary>
    public ChannelReader<ControlPacket> Inbound => _inbound.Reader;
    /// <summary>Data-channel packets, still sealed: the same transport carries both channels, and only the opcode tells
    /// them apart.</summary>
    public ChannelReader<byte[]> DataInbound => _data.Reader;
    /// <summary>Data packets thrown away unread because <see cref="DataInbound"/> was full.</summary>
    public long DataOverflowed => Interlocked.Read(ref _dataOverflowed);

    /// <summary>Sends a sealed data packet. It shares the semaphore with the control channel because a TCP transport
    /// cannot take two writes at once.</summary>
    public async ValueTask SendDataAsync(byte[] packet, CancellationToken ct)
    {
        await _sending.WaitAsync(ct);
        try { await _transport.SendAsync(packet, ct); }
        finally { _sending.Release(); }
    }

    public bool AllAcknowledged { get { lock (_gate) return _outgoing.Count == 0; } }

    public void Start()
    {
        _receiving = Task.Run(() => ReceiveLoopAsync(_stop.Token));
        _ticking = Task.Run(() => TickLoopAsync(_stop.Token));
    }

    public Task SendAsync(Opcode opcode, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException($"a control packet carries at most {MaxPayload} bytes", nameof(payload));
        lock (_gate) _outgoing.Add(new Outgoing(_nextId++, opcode, payload));
        return FlushAsync(ct);
    }

    /// <summary>Sends whatever is due — new messages inside the window, retransmits, owed ACKs. Collecting and sending
    /// under one semaphore keeps replay ids leaving in the order they were assigned.</summary>
    private async Task FlushAsync(CancellationToken ct)
    {
        await _sending.WaitAsync(ct);
        try
        {
            List<byte[]> packets;
            lock (_gate) packets = CollectLocked(Environment.TickCount64);
            foreach (var packet in packets) await _transport.SendAsync(packet, ct);
        }
        finally { _sending.Release(); }
    }

    private List<byte[]> CollectLocked(long now)
    {
        var packets = new List<byte[]>();
        if (_outgoing.Count > 0)
        {
            var limit = _outgoing[0].Id + SendWindow;
            foreach (var o in _outgoing)
            {
                if (o.Id >= limit) break;
                if (o.Sends > 0 && (_transport.IsReliable || now < o.DueAt)) continue;
                packets.Add(BuildLocked(o.Opcode, o.Id, o.Payload));
                o.DueAt = now + (FirstRetransmitMs << Math.Min(o.Sends, 3));
                o.Sends++;
            }
        }
        // ACKs ride on whatever was going anyway; only the ones left over need a packet of their own.
        while (_acksOwed.Count > 0 && PeerSession is not null) packets.Add(BuildLocked(Opcode.AckV1, null, []));
        return packets;
    }

    private byte[] BuildLocked(Opcode opcode, uint? id, byte[] payload)
    {
        var take = PeerSession is null ? 0 : Math.Min(_acksOwed.Count, ControlPacket.MaxAcks);
        var acks = _acksOwed.GetRange(0, take);
        _acksOwed.RemoveRange(0, take);
        var packet = new ControlPacket(opcode, KeyId: 0, LocalSession, ++_replay, _time, acks, take > 0 ? PeerSession : null, id, payload);
        return packet.Encode(_crypt);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var datagram = await _transport.ReceiveAsync(ct);
                if (datagram.Length > 0 && (Opcode)(datagram[0] >> 3) is Opcode.DataV1 or Opcode.DataV2)
                {
                    _data.Writer.TryWrite(datagram);
                    continue;
                }
                var packet = ControlPacket.Decode(datagram, _crypt);
                if (packet is null) { Interlocked.Increment(ref _dropped); continue; }
                if (Accept(packet)) await FlushAsync(ct);
            }
            Complete(null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Complete(null); }
        catch (Exception e) { Complete(e); }
    }

    private void Complete(Exception? error)
    {
        _inbound.Writer.TryComplete(error);
        _data.Writer.TryComplete(error);
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await FlushAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { Complete(e); }
    }

    /// <returns>Whether the packet was taken — and so whether something (an ACK, or window room) may now be due.</returns>
    private bool Accept(ControlPacket packet)
    {
        lock (_gate)
        {
            if (PeerSession is not null && !packet.SessionId.AsSpan().SequenceEqual(PeerSession)) { _dropped++; return false; }
            // An ACK names whose packets it acknowledges; one that names another session is not about ours — and is not
            // the peer to pin either: a packet of an earlier connection, replayed, acknowledges that one's client.
            if (packet.Acks.Count > 0 && (packet.RemoteSessionId is null || !packet.RemoteSessionId.AsSpan().SequenceEqual(LocalSession))) { _dropped++; return false; }
            PeerSession ??= packet.SessionId;
            if (packet.Acks.Count > 0) _outgoing.RemoveAll(o => packet.Acks.Contains(o.Id));

            if (packet.MessageId is not { } id) return true;
            if (id == _expected)
            {
                Deliver(packet);
                while (_early.Remove(_expected, out var next)) Deliver(next);
            }
            // One past the window is dropped unacknowledged, so its sender sends it again; a repeat of one delivered is
            // acknowledged again, our earlier ACK having been lost.
            else if (id > _expected && id - _expected >= ReceiveWindow) return true;
            else if (id > _expected) _early.TryAdd(id, packet);
            if (!_acksOwed.Contains(id)) _acksOwed.Add(id);
            return true;
        }
    }

    private void Deliver(ControlPacket packet)
    {
        _inbound.Writer.TryWrite(packet);
        _expected++;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await Task.WhenAll(_receiving, _ticking); } catch (OperationCanceledException) { }
        await _transport.DisposeAsync();
        _stop.Dispose();
        _sending.Dispose();
    }
}
