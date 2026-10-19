using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Sallyport.Proxy;

namespace Sallyport.Net;

/// <summary>Where a connection's packets go, and who numbers them.</summary>
internal interface IPacketSink
{
    /// <summary>Queues an IPv4 packet for the tunnel. Never blocks: connections call it under their own lock.</summary>
    void Send(byte[] packet);
    ushort NextIpId();
}

/// <summary>
/// One outbound TCP connection through the tunnel, as a <see cref="Stream"/>. Active open only — a proxy never listens
/// inside the tunnel — and only what a browser needs: MSS and window scaling, retransmission with a measured timeout
/// (RFC 6298), slow start and fast retransmit, out-of-order reassembly, and a FIN in each direction.
/// </summary>
/// <remarks>Every method takes one lock; segments, timer ticks and the application all go through it, and packets
/// leave through <see cref="IPacketSink.Send"/>, which only queues.</remarks>
internal sealed class TcpConnection : Stream, IHalfClosable
{
    /// <summary>The largest segment this side accepts. Below the usual 1460 so a full segment plus the tunnel's own
    /// overhead still fits a 1500-byte path without fragmenting the outer UDP.</summary>
    public const int OurMss = 1360;
    /// <summary>A 4 MB receive window: a connection's ceiling is window ÷ round trip, and at the 200 ms the Japan route
    /// measured, 512 KB held downloads under 2 MB/s while Linux's larger window took the same server past 3.</summary>
    public const int ReceiveCapacity = 4 * 1024 * 1024;
    public const int SendCapacity = 256 * 1024;
    /// <summary>The window field is 16 bits; shifted by 6 it reaches the 4 MB above.</summary>
    private const byte OurScale = 6;
    private const int InitialRtoMs = 1000, MinRtoMs = 300, MaxRtoMs = 60_000, MaxBackoffs = 10, MaxSynBackoffs = 4;
    /// <summary>How long a closed-for-writing connection is kept after its stream is disposed, so what was written
    /// still gets delivered.</summary>
    private const long LingerMs = 30_000;
    /// <summary>How long a closed connection still answers a retransmitted FIN whose ACK was lost.</summary>
    private const long TimeWaitMs = 10_000;

    private enum State { Idle, SynSent, Established, Closed }

    private readonly object _gate = new();
    private readonly IPacketSink _sink;
    private readonly Func<long> _clock;
    private readonly IPAddress _local;
    private readonly ByteRing _send = new(SendCapacity), _receive = new(ReceiveCapacity);
    private readonly SortedDictionary<uint, (byte[] Data, bool Fin)> _early = new(SeqComparer.Instance);
    /// <summary>What <see cref="_early"/> holds, as contiguous ranges in sequence order, kept as segments arrive. Every
    /// early segment is answered at once with SACK blocks; built from the segments each time, one hole in a 4 MB window
    /// cost 3000 answers × 3000 segments, and the reader fell behind the tunnel and dropped packets of its own.</summary>
    private readonly List<(uint Left, uint Right)> _held = [];
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _segment = new byte[OurMss];
    private TaskCompletionSource? _readable, _writable;
    private State _state;
    private SocketException? _error;
    private bool _aborted, _disposed, _ackPending, _readablePending;

    private uint _iss, _sndUna, _sndNxt, _rcvNxt;
    private int _sndWnd, _sendMss = 536, _cwnd, _ssthresh = int.MaxValue, _dupAcks, _advertised;
    private byte _peerScale;
    private bool _scaling, _shutWrite, _finSent, _finAcked, _peerFin;
    private int _rto = InitialRtoMs, _backoffs;
    private double _srtt = -1, _rttvar;
    private long _rtoDue, _probeDue, _synSentAt, _closedAt, _disposedAt, _timedAt;
    private uint _timedSeq, _lastEarly;
    private bool _timing, _sackOk;

    public TcpConnection(IPacketSink sink, Func<long> clock, IPAddress local, ushort localPort, IPAddress remote, ushort remotePort)
    {
        _sink = sink;
        _clock = clock;
        _local = local;
        LocalPort = localPort;
        Remote = remote;
        RemotePort = remotePort;
    }

