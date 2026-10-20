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
/// One game's route across its tunnel's generations: the router on the adapter, the generation whose tunnel takes the
/// game's traffic now, and the loss policy for the gaps between two. Whatever holds the tunnel calls
/// <see cref="Commit"/> when one is up and <see cref="Retire"/> when it goes, and hands over what each returns; a packet
/// of any generation but the current one is refused — a late reply of a tunnel already gone.
/// </summary>
public sealed class GameRoute : IDisposable
{
    private readonly LossPolicy _loss;
    private readonly Func<int, LossPolicy?>? _lossOf;
    private readonly Action<int, byte[]> _toTunnel;
    private readonly Lock _lock = new();
    private Timer? _countdown;
    private volatile int _generation;
    private long _downAt;

    /// <param name="toTunnel">Where a routed packet goes: the generation it was routed for, and the packet.</param>
    /// <param name="lossOf">Each selected process's own loss policy, when its program's differs from the route's
    /// (several apps through one route, D23): while the tunnel is lost the route holds, and a process whose policy says
    /// so goes direct — at once, or once its program's wait is over. Under it the route never stops on a loss.</param>
    public GameRoute(PacketDriver driver, DriverAdapter adapter, Func<int, bool> selects, LossPolicy loss, Action<int, byte[]> toTunnel,
        IEnumerable<(uint Network, uint Mask)>? onLink = null, Func<DriverPacket, bool>? discard = null, bool receivePath = false,
        Func<int, LossPolicy?>? lossOf = null)
    {
        _loss = loss;
        _lossOf = lossOf;
        _toTunnel = toTunnel;
        Router = new Router(driver, adapter.Handle, selects, packet => _toTunnel(_generation, packet), onLink, discard, receivePath);
        if (lossOf is not null) Router.DirectWhileHeld = DirectWhileHeld;
    }

    // Only after a loss: before the first tunnel everything selected is held, whatever its policy says of a tunnel lost.
    private bool DirectWhileHeld(int pid) => Volatile.Read(ref _downAt) != 0 && _lossOf!(pid) switch
    {
        { Mode: LossMode.Direct or LossMode.Stop } => true,
        { DirectAfter: { } after } => Environment.TickCount64 - Volatile.Read(ref _downAt) >= after.TotalMilliseconds,
        _ => false,
    };

    public Router Router { get; }
    public RoutePhase Phase { get; private set; } = RoutePhase.Armed;
    /// <summary>The generation whose tunnel takes the game's traffic; 0 while none does.</summary>
    public int Generation => _generation;
    /// <summary>Packets the tunnel returned for a generation that is not the current one.</summary>
    public long Stale { get; private set; }
    /// <summary>When the current outage began; null while a tunnel is up.</summary>
    public DateTimeOffset? DownSince { get; private set; }

    /// <summary>Raised on every change of phase.</summary>
    public event Action<RoutePhase>? PhaseChanged;

    /// <summary>Drains and decides until <paramref name="ct"/> is cancelled, then gives the adapter back.</summary>
    public void Run(CancellationToken ct) => Router.Run(ct);

    /// <summary>A tunnel is up: its generation takes the game's traffic from the next packet, through a NAT of its own.</summary>
    public void Commit(int generation, uint tunnelAddress, int mtu, ushort mss = Nat.DefaultMss)
    {
        lock (_lock)
        {
            if (Phase == RoutePhase.Stopped) return;
            _countdown?.Dispose();
            _countdown = null;
            _generation = generation;
            Router.Commit(new Nat(tunnelAddress, mss, mtu));
            DownSince = null;
            Volatile.Write(ref _downAt, 0);
            Enter(RoutePhase.Routing);
        }
    }

    /// <summary>A generation's tunnel has gone: the game's traffic is held, sent direct or the route stopped, as the
    /// loss policy says. A generation already replaced is ignored.</summary>
    public void Retire(int generation)
    {
        lock (_lock)
        {
            if (generation != _generation || Phase == RoutePhase.Stopped) return;
            _generation = 0;
            DownSince = DateTimeOffset.UtcNow;
            Volatile.Write(ref _downAt, Environment.TickCount64);
            if (_lossOf is not null)
            {
                Router.Retire(direct: false);
                Enter(RoutePhase.Held);
                return;
            }
            switch (_loss.Mode)
            {
                case LossMode.Stop:
                    Router.Retire(direct: true);
                    Enter(RoutePhase.Stopped);
                    break;
                case LossMode.Direct:
                    Router.Retire(direct: true);
                    Enter(RoutePhase.Direct);
                    break;
                default:
                    Router.Retire(direct: false);
                    Enter(RoutePhase.Held);
                    // Once per outage: failed attempts do not restart it, only a tunnel up again does.
                    if (_loss.DirectAfter is { } after) _countdown = new Timer(_ => GoDirect(), null, after, Timeout.InfiniteTimeSpan);
                    break;
            }
        }
    }

    /// <summary>The route is over: the game's traffic goes direct, and no tunnel takes it again.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _countdown?.Dispose();
            _countdown = null;
            _generation = 0;
            Router.Retire(direct: true);
            Enter(RoutePhase.Stopped);
        }
    }

    /// <summary>A packet a generation's tunnel returned.</summary>
    public void FromTunnel(int generation, byte[] packet)
    {
        if (generation != _generation)
        {
            Stale++;
            return;
        }
        Router.FromTunnel(packet);
    }

    private void GoDirect()
    {
        lock (_lock)
        {
            if (Phase != RoutePhase.Held) return;
            Router.Retire(direct: true);
            Enter(RoutePhase.Direct);
        }
    }

    private void Enter(RoutePhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        PhaseChanged?.Invoke(phase);
    }

    public void Dispose()
    {
        _countdown?.Dispose();
        Router.Dispose();
    }
}
