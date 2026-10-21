using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using Sallyport.Windows.PacketFilter;

namespace Sallyport.Windows.Routing;

/// <summary>What the router decided for a flow at its first packet, and kept.</summary>
public enum Verdict { Routed, Direct, Unowned, Blocked }

/// <summary>Why a selected program's flow was blocked rather than routed: IPv6 (the tunnel carries IPv4 only), or a
/// destination on this side of the tunnel — every host on a link, or this machine's own network (docs/design/router.md
/// §6). Sent direct, it would leave outside the tunnel; sent into it, it would reach nothing, or the server's network.</summary>
public enum BlockReason { None, Ipv6, Broadcast, Multicast, LinkLocal, OnLink }

/// <summary>Where a selected program's routed flows go now: into the tunnel through the current generation's NAT, held
/// (dropped while there is no tunnel — before the first, and while one is lost, as the loss policy says), or direct.</summary>
public enum SelectedPath { Held, Tunnel, Direct }

public sealed class FlowDecision(Verdict verdict, int pid, BlockReason reason = BlockReason.None, int lane = 0)
{
    public Verdict Verdict { get; internal set; } = verdict;
    public int Pid { get; internal set; } = pid;
    public BlockReason Reason { get; internal set; } = reason;
    /// <summary>The route a routed flow goes into: its program's profile, one lane each (D24).</summary>
    public int Lane { get; internal set; } = lane;
    public long Packets { get; internal set; }
    /// <summary>Environment.TickCount64 of its last packet.</summary>
    public long LastSeen { get; internal set; } = Environment.TickCount64;
    // Where its routed packets are counted, and for which process: a UDP port that changes hands is counted anew.
    internal ProcessRouted? Tally;
    internal int TallyPid;
}

/// <summary>What one process has had routed: the flows that went to the tunnel, the packets and bytes it sent into them,
/// the bytes that came back to it, and its round trips through the tunnel. Its flows, packets and bytes out are written
/// by the routing thread, its bytes in and round trips by the tunnel's; read by any.</summary>
public sealed class ProcessRouted
{
    /// <summary>The round trips kept, the latest: enough for a middle that moves with the path, not with one slow one.</summary>
    private const int Kept = 32;

    internal long _flows, _packets, _bytesOut, _bytesIn;
    private readonly Lock _times = new();
    private readonly double[] _handshakes = new double[Kept], _replies = new double[Kept];
    private int _handshakeCount, _replyCount;

    public long Flows => Interlocked.Read(ref _flows);
    public long Packets => Interlocked.Read(ref _packets);
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public long BytesIn => Interlocked.Read(ref _bytesIn);

    internal void Handshake(double milliseconds)
    {
        lock (_times) _handshakes[_handshakeCount++ % Kept] = milliseconds;
    }

    internal void Reply(double milliseconds)
    {
        lock (_times) _replies[_replyCount++ % Kept] = milliseconds;
    }

    /// <summary>Its round trip through the tunnel now, in milliseconds: the middle of its latest TCP handshakes (a SYN to
    /// its SYN-ACK — the network's own round trip); for a process that opened none, as a game on UDP alone, the shortest
    /// of its latest waits for a reply (a request's round trip, plus however long its server took, which the shortest
    /// holds least of). Null before either.</summary>
    public double? Latency
    {
        get
        {
            lock (_times)
            {
                if (_handshakeCount > 0) return _handshakes.Take(Math.Min(_handshakeCount, Kept)).Order().ElementAt(Math.Min(_handshakeCount, Kept) / 2);
                return _replyCount > 0 ? _replies.Take(Math.Min(_replyCount, Kept)).Min() : null;
            }
        }
    }
}