    public IPAddress Remote { get; }
    public ushort RemotePort { get; }
    public ushort LocalPort { get; }
    /// <summary>Completes when the handshake does; faults with a <see cref="SocketException"/> — ConnectionRefused for a
    /// reset, TimedOut for silence — when it cannot.</summary>
    public Task Connected => _connected.Task;
    public long BytesReceived { get; private set; }
    public long BytesSent { get; private set; }
    public int Retransmits { get; private set; }
    /// <summary>Segments that arrived past a gap. On a download this is the only trace of loss on the way in: the
    /// peer's retransmissions are invisible from here.</summary>
    public int SegmentsOutOfOrder { get; private set; }
    /// <summary>Segments that arrived again after being acknowledged — the peer did not hear our ACK in time.</summary>
    public int SegmentsRepeated { get; private set; }
    public int AcksSent { get; private set; }
    public int SegmentsIn { get; private set; }

    public void Open()
    {
        lock (_gate)
        {
            if (_state != State.Idle) return;
            _iss = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            _sndUna = _iss;
            _sndNxt = _iss + 1;
            _state = State.SynSent;
            _synSentAt = _clock();
            _rtoDue = _synSentAt + _rto;
            Emit(_iss, TcpFlags.Syn, [], syn: true);
        }
    }

    public void OnSegment(in TcpSegment s)
    {
        lock (_gate)
        {
            var now = _clock();
            SegmentsIn++;
            switch (_state)
            {
                case State.SynSent: OnSynSent(s, now); break;
                case State.Established: OnEstablished(s, now); break;
                case State.Closed when !_aborted && s.Has(TcpFlags.Fin):
                    Emit(_sndNxt, TcpFlags.Ack, []);
                    break;
            }
        }
    }

    /// <summary>Sends the ACK owed for what arrived, and wakes a reader waiting for it. Called once the tunnel's queue
    /// is drained, so a burst of segments is acknowledged by one ACK rather than one each — and hands the application
    /// one wake-up: waking it per segment queued a continuation per packet, and the woken reader then fought the
    /// network's reader for this connection's lock while the burst was still arriving.</summary>
    public void FlushAck()
    {
        lock (_gate)
        {
            if (_ackPending && !_aborted && _state is State.Established or State.Closed) Emit(_sndNxt, TcpFlags.Ack, []);
            if (_readablePending)
            {
                _readablePending = false;
                Signal(ref _readable);
            }
        }
    }

    public void OnTick(long now)
    {
        lock (_gate)
        {
            if (_state == State.SynSent && now >= _rtoDue)
            {
                if (++_backoffs > MaxSynBackoffs) { Fail(SocketError.TimedOut, sendReset: false); return; }
                _rto = Math.Min(_rto * 2, MaxRtoMs);
                _rtoDue = now + _rto;
                Retransmits++;
                Emit(_iss, TcpFlags.Syn, [], syn: true);
                return;
            }
            if (_state != State.Established) return;
            if (_rtoDue != 0 && now >= _rtoDue) OnRetransmitTimeout(now);
            else Probe(now);
            if (_state == State.Established && _disposed && now - _disposedAt >= LingerMs) Fail(SocketError.TimedOut, sendReset: true);
        }
    }

    /// <summary>Whether the network can forget this connection.</summary>
    public bool Expired(long now)
    {
        lock (_gate) return _state == State.Closed && (_aborted || now - _closedAt >= TimeWaitMs);
    }

    /// <summary>Ends the connection now, with a reset.</summary>
    public void Abort(SocketError reason = SocketError.ConnectionAborted)
    {
        lock (_gate)
        {
            if (_state is State.SynSent or State.Established) Fail(reason, sendReset: true);
        }
    }

