using System.Net.Sockets;
using Sallyport;
using Sallyport.Net;

namespace Sallyport.Windows.Service;

/// <summary>
/// One tunnel generation's side of a run through the router service, as the router side of that generation's
/// <see cref="TunnelDispatcher"/>: it commits the generation over the run's pipe before anything of it crosses, then
/// takes from the dispatcher what is not the proxy's and hands it to the run, and retires the generation when it is let
/// go — with the tunnel, or alone when routing stops and the channel stays. What the router routed for this generation is
/// sent into its tunnel by whoever reads <see cref="RouterRun.ToTunnel"/> (<see cref="ForwardAsync"/>).
/// </summary>
public sealed class RunPump : ITunnelRoute
{
    private readonly RouterRun _run;
    private readonly TunnelDispatcher _dispatcher;
    private readonly Task _committed;
    private int _disposed;

    /// <summary>The generation is committed with the largest packet its tunnel carries whole: the router's adapter's
    /// MTU less the tunnel's own overhead (<see cref="Tunnel.Overhead"/>).</summary>
    public RunPump(TunnelDispatcher dispatcher, RouterRun run)
    {
        _dispatcher = dispatcher;
        Tunnel = dispatcher.Tunnel;
        Generation = dispatcher.Generation;
        _run = run;
        _committed = Tunnel.Config.Address is { AddressFamily: AddressFamily.InterNetwork } address
            ? run.CommitAsync(Generation, address, run.Attached.AdapterMtu - Tunnel.Overhead).AsTask()
            : Task.CompletedTask;
        // Attached at once, so a generation that goes before it is committed still lets this go (and retires it). What
        // the tunnel returns before the commit lands is for a generation the router does not have yet, and it drops it.
        dispatcher.Attach(this);
    }

    public Tunnel Tunnel { get; }
    public int Generation { get; }

    void ITunnelRoute.FromTunnel(byte[] packet) => _run.FromTunnel(Generation, packet);

    /// <summary>Sends what the router routed into the tunnel of the generation it was routed for, and nowhere else,
    /// until the run ends. <paramref name="current"/> is the pump of the tunnel up now, or null between two.</summary>
    public static async Task ForwardAsync(RouterRun run, Func<RunPump?> current)
    {
        await foreach (var (generation, packet) in run.ToTunnel.Reader.ReadAllAsync())
            if (current() is { } pump && pump.Generation == generation)
                try { await pump.Tunnel.SendAsync(packet); }
                catch (Exception e) when (e is TunnelEnded or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>Detaches from the dispatcher and retires the generation in the run: once, whether the generation went
    /// with its tunnel or routing stopped while the channel stays.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispatcher.Detach(this);
        // Retired after its commit, never before: the router must not see the two the other way round.
        try { await _committed; }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        try { await _run.RetireAsync(Generation); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