/// <summary>
/// The router (docs/design/router.md): every packet Windows sends on one adapter is matched to its process at its
/// flow's first packet; a selected process's IPv4 TCP and UDP go to the tunnel, NAT'd, its IPv6 and its packets for this
/// side of the tunnel are blocked, and everything else goes on unchanged. Replies from the tunnel are turned back and
/// handed up to Windows. The tunnel itself is not here — the caller sends what <c>toTunnel</c> is given and brings back
/// what the tunnel returns (<see cref="FromTunnel"/>). Tunnels come and go under it (<see cref="Commit"/>,
/// <see cref="Retire"/>); the interception and every flow's decision stay, so nothing selected slips out between two.
/// </summary>
public sealed partial class Router : IDisposable
{
    /// <summary>How stale the owner tables may grow before a UDP decision is checked again: a port let go and taken
    /// by another process was misattributed for 250 ms when nothing refreshed (measured, gate 3).</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a decision or a mapping outlives its last packet once no socket holds its local end. Two
    /// minutes, as home routers commonly keep a UDP mapping — a game keeps its own alive more often than that.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(10);
    private readonly PacketDriver _driver;
    private readonly Interception _interception;
    private readonly OwnerTable _owners = new();
    /// <summary>Routes at once in one router: one lane per profile whose apps it carries (D24).</summary>
    public const int Lanes = 8;

    private readonly Func<int, bool> _selects;
    private readonly Action<Nat, byte[]> _toTunnel;
    private readonly Func<DriverPacket, bool>? _discard;
    // Each lane's tunnel, as the other threads commit and retire it: read with Volatile, written whole.
    private readonly Nat?[] _nats = new Nat?[Lanes];
    private readonly int[] _paths = Enumerable.Repeat((int)SelectedPath.Held, Lanes).ToArray();
    private readonly (uint Network, uint Mask)[] _onLink;
    private readonly Dictionary<Flow, FlowDecision> _flows = [];
    private readonly Dictionary<Flow6, FlowDecision> _flows6 = [];
    private readonly long[] _blocked = new long[Enum.GetValues<BlockReason>().Length];
    private readonly Lock _injectLock = new();
    private readonly PacketBatch _inject = new(1);
    private readonly Dictionary<InboundRefusal, long> _refused = [];
    private readonly Dictionary<(byte, byte), long> _errors = [];
    private readonly Queue<(byte, byte, Flow)> _lastErrors = new();
    // When a routed SYN left, and when a routed UDP flow's datagram began waiting for its peer's next: the reply side
    // runs on the tunnel's thread.
    private readonly Lock _timing = new();
    // Each with the process it is counted for.
    private readonly Dictionary<Flow, (long At, ProcessRouted? Tally)> _synSent = [];
    private readonly Dictionary<Flow, (long At, ProcessRouted? Tally)> _udpWaiting = [];
    private long _lastRefresh;
    private long _lastRecheck;
    private long _lastSweep = Stopwatch.GetTimestamp();

    /// <param name="onLink">This machine's own networks on the adapter (address and mask, as <see cref="Flow"/> holds
    /// addresses): a selected program's packets to them are blocked, the tunnel having no way back to them.</param>
    /// <param name="discard">A measurement's cut: packets it picks are dropped before anything else, both ways when
    /// <paramref name="receivePath"/> takes what arrives too. Null in use.</param>
    /// <param name="toTunnel">Where a routed packet goes: the NAT that turned it (whose <see cref="Nat.Generation"/> names
    /// the tunnel), and the packet.</param>
    public Router(PacketDriver driver, ulong adapter, Func<int, bool> selects, Action<Nat, byte[]> toTunnel,
        IEnumerable<(uint Network, uint Mask)>? onLink = null, Func<DriverPacket, bool>? discard = null, bool receivePath = false)
    {
        _driver = driver;
        _selects = selects;
        _toTunnel = toTunnel;
        _discard = discard;
        _onLink = [.. onLink ?? []];
        _owners.Refresh();
        _lastRefresh = _lastRecheck = Stopwatch.GetTimestamp();
        _interception = new Interception(driver, adapter, 64, receivePath);
    }

    /// <summary>The first lane's current NAT; null while it has no tunnel.</summary>
    public Nat? Nat => NatOf(0);

    public SelectedPath Path => PathOf(0);