    private void OnSynSent(in TcpSegment s, long now)
    {
        var acksOurSyn = s.Has(TcpFlags.Ack) && s.Ack == _iss + 1;
        if (s.Has(TcpFlags.Rst))
        {
            if (acksOurSyn) Fail(SocketError.ConnectionRefused, sendReset: false);
            return;
        }
        if (!s.Has(TcpFlags.Syn) || !acksOurSyn) return;

        _rcvNxt = s.Seq + 1;
        _sndUna = s.Ack;
        // Scaling holds only when both SYNs offered it; a SYN's own window is never scaled.
        _scaling = s.WindowScale is not null;
        _sackOk = s.SackPermitted;
        _peerScale = _scaling ? Math.Min(s.WindowScale!.Value, (byte)14) : (byte)0;
        _sndWnd = s.Window;
        _sendMss = Math.Clamp((int)(s.Mss ?? 536), 64, OurMss);
        _cwnd = 10 * _sendMss;
        if (_backoffs == 0) SampleRtt(now - _synSentAt);
        _backoffs = 0;
        _rtoDue = 0;
        _state = State.Established;
        Emit(_sndNxt, TcpFlags.Ack, []);
        _connected.TrySetResult();
        Pump(now);
    }

    private void OnEstablished(in TcpSegment s, long now)
    {
        if (s.Has(TcpFlags.Rst))
        {
            // Only a reset inside the receive window is believed: anything else is stale, or forged.
            if (Seq.InWindow(s.Seq, _rcvNxt, Math.Max(_receive.Free, 1))) Fail(SocketError.ConnectionReset, sendReset: false);
            return;
        }
        if (s.Has(TcpFlags.Syn))
        {
            // The peer is resending its SYN-ACK: the ACK that finished the handshake was lost.
            Emit(_sndNxt, TcpFlags.Ack, []);
            return;
        }
        if (!s.Has(TcpFlags.Ack)) return;
        if (!OnAck(s, now)) return;
        OnData(s);
        Pump(now);
        if (_finAcked && _peerFin) Close(now);
    }

    /// <returns>False when the segment is to be dropped (it acknowledges what was never sent).</returns>
    private bool OnAck(in TcpSegment s, long now)
    {
        var window = s.Window << _peerScale;
        if (Seq.Gt(s.Ack, _sndNxt))
        {
            Emit(_sndNxt, TcpFlags.Ack, []);
            return false;
        }
        if (Seq.Gt(s.Ack, _sndUna))
        {
            var acked = (int)(s.Ack - _sndUna);
            var data = Math.Min(acked, _send.Count);
            _send.Discard(data);
            if (_finSent && acked > data) _finAcked = true;
            _sndUna = s.Ack;
            if (_timing && Seq.Gt(s.Ack, _timedSeq))
            {
                _timing = false;
                SampleRtt(now - _timedAt);
            }
            _cwnd = Math.Min(_cwnd + (_cwnd < _ssthresh ? Math.Min(acked, _sendMss) : Math.Max(1, _sendMss * _sendMss / _cwnd)), 4 * SendCapacity);
            _dupAcks = 0;
            _backoffs = 0;
            _rtoDue = _sndNxt == _sndUna ? 0 : now + _rto;
            Signal(ref _writable);
        }
        else if (s.Ack == _sndUna && s.Payload.Count == 0 && !s.Has(TcpFlags.Fin) && window == _sndWnd && _sndNxt != _sndUna)
        {
            // Three identical ACKs while data is out: the peer keeps receiving past a hole at _sndUna.
            if (++_dupAcks == 3) FastRetransmit(now);
        }
        _sndWnd = window;
        return true;
    }

