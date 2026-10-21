using Sallyport.Windows.PacketFilter;

namespace Sallyport.Windows.Routing;

/// <summary>What a game route does with the game's traffic while its tunnel is lost (docs/design/product.md §3):
/// <see cref="Hold"/> drops it, so nothing leaves outside the tunnel; <see cref="Direct"/> lets it go out directly until
/// the tunnel is back; <see cref="Stop"/> ends the route, the game direct from then on.</summary>
public enum LossMode { Hold, Direct, Stop }

/// <summary>A game profile's loss policy: its mode, and under <see cref="LossMode.Hold"/> how long an outage may last
/// before the game goes direct after all (null: never).</summary>
public sealed record LossPolicy(LossMode Mode = LossMode.Hold, TimeSpan? DirectAfter = null);

/// <summary><see cref="Armed"/>: waiting for the first tunnel, the game's traffic held. <see cref="Held"/> and
/// <see cref="Direct"/>: a tunnel lost, by the loss policy. <see cref="Stopped"/>: the route is over and the game direct.</summary>
public enum RoutePhase { Armed, Routing, Held, Direct, Stopped }

/// <summary>
/// The route across its tunnels' generations: the router on the adapter, and for each lane — one per profile whose apps
/// it carries (D24) — the generation whose tunnel takes that lane's traffic now, and the loss policy for the gaps between
/// two. Whatever holds a lane's tunnel calls <see cref="Commit"/> when one is up and <see cref="Retire"/> when it goes, and
/// hands over what each returns; a packet of any generation but its lane's current one is refused — a late reply of a
/// tunnel already gone. A generation on the wire says its lane in its top byte (<see cref="Wire"/>): lane 0's are the
/// session's own numbers.
/// </summary>
public sealed class GameRoute : IDisposable
{
    private readonly LossPolicy _loss;
    private readonly Func<int, LossPolicy?>? _lossOf;
    private readonly Action<int, byte[]> _toTunnel;
    private readonly Lock _lock = new();
    private readonly Lane[] _lanes = [.. Enumerable.Range(0, Router.Lanes).Select(_ => new Lane())];

    private sealed class Lane
    {
        public volatile int Generation;
        public long DownAt;
        public Timer? Countdown;
        // Which outage the countdown is of: a callback already queued when its outage ended must not end the next.
        public int Outage;
        public RoutePhase Phase = RoutePhase.Armed;
        public DateTimeOffset? DownSince;
    }

    /// <summary>A lane's generation as the pipe carries it: the lane in the top byte, the session's generation below.</summary>
    public static int Wire(int lane, int generation) => lane << 24 | generation & 0xFFFFFF;

    public static (int Lane, int Generation) FromWire(int wire) => ((wire >> 24) & 0x7F, wire & 0xFFFFFF);

    /// <param name="toTunnel">Where a routed packet goes: the generation it was routed for (<see cref="Wire"/>), and the packet.</param>
    /// <param name="lossOf">Each selected process's own loss policy, when its program's differs from the route's
    /// (several apps through one route, D23): while a lane's tunnel is lost it holds, and a process whose policy says so
    /// goes direct — at once, or once its program's wait is over. Under it the route never stops on a loss.</param>
    /// <param name="laneOf">Each selected process's lane: its app's profile (D24). Null: one lane.</param>
    public GameRoute(PacketDriver driver, DriverAdapter adapter, Func<int, bool> selects, LossPolicy loss, Action<int, byte[]> toTunnel,
        IEnumerable<(uint Network, uint Mask)>? onLink = null, Func<DriverPacket, bool>? discard = null, bool receivePath = false,
        Func<int, LossPolicy?>? lossOf = null, Func<int, int>? laneOf = null)
    {
        _loss = loss;
        _lossOf = lossOf;
        _toTunnel = toTunnel;
        // Into the generation whose NAT turned the packet (Nat.Generation is its wire number), not whichever is current
        // by the time it is sent.
        Router = new Router(driver, adapter.Handle, selects, (nat, packet) => _toTunnel(nat.Generation, packet), onLink, discard, receivePath)
        {
            LaneOf = laneOf,
        };
        if (lossOf is not null) Router.DirectWhileHeld = DirectWhileHeld;
    }

    // Only after its own lane's loss: before the first tunnel everything selected is held, whatever its policy says of a
    // tunnel lost.
    private bool DirectWhileHeld(int pid)
    {
        var lane = _lanes[Math.Clamp(Router.LaneOf?.Invoke(pid) ?? 0, 0, Router.Lanes - 1)];
        var downAt = Volatile.Read(ref lane.DownAt);
        return downAt != 0 && _lossOf!(pid) switch
        {
            { Mode: LossMode.Direct or LossMode.Stop } => true,
            { DirectAfter: { } after } => Environment.TickCount64 - downAt >= after.TotalMilliseconds,
            _ => false,
        };
    }

    public Router Router { get; }

    /// <summary>The first lane's phase, as a route of one profile has it.</summary>
    public RoutePhase Phase => PhaseOf(0);

