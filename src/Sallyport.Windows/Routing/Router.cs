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

public sealed class FlowDecision(Verdict verdict, int pid, BlockReason reason = BlockReason.None)
{
    public Verdict Verdict { get; internal set; } = verdict;
    public int Pid { get; internal set; } = pid;
    public BlockReason Reason { get; internal set; } = reason;
    public long Packets { get; internal set; }
    /// <summary>Environment.TickCount64 of its last packet.</summary>
    public long LastSeen { get; internal set; } = Environment.TickCount64;
}

/// <summary>
/// The router (docs/design/router.md): every packet Windows sends on one adapter is matched to its process at its
/// flow's first packet; a selected process's IPv4 TCP and UDP go to the tunnel, NAT'd, its IPv6 and its packets for this
/// side of the tunnel are blocked, and everything else goes on unchanged. Replies from the tunnel are turned back and
/// handed up to Windows. The tunnel itself is not here — the caller sends what <c>toTunnel</c> is given and brings back
/// what the tunnel returns (<see cref="FromTunnel"/>). Tunnels come and go under it (<see cref="Commit"/>,
/// <see cref="Retire"/>); the interception and every flow's decision stay, so nothing selected slips out between two.
/// </summary>
public sealed class Router : IDisposable
{
    /// <summary>How stale the owner tables may grow before a UDP decision is checked again: a port let go and taken
    /// by another process was misattributed for 250 ms when nothing refreshed (measured, gate 3).</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a decision or a mapping outlives its last packet once no socket holds its local end. Two
    /// minutes, as home routers commonly keep a UDP mapping — a game keeps its own alive more often than that.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(10);
    // A datagram's pieces leave within microseconds of each other, and through the tunnel within milliseconds.
    private const long PieceLifeMs = 30_000, AheadLifeMs = 2_000;
    private const int MostAhead = 64;

    private readonly PacketDriver _driver;
    private readonly Interception _interception;
    private readonly OwnerTable _owners = new();
    private readonly Func<int, bool> _selects;
    private readonly Action<byte[]> _toTunnel;
    private readonly Func<DriverPacket, bool>? _discard;
    private volatile Nat? _nat;
    private volatile SelectedPath _path = SelectedPath.Held;
    private readonly (uint Network, uint Mask)[] _onLink;
    private readonly Dictionary<Flow, FlowDecision> _flows = [];
    private readonly Dictionary<Flow6, FlowDecision> _flows6 = [];
    // What the first piece of a datagram Windows sent in pieces was decided: its later pieces carry no ports.
    private readonly Dictionary<(uint, uint, byte, ushort), (FlowDecision Decision, long Since)> _pieces = [];
    private readonly Dictionary<(UInt128, UInt128, uint), (FlowDecision Decision, long Since)> _pieces6 = [];
    private readonly List<(uint Source, byte Protocol, ushort Id, byte[] Packet, long At)> _ahead = [];
    private readonly long[] _blocked = new long[Enum.GetValues<BlockReason>().Length];
    private readonly Lock _injectLock = new();
    private readonly PacketBatch _inject = new(1);
    private readonly Dictionary<InboundRefusal, long> _refused = [];
    private readonly Dictionary<(byte, byte), long> _errors = [];
    private readonly Queue<(byte, byte, Flow)> _lastErrors = new();
    // When a routed SYN left, and when a routed UDP flow's datagram began waiting for its peer's next: the reply side
    // runs on the tunnel's thread.
    private readonly Lock _timing = new();
    private readonly Dictionary<Flow, long> _synSent = [];
    private readonly Dictionary<Flow, long> _udpWaiting = [];
    private long _lastRefresh;
    private long _lastRecheck;
    private long _lastSweep = Stopwatch.GetTimestamp();