    private void OnData(in TcpSegment s)
    {
        var fin = s.Has(TcpFlags.Fin);
        var data = s.Payload.AsSpan();
        if (data.Length == 0 && !fin) return;
        if (_peerFin)
        {
            _ackPending = true;
            return;
        }
        var seq = s.Seq;
        if (Seq.Lt(seq, _rcvNxt))
        {
            var old = (int)(_rcvNxt - seq);
            if (old > data.Length || old == data.Length && !fin)
            {
                // Entirely a repeat: the peer did not hear our ACK, so say it again now.
                SegmentsRepeated++;
                Emit(_sndNxt, TcpFlags.Ack, []);
                return;
            }
            data = data[old..];
            seq = _rcvNxt;
        }
        if (seq == _rcvNxt)
        {
            Accept(data, fin);
            DrainEarly();
            _ackPending = true;
            _readablePending = true;
            return;
        }
        // Past a gap: kept if it fits the window, and acknowledged at once, so the repeated ACK tells the peer what is missing.
        var offset = (int)(seq - _rcvNxt);
        SegmentsOutOfOrder++;
        if (offset + data.Length <= _receive.Free && !_early.ContainsKey(seq))
        {
            _early[seq] = (data.ToArray(), fin);
            if (data.Length > 0) Hold(seq, seq + (uint)data.Length);
        }
        _lastEarly = seq;
        Emit(_sndNxt, TcpFlags.Ack, []);
    }

    private void Accept(ReadOnlySpan<byte> data, bool fin)
    {
        // What does not fit is dropped unacknowledged; the peer resends it once the window opens.
        var n = _receive.Append(data);
        _rcvNxt += (uint)n;
        BytesReceived += n;
        if (fin && n == data.Length)
        {
            _peerFin = true;
            _rcvNxt++;
        }
    }

    private void DrainEarly()
    {
        while (_early.Count > 0 && !_peerFin)
        {
            var (seq, (data, fin)) = _early.First();
            if (Seq.Gt(seq, _rcvNxt)) break;
            _early.Remove(seq);
            var skip = (int)(_rcvNxt - seq);
            if (skip > data.Length || skip == data.Length && !fin) continue;
            Accept(data.AsSpan(skip), fin);
        }
        if (_peerFin)
        {
            _early.Clear();
            _held.Clear();
            return;
        }
        while (_held.Count > 0 && Seq.Le(_held[0].Right, _rcvNxt)) _held.RemoveAt(0);
        if (_held.Count > 0 && Seq.Lt(_held[0].Left, _rcvNxt)) _held[0] = (_rcvNxt, _held[0].Right);
    }

    /// <summary>Adds [left, right) to <see cref="_held"/>, joining every range it touches.</summary>
    private void Hold(uint left, uint right)
    {
        // Past a hole, segments mostly arrive in order, so the place is looked for from the end.
        var at = _held.Count;
        while (at > 0 && Seq.Gt(_held[at - 1].Left, left)) at--;
        if (at > 0 && Seq.Ge(_held[at - 1].Right, left))
        {
            at--;
            left = _held[at].Left;
            if (Seq.Gt(_held[at].Right, right)) right = _held[at].Right;
            _held.RemoveAt(at);
        }
        while (at < _held.Count && Seq.Le(_held[at].Left, right))
        {
            if (Seq.Gt(_held[at].Right, right)) right = _held[at].Right;
            _held.RemoveAt(at);
        }
        _held.Insert(at, (left, right));
    }

    /// <summary>Sends what the windows allow, then the FIN once everything written has been sent.</summary>
    private void Pump(long now)
    {
        if (_state != State.Established) return;
        while (true)
        {
            var inFlight = (int)(_sndNxt - _sndUna);
            var dataSent = inFlight - (_finSent && !_finAcked ? 1 : 0);
            var unsent = _send.Count - dataSent;
            if (unsent > 0)
            {
                var room = Math.Min(_sndWnd, _cwnd) - inFlight;
                if (room <= 0) return;
                var n = Math.Min(Math.Min(unsent, _sendMss), room);
                _send.CopyTo(dataSent, _segment.AsSpan(0, n));
                if (!_timing)
                {
                    _timing = true;
                    _timedSeq = _sndNxt;
                    _timedAt = now;
                }
                Emit(_sndNxt, TcpFlags.Ack | TcpFlags.Psh, _segment.AsSpan(0, n));
                _sndNxt += (uint)n;
                BytesSent += n;
                if (_rtoDue == 0) _rtoDue = now + _rto;
                continue;
            }
            if (_shutWrite && !_finSent)
            {
                Emit(_sndNxt, TcpFlags.Ack | TcpFlags.Fin, []);
                _sndNxt++;
                _finSent = true;
                if (_rtoDue == 0) _rtoDue = now + _rto;
            }
            return;
        }
    }