    public Nat? NatOf(int lane) => Volatile.Read(ref _nats[lane]);

    public SelectedPath PathOf(int lane) => (SelectedPath)Volatile.Read(ref _paths[lane]);

    /// <summary>A lane's which program goes into, by its process: its app's profile (D24). Null: every one the first.
    /// Asked on the router's thread when a flow is decided.</summary>
    public Func<int, int>? LaneOf { get; set; }

    /// <summary>A new tunnel's NAT for a lane: its routed flows go into it from their next packet. Mappings start afresh —
    /// the tunnel's address may have changed, and the server forgot the old ones.</summary>
    public void Commit(int lane, Nat nat)
    {
        Volatile.Write(ref _nats[lane], nat);
        Volatile.Write(ref _paths[lane], (int)SelectedPath.Tunnel);
        ForgetAhead(lane);
    }

    public void Commit(Nat nat) => Commit(0, nat);

    /// <summary>A lane's tunnel has gone: its routed flows are held until the next, or sent direct.</summary>
    public void Retire(int lane, bool direct)
    {
        Volatile.Write(ref _paths[lane], (int)(direct ? SelectedPath.Direct : SelectedPath.Held));
        Volatile.Write(ref _nats[lane], null);
        ForgetAhead(lane);
    }

    public void Retire(bool direct) => Retire(0, direct);

    /// <summary>Which selected processes go direct while the route holds the rest: their program's own loss policy, where
    /// the programs of one route differ (D23). Null: every one held. Asked on the router's thread, per packet while held.</summary>
    public Func<int, bool>? DirectWhileHeld { get; set; }

    /// <summary>Changes what is selected — <paramref name="change"/> runs on the router's own thread, between two batches —
    /// then decides every flow again: a program newly selected goes to the tunnel from its next packet, one let go goes
    /// direct from its next. Either may have to reconnect, as when a route starts or stops under it.</summary>
    public void Reselect(Action change) => _reselections.Enqueue(change);

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _reselections = new();

    /// <summary>Flows whose decision a change of selection turned: to the tunnel, and away from it.</summary>
    public long Reselected { get; private set; }

    /// <summary>What becomes of a routed packet too big for the tunnel in one piece and free to be split: sent whole
    /// (false), so Windows splits the outer datagram and the server puts it back together, or split here (true). Whole:
    /// the far end then receives the datagram as it was sent. Split, it receives pieces, and Google's STUN server, which
    /// answers a whole 1500-byte request, answered neither of two split ones (measured, gate 5).</summary>
    public bool SplitOversized { get; set; }