    public RoutePhase PhaseOf(int lane)
    {
        lock (_lock) return _lanes[lane].Phase;
    }

    /// <summary>The first lane's generation whose tunnel takes its traffic; 0 while none does.</summary>
    public int Generation => _lanes[0].Generation;

    public int GenerationOf(int lane) => _lanes[lane].Generation;

    /// <summary>Packets a tunnel returned for a generation that is not its lane's current one.</summary>
    public long Stale { get; private set; }

    /// <summary>When the first lane's current outage began; null while a tunnel is up.</summary>
    public DateTimeOffset? DownSince
    {
        get { lock (_lock) return _lanes[0].DownSince; }
    }

    /// <summary>Raised on every change of a lane's phase.</summary>
    public event Action<int, RoutePhase>? LanePhaseChanged;

    /// <summary>Raised on every change of the first lane's phase.</summary>
    public event Action<RoutePhase>? PhaseChanged;

    /// <summary>Drains and decides until <paramref name="ct"/> is cancelled, then gives the adapter back.</summary>
    public void Run(CancellationToken ct) => Router.Run(ct);

    /// <summary>A tunnel is up: its generation (<see cref="Wire"/>) takes its lane's traffic from the next packet, through
    /// a NAT of its own.</summary>
    public void Commit(int wire, uint tunnelAddress, int mtu, ushort mss = Nat.DefaultMss)
    {
        var (index, generation) = FromWire(wire);
        if (index >= Router.Lanes) return;
        var lane = _lanes[index];
        lock (_lock)
        {
            if (lane.Phase == RoutePhase.Stopped) return;
            lane.Countdown?.Dispose();
            lane.Countdown = null;
            lane.Generation = generation;
            Router.Commit(index, new Nat(tunnelAddress, mss, mtu) { Generation = Wire(index, generation) });
            lane.DownSince = null;
            Volatile.Write(ref lane.DownAt, 0);
            Enter(index, RoutePhase.Routing);
        }
    }

    /// <summary>A generation's tunnel has gone: its lane's traffic is held, sent direct or the lane stopped, as the loss
    /// policy says. A generation already replaced is ignored.</summary>
    public void Retire(int wire)
    {
        var (index, generation) = FromWire(wire);
        if (index >= Router.Lanes) return;
        var lane = _lanes[index];
        lock (_lock)
        {
            if (generation != lane.Generation || lane.Phase == RoutePhase.Stopped) return;
            lane.Generation = 0;
            lane.DownSince = DateTimeOffset.UtcNow;
            Volatile.Write(ref lane.DownAt, Environment.TickCount64);
            if (_lossOf is not null)
            {
                Router.Retire(index, direct: false);
                Enter(index, RoutePhase.Held);
                return;
            }
            switch (_loss.Mode)
            {
                case LossMode.Stop:
                    Router.Retire(index, direct: true);
                    Enter(index, RoutePhase.Stopped);
                    break;
                case LossMode.Direct:
                    Router.Retire(index, direct: true);
                    Enter(index, RoutePhase.Direct);
                    break;
                default:
                    Router.Retire(index, direct: false);
                    Enter(index, RoutePhase.Held);
                    // Once per outage: failed attempts do not restart it, only a tunnel up again does.
                    var outage = ++lane.Outage;
                    if (_loss.DirectAfter is { } after) lane.Countdown = new Timer(_ => GoDirect(index, outage), null, after, Timeout.InfiniteTimeSpan);
                    break;
            }
        }
    }

    /// <summary>The route is over: every lane's traffic goes direct, and no tunnel takes it again.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            for (var index = 0; index < Router.Lanes; index++)
            {
                var lane = _lanes[index];
                lane.Countdown?.Dispose();
                lane.Countdown = null;
                lane.Generation = 0;
                Router.Retire(index, direct: true);
                Enter(index, RoutePhase.Stopped);
            }
        }
    }

    /// <summary>A packet a generation's tunnel returned (<see cref="Wire"/>).</summary>
    public void FromTunnel(int wire, byte[] packet)
    {
        var (index, generation) = FromWire(wire);
        if (index >= Router.Lanes || generation != _lanes[index].Generation || generation == 0)
        {
            Stale++;
            return;
        }
        Router.FromTunnel(index, packet);
    }

    private void GoDirect(int index, int outage)
    {
        lock (_lock)
        {
            if (_lanes[index].Phase != RoutePhase.Held || _lanes[index].Outage != outage) return;
            Router.Retire(index, direct: true);
            Enter(index, RoutePhase.Direct);
        }
    }

    private void Enter(int index, RoutePhase phase)
    {
        var lane = _lanes[index];
        if (lane.Phase == phase) return;
        lane.Phase = phase;
        LanePhaseChanged?.Invoke(index, phase);
        if (index == 0) PhaseChanged?.Invoke(phase);
    }

    public void Dispose()
    {
        foreach (var lane in _lanes) lane.Countdown?.Dispose();
        Router.Dispose();
    }
}
