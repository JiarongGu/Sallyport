using System.Net.Sockets;
using Sallyport;

namespace Sallyport.Windows.Service;

/// <summary>
/// One tunnel generation's side of a run through the router service, as a <see cref="Runtime.TunnelSession"/> consumer:
/// it commits the generation over the run's pipe before anything of it crosses, hands the run what the tunnel returns,
/// and retires the generation when the session lets the tunnel go. What the router routed for this generation is sent
/// into its tunnel by whoever reads <see cref="RouterRun.ToTunnel"/> (<see cref="ForwardAsync"/>).
/// </summary>
public sealed class RunPump : IAsyncDisposable
{
    private readonly RouterRun _run;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiving;

    /// <summary>The generation is committed with the largest packet its tunnel carries whole: the router's adapter's
    /// MTU less the tunnel's own overhead (<see cref="Tunnel.Overhead"/>).</summary>
    public RunPump(Tunnel tunnel, int generation, RouterRun run)
    {
        Tunnel = tunnel;
        Generation = generation;
        _run = run;
        var committed = tunnel.Config.Address is { AddressFamily: AddressFamily.InterNetwork } address
            ? run.CommitAsync(generation, address, run.Attached.AdapterMtu - tunnel.Overhead).AsTask()
            : Task.CompletedTask;
        _receiving = ReceiveAsync(committed);
    }

    public Tunnel Tunnel { get; }
    public int Generation { get; }

    /// <summary>Sends what the router routed into the tunnel of the generation it was routed for, and nowhere else,
    /// until the run ends. <paramref name="current"/> is the pump of the tunnel up now, or null between two.</summary>
    public static async Task ForwardAsync(RouterRun run, Func<RunPump?> current)
    {
        await foreach (var (generation, packet) in run.ToTunnel.Reader.ReadAllAsync())
            if (current() is { } pump && pump.Generation == generation)
                try { await pump.Tunnel.SendAsync(packet); }
                catch (Exception e) when (e is TunnelEnded or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task ReceiveAsync(Task committed)
    {
        try
        {
            await committed;
            await foreach (var packet in Tunnel.Packets.ReadAllAsync(_stop.Token)) _run.FromTunnel(Generation, packet);
        }
        // The tunnel's packets end with what ended it; the session says why.
        catch (Exception e) when (e is OperationCanceledException or TunnelEnded or IOException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _run.RetireAsync(Generation); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        await _stop.CancelAsync();
        await _receiving;
        _stop.Dispose();
    }
}