    /// <summary>Drains and decides until <paramref name="ct"/> is cancelled, then gives the adapter back.</summary>
    public void Run(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                _interception.Wait(50);
                var waited = Stopwatch.GetTimestamp();
                if (!_reselections.IsEmpty)
                {
                    while (_reselections.TryDequeue(out var change)) change();
                    DecideAgain();
                    Timed("deciding every flow again", waited);
                }
                if (Stopwatch.GetElapsedTime(_lastRecheck) >= Recheck) RecheckUdp();
                Timed("rechecking UDP", waited);
                var at = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(_lastSweep) >= SweepEvery) Sweep();
                Timed($"sweeping {_flows.Count} flows", at);
                at = Stopwatch.GetTimestamp();
                _interception.Drain(Decide);
                Timed("draining", at);
                Timed("waiting for the driver", started, waited);
            }
        }
        finally { _interception.Stop(); }
    }

    /// <summary>A packet a lane's tunnel returned: turned back and handed up, or refused and counted.</summary>
    public void FromTunnel(int lane, byte[] packet)
    {
        if (NatOf(lane) is not { } nat)
        {
            NoTunnel++;
            return;
        }
        var refusal = nat.Inbound(packet, out var mapping);
        lock (_injectLock)
        {
            if (refusal == InboundRefusal.PieceAhead)
            {
                HoldAhead(lane, packet);
                return;
            }
            if (refusal != InboundRefusal.None)
            {
                _refused[refusal] = _refused.GetValueOrDefault(refusal) + 1;
                return;
            }
            if (Inject(Nat.Frame(mapping!, packet)))
            {
                HandedUp++;
                if (Volatile.Read(ref mapping!.Tally) is { } owner) Interlocked.Add(ref owner._bytesIn, packet.Length);
                if (packet[9] == 1)
                {
                    ErrorsHandedUp++;
                    var icmp = packet.AsSpan((packet[0] & 0x0F) * 4);
                    var kind = (icmp[0], icmp[1]);
                    _errors[kind] = _errors.GetValueOrDefault(kind) + 1;
                    // The quote, turned back already: from the program's own address and port.
                    var quote = icmp[8..];
                    var quoted = quote[((quote[0] & 0x0F) * 4)..];
                    _lastErrors.Enqueue((icmp[0], icmp[1], new Flow(quote[9], BinaryPrimitives.ReadUInt32LittleEndian(quote[12..]), BinaryPrimitives.ReadUInt16BigEndian(quoted),
                        BinaryPrimitives.ReadUInt32LittleEndian(quote[16..]), BinaryPrimitives.ReadUInt16BigEndian(quoted[2..]))));
                    if (_lastErrors.Count > 32) _lastErrors.Dequeue();
                }
                else Answered(packet);
            }
            // A first piece names the mapping its later pieces follow: any that came before it can go now.
            if ((packet[6] & 0x20) != 0 && _ahead.Count > 0) ReleaseAhead(lane, packet);
        }
    }

    public void FromTunnel(byte[] packet) => FromTunnel(0, packet);

    private bool Decide(DriverPacket packet)
    {
        if (_discard?.Invoke(packet) == true)
        {
            Discarded++;
            return true;
        }
        if (packet.Direction != PacketDirection.Send) return false;
        var frame = packet.Frame;
        return Frames.TryReadOutbound(frame, out var flow) switch
        {
            FrameKind.Flow => DecideFlow(frame, flow),
            FrameKind.Fragment => DecidePiece(frame),
            FrameKind.Ipv6 => Decide6(frame),
            _ => false,
        };
    }

    private bool DecideFlow(ReadOnlySpan<byte> frame, in Flow flow)
    {
        var link = LinkLength(frame);
        var ip = frame[link..];
        var decision = Decision(flow, ip);
        if ((ip[6] & 0x20) != 0) _pieces[PieceKey(ip)] = (decision, Environment.TickCount64);
        switch (decision.Verdict)
        {
            case Verdict.Direct: DirectPackets++; return false;
            case Verdict.Unowned: UnownedPackets++; return false;
            case Verdict.Blocked: Block(decision.Reason, frame[..link], ip, flow.Protocol == Flow.Tcp ? (ip[0] & 0x0F) * 4 : -1); return true;
        }

        var lane = decision.Lane;
        if (WhileDown(decision.Pid, lane) is { } passed) return passed;
        var nat = NatOf(lane);
        if (nat is null) { HeldWhileDown++; return true; }
        var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        // A frame shorter than the packet it says it holds — a segmentation offload's, which none measured was (gate 5).
        if (total > ip.Length) { HeldBack++; return true; }
        if (total > nat.Mtu && (ip[6] & 0x40) != 0)
        {
            // Don't-fragment, and too big for the tunnel: the program is told so, as a router on its path would tell it.
            if (HandUp(frame[..link], Replies.FragmentationNeeded(ip, nat.Mtu))) TooBig++;
            return true;
        }
        // The frame may be padded past the packet to Ethernet's 60-byte minimum.
        var copy = ip[..total].ToArray();
        if (nat.Outbound(copy, frame[..link]) is not { } mapping) { HeldBack++; return true; }
        // What comes back on the mapping is counted for the process its socket is now.
        Volatile.Write(ref mapping.Tally, decision.Tally);
        if (decision.Tally is { } tally) Interlocked.Add(ref tally._bytesOut, total);
        Asked(flow, ip, decision.Tally);
        ToTunnel(nat, copy);
        return true;
    }

    private void Asked(in Flow flow, ReadOnlySpan<byte> ip, ProcessRouted? tally)
    {
        var now = Stopwatch.GetTimestamp();
        if (flow.Protocol == Flow.Udp)
            lock (_timing) _udpWaiting.TryAdd(flow, (now, tally));
        else if ((ip[(ip[0] & 0x0F) * 4 + 13] & 0x12) == 0x02)
            lock (_timing) _synSent[flow] = (now, tally);
    }

    // A whole TCP or UDP packet from the tunnel, already turned back to the program: whatever was waiting for it.
    private void Answered(ReadOnlySpan<byte> ip)
    {
        var protocol = ip[9];
        if (protocol is not (Flow.Tcp or Flow.Udp) || (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x1FFF) != 0) return;
        var segment = ip[((ip[0] & 0x0F) * 4)..];
        var flow = new Flow(protocol, BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]), BinaryPrimitives.ReadUInt16BigEndian(segment[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]), BinaryPrimitives.ReadUInt16BigEndian(segment));
        lock (_timing)
        {
            if (protocol == Flow.Udp)
            {
                if (_udpWaiting.Remove(flow, out var asked))
                {
                    var waited = Stopwatch.GetElapsedTime(asked.At).TotalMilliseconds;
                    UdpReplies.Add(waited);
                    asked.Tally?.Reply(waited);
                }
            }
            else if ((segment[13] & 0x12) == 0x12 && _synSent.Remove(flow, out var sent))
            {
                var trip = Stopwatch.GetElapsedTime(sent.At).TotalMilliseconds;
                Handshakes.Add(trip);
                sent.Tally?.Handshake(trip);
            }
        }
    }

    private bool Decide6(ReadOnlySpan<byte> frame)
    {
        var kind = Frames.TryReadOutbound6(frame, out var flow, out var transport, out var fragmentId);
        var link = LinkLength(frame);
        if (kind == FrameKind.Fragment)
        {
            if (!_pieces6.TryGetValue((flow.LocalAddress, flow.RemoteAddress, fragmentId!.Value), out var set)) { LonePieces++; return false; }
            if (set.Decision.Verdict != Verdict.Blocked) { DirectPackets++; return false; }
            Block(BlockReason.Ipv6, frame[..link], frame[link..], tcpAt: -1);
            return true;
        }
        if (kind != FrameKind.Flow) return false;

        if (!_flows6.TryGetValue(flow, out var decision))
        {
            decision = Classify(flow);
            _flows6[flow] = decision;
            if (decision.Verdict == Verdict.Blocked) BlockedFlows++;
        }
        decision.Packets++;
        decision.LastSeen = Environment.TickCount64;
        if (fragmentId is { } id) _pieces6[(flow.LocalAddress, flow.RemoteAddress, id)] = (decision, Environment.TickCount64);
        switch (decision.Verdict)
        {
            case Verdict.Blocked: Block(BlockReason.Ipv6, frame[..link], frame[link..], flow.Protocol == Flow.Tcp ? transport : -1); return true;
            case Verdict.Unowned: UnownedPackets++; return false;
            default: DirectPackets++; return false;
        }
    }

    private FlowDecision Decision(in Flow flow, ReadOnlySpan<byte> ip)
    {
        var syn = flow.Protocol == Flow.Tcp && (ip[(ip[0] & 0x0F) * 4 + 13] & 0x12) == 0x02;
        _flows.TryGetValue(flow, out var decision);
        // A SYN opens a connection, whoever had this four-tuple before: decided afresh, and the old decision kept only
        // when it is the same one — a retransmitted SYN counts nothing twice.
        var fresh = decision is null || syn ? Classify(flow, syn) : null;
        if (fresh is not null && (decision is null || fresh.Verdict != decision.Verdict || fresh.Pid != decision.Pid || fresh.Lane != decision.Lane))
        {
            decision = fresh;
            _flows[flow] = decision;
            if (decision.Verdict == Verdict.Routed)
            {
                RoutedFlows++;
                if (flow.Protocol == Flow.Tcp) RoutedTcpFlows++;
            }
            else if (decision.Verdict == Verdict.Blocked) BlockedFlows++;
        }
        decision!.Packets++;
        decision.LastSeen = Environment.TickCount64;
        if (decision.Verdict == Verdict.Routed) Count(decision);
        return decision;
    }

    // A routed packet against its process; a flow the first time it is routed for that process.
    private void Count(FlowDecision decision)
    {
        if (decision.Tally is not { } tally || decision.TallyPid != decision.Pid)
        {
            tally = _byProcess.GetOrAdd(decision.Pid, static _ => new ProcessRouted());
            decision.Tally = tally;
            decision.TallyPid = decision.Pid;
            Interlocked.Increment(ref tally._flows);
        }
        Interlocked.Increment(ref tally._packets);
    }

    // A routed packet with no tunnel to take it: true, dropped (held); false, passed on direct; null, there is a tunnel.
    private bool? WhileDown(int pid, int lane)
    {
        switch (PathOf(lane))
        {
            case SelectedPath.Held when DirectWhileHeld?.Invoke(pid) != true: HeldWhileDown++; return true;
            case SelectedPath.Held or SelectedPath.Direct: DirectWhileDown++; return false;
            default: return null;
        }
    }

    // A packet routed, split first if it is too big for the tunnel in one piece and the policy splits — into the tunnel
    // of the NAT that turned it.
    private void ToTunnel(Nat nat, byte[] packet)
    {
        var mtu = nat.Mtu;
        if (packet.Length > mtu && SplitOversized && (packet[0] & 0x0F) != 5) { HeldBack++; return; }
        RoutedPackets++;
        if (packet.Length > LargestRouted) LargestRouted = packet.Length;
        if (packet.Length <= mtu || !SplitOversized)
        {
            if (packet.Length > mtu) OversizedWhole++;
            _toTunnel(nat, packet);
            return;
        }
        SplitPackets++;
        foreach (var piece in Replies.Split(packet, mtu)) _toTunnel(nat, piece);
    }

    // A blocked packet is dropped; a connection it opens is refused at once (a reset), so the program goes on to another
    // address — the IPv4 one of a host it tried by IPv6 first — rather than waiting out its own retries. tcpAt: where
    // the packet's TCP header starts, or -1 when it carries none (UDP, or a later piece).
    private void Block(BlockReason reason, ReadOnlySpan<byte> link, ReadOnlySpan<byte> ip, int tcpAt)
    {
        Interlocked.Increment(ref _blocked[(int)reason]);
        if (tcpAt < 0 || ip.Length < tcpAt + 14 || (ip[tcpAt + 13] & 0x12) != 0x02) return;
        if (HandUp(link, ip[0] >> 4 == 6 ? Replies.Reset6(ip, tcpAt) : Replies.Reset(ip))) ResetsSent++;
    }

    private FlowDecision Classify(in Flow flow, bool syn = false)
    {
        var answer = _owners.Owner(flow, out var pid);
        var since = Stopwatch.GetElapsedTime(_lastRefresh);
        var refreshed = false;
        if (answer == Ownership.Unowned && Stale())
        {
            Refresh();
            refreshed = true;
            answer = _owners.Owner(flow, out pid);
        }
        if (answer != Ownership.Owned && syn)
            lock (_synMisses)
            {
                _synMisses.Enqueue($"{flow}: {answer}; the table read {since.TotalMilliseconds:F1} ms before{(refreshed ? ", read again" : ", not read again")};"
                    + $" its rows on port {flow.LocalPort}: {string.Join("; ", _owners.TcpRowsOn(flow.LocalPort).DefaultIfEmpty("none"))}");
                if (_synMisses.Count > 16) _synMisses.Dequeue();
            }
        return answer == Ownership.Owned ? Selected(pid, flow.RemoteAddress) : new(Verdict.Unowned, 0);
    }

    private FlowDecision Classify(in Flow6 flow)
    {
        var answer = _owners.Owner(flow, out var pid);
        if (answer == Ownership.Unowned && Stale())
        {
            Refresh();
            answer = _owners.Owner(flow, out pid);
        }
        if (answer != Ownership.Owned) return new(Verdict.Unowned, 0);
        return _selects(pid) ? new(Verdict.Blocked, pid, BlockReason.Ipv6) : new(Verdict.Direct, pid);
    }

    private FlowDecision Selected(int pid, uint remote)
    {
        if (!_selects(pid)) return new(Verdict.Direct, pid);
        var reason = Beyond(remote);
        return reason == BlockReason.None ? new(Verdict.Routed, pid, lane: Math.Clamp(LaneOf?.Invoke(pid) ?? 0, 0, Lanes - 1)) : new(Verdict.Blocked, pid, reason);
    }

    // Where the tunnel cannot take a selected program's packet. Addresses as Flow holds them: the first octet lowest.
    private BlockReason Beyond(uint address)
    {
        if (address == uint.MaxValue) return BlockReason.Broadcast;
        var first = (byte)address;
        if (first is >= 224 and <= 239) return BlockReason.Multicast;
        if (first == 169 && (byte)(address >> 8) == 254) return BlockReason.LinkLocal;
        foreach (var (network, mask) in _onLink)
            if ((address & mask) == (network & mask)) return BlockReason.OnLink;
        return BlockReason.None;
    }

    // A UDP port can change hands between snapshots, so every UDP decision is looked at again; a TCP four-tuple cannot,
    // so only one whose owner was not yet in the table at its first packet is — found later to be a selected program's,
    // it left direct, and is counted as such (router.md §4: could not be checked until it is known, failed once it is).
    private void RecheckUdp()
    {
        Refresh();
        _lastRecheck = _lastRefresh;
        foreach (var (flow, decision) in _flows)
        {
            if (flow.Protocol != Flow.Udp && decision.Verdict != Verdict.Unowned) continue;
            var answer = _owners.Owner(flow, out var pid);
            if (answer != Ownership.Owned || pid == decision.Pid) continue;
            Reconsider(decision, Selected(pid, flow.RemoteAddress));
        }
        foreach (var (flow, decision) in _flows6)
        {
            if (flow.Protocol != Flow.Udp) continue;
            var answer = _owners.Owner(flow, out var pid);
            if (answer != Ownership.Owned || pid == decision.Pid) continue;
            Reconsider(decision, _selects(pid) ? new(Verdict.Blocked, pid, BlockReason.Ipv6) : new(Verdict.Direct, pid));
        }
    }

    // After a change of selection: every flow whose owner is known, decided again for that owner. Not late flows — what
    // left before was not selected then.
    private void DecideAgain()
    {
        foreach (var (flow, decision) in _flows)
            if (decision.Verdict != Verdict.Unowned) Turn(decision, Selected(decision.Pid, flow.RemoteAddress), flow.Protocol);
        foreach (var (flow, decision) in _flows6)
            if (decision.Verdict != Verdict.Unowned)
                Turn(decision, _selects(decision.Pid) ? new(Verdict.Blocked, decision.Pid, BlockReason.Ipv6) : new(Verdict.Direct, decision.Pid), flow.Protocol);
    }

    private void Turn(FlowDecision decision, FlowDecision now, byte protocol)
    {
        if (decision.Verdict == now.Verdict && decision.Reason == now.Reason && decision.Lane == now.Lane) return;
        if (now.Verdict == Verdict.Routed)
        {
            RoutedFlows++;
            if (protocol == Flow.Tcp) RoutedTcpFlows++;
        }
        Reselected++;
        decision.Verdict = now.Verdict;
        decision.Pid = now.Pid;
        decision.Reason = now.Reason;
        decision.Lane = now.Lane;
    }

    private void Reconsider(FlowDecision decision, FlowDecision now)
    {
        var selectedNow = now.Verdict is Verdict.Routed or Verdict.Blocked;
        if (selectedNow && decision.Verdict is Verdict.Direct or Verdict.Unowned)
        {
            LateFlows++;
            LatePackets += decision.Packets;
        }
        decision.Verdict = now.Verdict;
        decision.Pid = now.Pid;
        decision.Reason = now.Reason;
        decision.Lane = now.Lane;
    }

    // Decisions and mappings idle past Idle go, unless a routed one's socket still holds its local end: a game's UDP
    // socket quiet in a menu keeps its tunnel port, so the server can still reach it. Any other is decided again at its
    // next packet — kept while its socket lived, one unselected program talking to many peers on one socket (a torrent's,
    // DHT's) would hold a decision per peer for as long as it ran.
    private void Sweep()
    {
        _lastSweep = Stopwatch.GetTimestamp();
        var idleSince = Environment.TickCount64 - (long)Idle.TotalMilliseconds;
        foreach (var flow in _flows.Where(f => f.Value.LastSeen < idleSince
                         && (f.Value.Verdict != Verdict.Routed || !_owners.Holds(f.Key.Protocol, f.Key.LocalAddress, f.Key.LocalPort)))
                     .Select(f => f.Key).ToList())
        {
            _flows.Remove(flow);
            ExpiredFlows++;
        }
        // Never routed: the tunnel carries IPv4 only.
        foreach (var flow in _flows6.Where(f => f.Value.LastSeen < idleSince).Select(f => f.Key).ToList())
        {
            _flows6.Remove(flow);
            ExpiredFlows++;
        }
        var now = Environment.TickCount64;
        foreach (var key in _pieces.Where(p => now - p.Value.Since > PieceLifeMs).Select(p => p.Key).ToList()) _pieces.Remove(key);
        foreach (var key in _pieces6.Where(p => now - p.Value.Since > PieceLifeMs).Select(p => p.Key).ToList()) _pieces6.Remove(key);
        // A SYN never answered, a datagram never replied to: not round trips.
        var stale = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 30;
        lock (_timing)
        {
            foreach (var key in _synSent.Where(s => s.Value.At < stale).Select(s => s.Key).ToList()) _synSent.Remove(key);
            foreach (var key in _udpWaiting.Where(s => s.Value.At < stale).Select(s => s.Key).ToList()) _udpWaiting.Remove(key);
        }
        for (var lane = 0; lane < Lanes; lane++)
            if (NatOf(lane) is { } nat) ExpiredMappings += nat.Expire(idleSince, m => _owners.Holds(m.Protocol, m.LocalAddress, m.LocalPort));
    }

    // A packet this router made, handed up as if it came from the far end of the frame it answers.
    private bool HandUp(ReadOnlySpan<byte> outboundLink, byte[] ip)
    {
        var link = Nat.Swapped(outboundLink);
        var frame = new byte[link.Length + ip.Length];
        link.CopyTo(frame, 0);
        ip.CopyTo(frame, link.Length);
        lock (_injectLock) return Inject(frame);
    }

    // Under _injectLock: one buffer serves every hand-up.
    private bool Inject(byte[] frame)
    {
        _inject.Write(0, _interception.Adapter, frame, PacketDirection.Receive);
        try
        {
            _driver.Send(_interception.Adapter, _inject, [0], toNetwork: false);
            return true;
        }
        catch (Win32Exception)
        {
            HandUpRefused++;
            return false;
        }
    }

    private static int LinkLength(ReadOnlySpan<byte> frame) => BinaryPrimitives.ReadUInt16BigEndian(frame[12..]) == 0x8100 ? 18 : 14;

    // A miss reads the tables again unless they were read after this packet's batch left the driver: such a read holds
    // every socket that sent a packet in it. One read a batch at most, then, and none missed for being a moment old —
    // a gap of 2 ms between reads instead left SYNs unowned whenever another miss had just read (measured, step 4).
    private bool Stale() => _lastRefresh < _interception.BatchTakenAt;

    private void Refresh()
    {
        _owners.Refresh();
        _lastRefresh = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        _interception.Dispose();
        _inject.Dispose();
    }
}