    private void OnRetransmitTimeout(long now)
    {
        if (_sndNxt == _sndUna)
        {
            _rtoDue = 0;
            return;
        }
        if (++_backoffs > MaxBackoffs)
        {
            Fail(SocketError.TimedOut, sendReset: true);
            return;
        }
        _ssthresh = Math.Max((int)(_sndNxt - _sndUna) / 2, 2 * _sendMss);
        _cwnd = _sendMss;
        // Back to the oldest unacknowledged byte and on from there: without SACK, the peer's ACK is the only map of
        // what arrived.
        _sndNxt = _sndUna;
        if (!_finAcked) _finSent = false;
        // Karn: the ACK of a resent segment cannot say which copy it answers, so it times nothing.
        _timing = false;
        _rto = Math.Min(_rto * 2, MaxRtoMs);
        _rtoDue = 0;
        Retransmits++;
        Pump(now);
    }

    private void FastRetransmit(long now)
    {
        _ssthresh = Math.Max((int)(_sndNxt - _sndUna) / 2, 2 * _sendMss);
        _cwnd = _ssthresh;
        _timing = false;
        Retransmits++;
        var n = Math.Min(_send.Count, _sendMss);
        if (n > 0)
        {
            _send.CopyTo(0, _segment.AsSpan(0, n));
            Emit(_sndUna, TcpFlags.Ack | TcpFlags.Psh, _segment.AsSpan(0, n));
        }
        else if (_finSent && !_finAcked) Emit(_sndUna, TcpFlags.Ack | TcpFlags.Fin, []);
        _rtoDue = now + _rto;
    }

    /// <summary>The peer's window is shut and nothing is in flight to open it: ask for its window again now and then,
    /// with a segment it must answer. Losing its window update would otherwise stall the connection for good.</summary>
    private void Probe(long now)
    {
        var dataSent = (int)(_sndNxt - _sndUna) - (_finSent && !_finAcked ? 1 : 0);
        if (_sndWnd != 0 || _send.Count - dataSent <= 0 || _sndNxt != _sndUna)
        {
            _probeDue = 0;
            return;
        }
        if (_probeDue == 0) _probeDue = now + _rto;
        else if (now >= _probeDue)
        {
            Emit(_sndNxt - 1, TcpFlags.Ack, []);
            _probeDue = now + Math.Min(_rto * 2, MaxRtoMs);
        }
    }

    private void SampleRtt(long rtt)
    {
        if (_srtt < 0)
        {
            _srtt = rtt;
            _rttvar = rtt / 2.0;
        }
        else
        {
            _rttvar = 0.75 * _rttvar + 0.25 * Math.Abs(_srtt - rtt);
            _srtt = 0.875 * _srtt + 0.125 * rtt;
        }
        _rto = (int)Math.Clamp(_srtt + Math.Max(4 * _rttvar, 10), MinRtoMs, MaxRtoMs);
    }

    /// <summary>What is held past the gap, as SACK blocks (RFC 2018): contiguous ranges, the one holding the latest
    /// arrival first, so the peer can resend only the holes rather than everything after the first one.</summary>
    private (uint Left, uint Right)[] SackBlocks()
    {
        var latest = -1;
        for (var i = _held.Count - 1; i >= 0 && latest < 0; i--)
            if (Seq.Ge(_lastEarly, _held[i].Left) && Seq.Lt(_lastEarly, _held[i].Right)) latest = i;
        var ordered = new List<(uint, uint)>(Tcp.MaxSackBlocks);
        if (latest >= 0) ordered.Add(_held[latest]);
        for (var i = _held.Count - 1; i >= 0 && ordered.Count < Tcp.MaxSackBlocks; i--) if (i != latest) ordered.Add(_held[i]);
        return [.. ordered];
    }

