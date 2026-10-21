using System.Diagnostics;
using Sallyport.Windows.PacketFilter;

namespace Sallyport.Windows.Routing;

/// <summary>What the router counts and remembers for whoever measures or shows it: written by the router's own thread
/// unless a member's comment says otherwise, and read from any — the collections each through its own lock.</summary>
public sealed partial class Router
{
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

    /// <summary>Routed flows and packets by process, from the start of the route: what a window says per program. Safe to
    /// read while routing, unlike <see cref="Flows"/>.</summary>
    public IReadOnlyDictionary<int, ProcessRouted> ByProcess => _byProcess;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ProcessRouted> _byProcess = new();
    public IReadOnlyDictionary<Flow6, FlowDecision> Flows6 => _flows6;

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
}