    /// <param name="onLink">This machine's own networks on the adapter (address and mask, as <see cref="Flow"/> holds
    /// addresses): a selected program's packets to them are blocked, the tunnel having no way back to them.</param>
    /// <param name="discard">A measurement's cut: packets it picks are dropped before anything else, both ways when
    /// <paramref name="receivePath"/> takes what arrives too. Null in use.</param>
    public Router(PacketDriver driver, ulong adapter, Func<int, bool> selects, Action<byte[]> toTunnel,
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

    /// <summary>The current generation's NAT; null while there is no tunnel.</summary>
    public Nat? Nat => _nat;

    public SelectedPath Path => _path;

    /// <summary>A new tunnel's NAT: routed flows go into it from their next packet. Mappings start afresh — the tunnel's
    /// address may have changed, and the server forgot the old ones.</summary>
    public void Commit(Nat nat)
    {
        _nat = nat;
        _path = SelectedPath.Tunnel;
    }

    /// <summary>The tunnel has gone: routed flows are held until the next, or sent direct.</summary>
    public void Retire(bool direct)
    {
        _path = direct ? SelectedPath.Direct : SelectedPath.Held;
        _nat = null;
    }

    /// <summary>What becomes of a routed packet too big for the tunnel in one piece and free to be split: sent whole
    /// (false), so Windows splits the outer datagram and the server puts it back together, or split here (true). Whole:
    /// the far end then receives the datagram as it was sent. Split, it receives pieces, and Google's STUN server, which
    /// answers a whole 1500-byte request, answered neither of two split ones (measured, gate 5).</summary>
    public bool SplitOversized { get; set; }

    public Interception Interception => _interception;
    public OwnerTable Owners => _owners;
    public long RoutedPackets { get; private set; }
    public long DirectPackets { get; private set; }
    public long UnownedPackets { get; private set; }
    /// <summary>Selected packets dropped rather than sent direct: no tunnel port free, a frame shorter than the packet it
    /// holds, or IP options on one too big for the tunnel.</summary>
    public long HeldBack { get; private set; }
    /// <summary>Flows first decided direct or unowned that a later look found a selected process owning: what of a
    /// selected program left directly, counted (rule 6: coverage failed, not unknown).</summary>
    public long LateFlows { get; private set; }
    public long LatePackets { get; private set; }
    public long HandedUp { get; private set; }
    /// <summary>Selected packets dropped while held, and passed on while direct: no tunnel to take them.</summary>
    public long HeldWhileDown { get; private set; }
    public long DirectWhileDown { get; private set; }
    /// <summary>Packets the tunnel returned with no generation to take them (between two tunnels).</summary>
    public long NoTunnel { get; private set; }
    /// <summary>Packets a measurement's cut dropped.</summary>
    public long Discarded { get; private set; }
    public long HandUpRefused { get; private set; }
    public long RoutedFlows { get; private set; }
    public long RoutedTcpFlows { get; private set; }
    public long BlockedFlows { get; private set; }
    /// <summary>Resets handed to a selected program for a connection it may not open (<see cref="BlockReason"/>).</summary>
    public long ResetsSent { get; private set; }
    /// <summary>"Fragmentation needed" handed to a selected program for a don't-fragment packet too big for the tunnel.</summary>
    public long TooBig { get; private set; }
    /// <summary>Routed packets too big for the tunnel in one piece and free to be split: sent as pieces, or whole
    /// (<see cref="SplitOversized"/>).</summary>
    public long SplitPackets { get; private set; }
    public long OversizedWhole { get; private set; }
    /// <summary>Later pieces of datagrams Windows sent in pieces, routed after their first.</summary>
    public long PiecesRouted { get; private set; }
    /// <summary>Later pieces whose first piece was never seen: passed on, their owner unknown.</summary>
    public long LonePieces { get; private set; }
    /// <summary>Pieces from the tunnel that came before their first piece, held for it; and those it never came for.</summary>
    public long PiecesAhead { get; private set; }
    public long PiecesAheadLost { get; private set; }
    /// <summary>ICMP errors from the tunnel about a routed packet, turned back and handed up; and by type and code.</summary>
    public long ErrorsHandedUp { get; private set; }
    public IReadOnlyDictionary<(byte Type, byte Code), long> Errors { get { lock (_injectLock) return new Dictionary<(byte, byte), long>(_errors); } }
    /// <summary>The last ICMP errors handed up: their type and code, and the flow each was about.</summary>
    public IReadOnlyList<(byte Type, byte Code, Flow About)> LastErrors { get { lock (_injectLock) return [.. _lastErrors]; } }
    /// <summary>TCP connections whose SYN found no owner — decided unowned, so direct, for their whole life — and what
    /// the owner table held then: the last 16, to say why (rule 6: coverage that failed is named).</summary>
    public IReadOnlyList<string> SynMisses { get { lock (_synMisses) return [.. _synMisses]; } }
    private readonly Queue<string> _synMisses = new();

    /// <summary>The largest packet routed, before any split.</summary>
    public int LargestRouted { get; private set; }
    public long ExpiredFlows { get; private set; }
    public long ExpiredMappings { get; private set; }
    public IReadOnlyDictionary<InboundRefusal, long> Refused { get { lock (_injectLock) return new Dictionary<InboundRefusal, long>(_refused); } }

    /// <summary>Routed TCP handshakes through the tunnel: a SYN leaving to its SYN-ACK handed up — the network's round
    /// trip to that server, the tunnel included.</summary>
    public RoundTrips Handshakes { get; } = new();

    /// <summary>A routed UDP datagram to the next one back from its peer, per flow: a game's request waiting for its
    /// server — the round trip plus however long the server takes, or less when the server sends unasked.</summary>
    public RoundTrips UdpReplies { get; } = new();

    /// <summary>Packets blocked, by why.</summary>
    public IReadOnlyDictionary<BlockReason, long> Blocked =>
        Enum.GetValues<BlockReason>().Where(r => Volatile.Read(ref _blocked[(int)r]) > 0).ToDictionary(r => r, r => Volatile.Read(ref _blocked[(int)r]));

    public IReadOnlyDictionary<Flow, FlowDecision> Flows => _flows;
    public IReadOnlyDictionary<Flow6, FlowDecision> Flows6 => _flows6;

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

    /// <summary>Moments the router's loop stood still longer than 200 ms, and in what: while it stands, everything Windows
    /// sends on the adapter waits in the driver (measured, item 2 step 4), so a stall is named. The last 32.</summary>
    public IReadOnlyList<string> Stalls { get { lock (_stalls) return [.. _stalls]; } }
    private readonly Queue<string> _stalls = new();

    private void Timed(string what, long since, long? until = null)
    {
        var took = Stopwatch.GetElapsedTime(since, until ?? Stopwatch.GetTimestamp());
        if (took < TimeSpan.FromMilliseconds(200)) return;
        lock (_stalls)
        {
            _stalls.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {what}: {took.TotalMilliseconds:F0} ms");
            if (_stalls.Count > 32) _stalls.Dequeue();
        }
    }

    /// <summary>A packet the tunnel returned: turned back and handed up, or refused and counted.</summary>
    public void FromTunnel(byte[] packet)
    {
        if (_nat is not { } nat)
        {
            NoTunnel++;
            return;
        }
        var refusal = nat.Inbound(packet, out var mapping);
        lock (_injectLock)
        {
            if (refusal == InboundRefusal.PieceAhead)
            {
                HoldAhead(packet);
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
            if ((packet[6] & 0x20) != 0 && _ahead.Count > 0) ReleaseAhead(packet);
        }
    }

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

        if (WhileDown() is { } passed) return passed;
        var nat = _nat;
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
        if (nat.Outbound(copy, frame[..link]) is null) { HeldBack++; return true; }
        Asked(flow, ip);
        ToTunnel(copy, nat.Mtu);
        return true;
    }

    private void Asked(in Flow flow, ReadOnlySpan<byte> ip)
    {
        var now = Stopwatch.GetTimestamp();
        if (flow.Protocol == Flow.Udp)
            lock (_timing) _udpWaiting.TryAdd(flow, now);
        else if ((ip[(ip[0] & 0x0F) * 4 + 13] & 0x12) == 0x02)
            lock (_timing) _synSent[flow] = now;
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
                if (_udpWaiting.Remove(flow, out var asked)) UdpReplies.Add(Stopwatch.GetElapsedTime(asked).TotalMilliseconds);
            }
            else if ((segment[13] & 0x12) == 0x12 && _synSent.Remove(flow, out var sent)) Handshakes.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
        }
    }

    // A later piece of a datagram Windows sent in pieces: it follows its first.
    private bool DecidePiece(ReadOnlySpan<byte> frame)
    {
        var link = LinkLength(frame);
        var ip = frame[link..];
        if (!_pieces.TryGetValue(PieceKey(ip), out var set)) { LonePieces++; return false; }
        switch (set.Decision.Verdict)
        {
            case Verdict.Direct: DirectPackets++; return false;
            case Verdict.Unowned: UnownedPackets++; return false;
            case Verdict.Blocked: Block(set.Decision.Reason, frame[..link], ip, tcpAt: -1); return true;
        }
        if (WhileDown() is { } passed) return passed;
        var nat = _nat;
        if (nat is null) { HeldWhileDown++; return true; }
        var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        if (total > ip.Length) { HeldBack++; return true; }
        var copy = ip[..total].ToArray();
        nat.OutboundPiece(copy);
        PiecesRouted++;
        ToTunnel(copy, nat.Mtu);
        return true;
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
        if (!_flows.TryGetValue(flow, out var decision))
        {
            decision = Classify(flow, flow.Protocol == Flow.Tcp && (ip[(ip[0] & 0x0F) * 4 + 13] & 0x12) == 0x02);
            _flows[flow] = decision;
            if (decision.Verdict == Verdict.Routed)
            {
                RoutedFlows++;
                if (flow.Protocol == Flow.Tcp) RoutedTcpFlows++;
            }
            else if (decision.Verdict == Verdict.Blocked) BlockedFlows++;
        }
        decision.Packets++;
        decision.LastSeen = Environment.TickCount64;
        return decision;
    }

    // A routed packet with no tunnel to take it: true, dropped (held); false, passed on direct; null, there is a tunnel.
    private bool? WhileDown()
    {
        switch (_path)
        {
            case SelectedPath.Held: HeldWhileDown++; return true;
            case SelectedPath.Direct: DirectWhileDown++; return false;
            default: return null;
        }
    }

    // A packet routed, split first if it is too big for the tunnel in one piece and the policy splits.
    private void ToTunnel(byte[] packet, int mtu)
    {
        if (packet.Length > mtu && SplitOversized && (packet[0] & 0x0F) != 5) { HeldBack++; return; }
        RoutedPackets++;
        if (packet.Length > LargestRouted) LargestRouted = packet.Length;
        if (packet.Length <= mtu || !SplitOversized)
        {
            if (packet.Length > mtu) OversizedWhole++;
            _toTunnel(packet);
            return;
        }
        SplitPackets++;
        foreach (var piece in Replies.Split(packet, mtu)) _toTunnel(piece);
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
        return reason == BlockReason.None ? new(Verdict.Routed, pid) : new(Verdict.Blocked, pid, reason);
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

    // A UDP port can change hands between snapshots; a TCP four-tuple cannot, so only UDP decisions are looked at again.
    private void RecheckUdp()
    {
        Refresh();
        _lastRecheck = _lastRefresh;
        foreach (var (flow, decision) in _flows)
        {
            if (flow.Protocol != Flow.Udp) continue;
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
    }

    // Decisions and mappings idle past Idle go, unless a socket still holds their local end: a game's UDP socket quiet in
    // a menu keeps its tunnel port, so the server can still reach it.
    private void Sweep()
    {
        _lastSweep = Stopwatch.GetTimestamp();
        var idleSince = Environment.TickCount64 - (long)Idle.TotalMilliseconds;
        foreach (var flow in _flows.Where(f => f.Value.LastSeen < idleSince && !_owners.Holds(f.Key.Protocol, f.Key.LocalAddress, f.Key.LocalPort))
                     .Select(f => f.Key).ToList())
        {
            _flows.Remove(flow);
            ExpiredFlows++;
        }
        foreach (var flow in _flows6.Where(f => f.Value.LastSeen < idleSince && !_owners.Holds(f.Key.Protocol, f.Key.LocalAddress, f.Key.LocalPort))
                     .Select(f => f.Key).ToList())
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
            foreach (var key in _synSent.Where(s => s.Value < stale).Select(s => s.Key).ToList()) _synSent.Remove(key);
            foreach (var key in _udpWaiting.Where(s => s.Value < stale).Select(s => s.Key).ToList()) _udpWaiting.Remove(key);
        }
        if (_nat is { } nat) ExpiredMappings += nat.Expire(idleSince, m => _owners.Holds(m.Protocol, m.LocalAddress, m.LocalPort));
    }

    private void HoldAhead(byte[] packet)
    {
        var now = Environment.TickCount64;
        PiecesAheadLost += _ahead.RemoveAll(a => now - a.At > AheadLifeMs);
        if (_ahead.Count >= MostAhead)
        {
            _ahead.RemoveAt(0);
            PiecesAheadLost++;
        }
        _ahead.Add((BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)), packet[9], BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)), packet, now));
        PiecesAhead++;
    }

    private void ReleaseAhead(byte[] first)
    {
        var source = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(12));
        var id = BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(4));
        foreach (var held in _ahead.Where(a => a.Source == source && a.Protocol == first[9] && a.Id == id).ToList())
        {
            _ahead.Remove(held);
            if (_nat?.Inbound(held.Packet, out var mapping) == InboundRefusal.None && Inject(Nat.Frame(mapping!, held.Packet))) HandedUp++;
            else PiecesAheadLost++;
        }
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

    private static (uint, uint, byte, ushort) PieceKey(ReadOnlySpan<byte> ip) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]), BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]), ip[9], BinaryPrimitives.ReadUInt16BigEndian(ip[4..]));

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