    /// <summary>The window the 16-bit field can say right now, in bytes: unscaled it stops at 65535 however much is free.</summary>
    private int Advertisable(bool syn = false) => syn || !_scaling
        ? Math.Min(_receive.Free, ushort.MaxValue)
        : Math.Min(_receive.Free >> OurScale, ushort.MaxValue) << OurScale;

    /// <summary>How much the window must have grown since last said before a read announces it — a quarter of the most
    /// the field can say, so an unscaled window (at most 65535) is announced too, not only a scaled one.</summary>
    private int WindowUpdateStep => Math.Max(2 * _sendMss, (_scaling ? Math.Min(ReceiveCapacity, ushort.MaxValue << OurScale) : ushort.MaxValue) / 4);

    private void Emit(uint seq, TcpFlags flags, ReadOnlySpan<byte> payload, bool syn = false)
    {
        _advertised = Advertisable(syn);
        var field = syn || !_scaling ? _advertised : _advertised >> OurScale;
        var ack = (flags & TcpFlags.Ack) != 0 ? _rcvNxt : 0;
        var sack = (flags & TcpFlags.Ack) != 0 && _sackOk && _held.Count > 0 ? SackBlocks() : [];
        _sink.Send(Tcp.Build(_local, Remote, _sink.NextIpId(), LocalPort, RemotePort, seq, ack, flags, (ushort)field, payload,
            syn ? OurMss : null, syn ? OurScale : null, sackPermitted: syn, sack));
        if ((flags & TcpFlags.Ack) != 0) _ackPending = false;
        if (payload.Length == 0 && flags == TcpFlags.Ack) AcksSent++;
    }

    private void Close(long now)
    {
        if (_ackPending) Emit(_sndNxt, TcpFlags.Ack, []);
        _state = State.Closed;
        _closedAt = now;
        _rtoDue = 0;
        Signal(ref _readable);
        Signal(ref _writable);
    }

    private void Fail(SocketError reason, bool sendReset)
    {
        if (sendReset && _state == State.Established) Emit(_sndNxt, TcpFlags.Rst | TcpFlags.Ack, []);
        _error = new SocketException((int)reason);
        _state = State.Closed;
        _aborted = true;
        _closedAt = _clock();
        _connected.TrySetException(_error);
        Signal(ref _readable);
        Signal(ref _writable);
    }

    private static void Signal(ref TaskCompletionSource? waiter)
    {
        var w = waiter;
        waiter = null;
        w?.TrySetResult();
    }

    // ---- the Stream the application sees ----

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_receive.Count > 0)
                {
                    var n = _receive.Read(buffer.Span);
                    // Tell the peer the window reopened once it has grown enough to matter, not after every read.
                    if (_state == State.Established && Advertisable() - _advertised >= WindowUpdateStep)
                        Emit(_sndNxt, TcpFlags.Ack, []);
                    return n;
                }
                if (_peerFin) return 0;
                if (_error is not null) throw new IOException("the connection failed", _error);
                if (_state == State.Closed) return 0;
                _readable ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _readable.Task;
            }
            await wait.WaitAsync(ct);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        while (buffer.Length > 0)
        {
            Task wait;
            lock (_gate)
            {
                if (_error is not null) throw new IOException("the connection failed", _error);
                if (_shutWrite || _state != State.Established) throw new IOException("the connection is closed for writing");
                var n = _send.Append(buffer.Span);
                buffer = buffer[n..];
                if (n > 0) Pump(_clock());
                if (buffer.Length == 0) return;
                _writable ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _writable.Task;
            }
            await wait.WaitAsync(ct);
        }
    }

    public void ShutdownWrite()
    {
        lock (_gate)
        {
            if (_state != State.Established || _shutWrite) return;
            _shutWrite = true;
            Pump(_clock());
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _disposedAt = _clock();
            // Not closed for writing: the application gave up on it, so reset. Closed for writing: linger, so what it
            // wrote is still delivered and the peer's FIN still answered.
            if (_state == State.SynSent || _state == State.Established && !_shutWrite) Fail(SocketError.ConnectionAborted, sendReset: true);
        }
        base.Dispose(disposing);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
