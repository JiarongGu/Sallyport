using System.IO.Pipes;
using System.Net;
using System.Threading.Channels;
using Sallyport.Windows.PacketFilter;
using Sallyport.Windows.Routing;

namespace Sallyport.Windows.Service;

/// <summary>Why the client would not talk to what answered, or the service would not talk to the client.</summary>
public sealed class RouterRefused(string message) : Exception(message);

/// <summary>
/// The client's side of the router service: it checks the service is the one Windows runs as LocalSystem from the
/// client's own folder before saying a word (product design §2), prepares a run, and keeps the run's lease alive.
/// </summary>
public sealed class RouterClient : IAsyncDisposable
{
    private readonly FrameStream _control;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<RouterMessage> _replies = Channel.CreateUnbounded<RouterMessage>();
    private readonly Task _reading;
    private Task? _beating;

    private RouterClient(NamedPipeClientStream pipe, Welcome welcome)
    {
        _control = new FrameStream(pipe);
        Welcome = welcome;
        _reading = ReadAsync();
    }

    public Welcome Welcome { get; }

    /// <summary>Raised when the supervisor ends the run, and why.</summary>
    public event Action<RunEnded>? RunEnded;

    /// <summary>Opens the supervisor's pipe. <paramref name="folder"/>: where the service's executable must live — the
    /// client's own install folder.</summary>
    public static async Task<RouterClient> ConnectAsync(string folder, string build, TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", RouterProtocol.ControlPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout, ct);
            Verify(PipePeer.ServerOf(pipe), folder, expectService: true);
            var frames = new FrameStream(pipe);
            await frames.SendAsync(new Hello(RouterProtocol.Version, build), ct);
            var first = await frames.ReadAsync(ct);
            return first?.Message switch
            {
                Welcome welcome => new RouterClient(pipe, welcome),
                Rejected rejected => throw new RouterRefused(rejected.Reason),
                _ => throw new RouterRefused("the service answered with something this client does not understand"),
            };
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    // The server must be LocalSystem, from the expected folder, and — for the supervisor — the very process the service
    // manager says runs the service, so a process that merely took the name first is not believed.
    internal static void Verify(PipePeer server, string folder, bool expectService)
    {
        if (!server.IsSystem) throw new RouterRefused("what answered on the router's pipe is not running as the system");
        if (!server.RunsFrom(folder)) throw new RouterRefused("what answered on the router's pipe does not run from Sallyport's folder");
        if (expectService)
        {
            var (state, pid) = ServiceControl.Status(RouterProtocol.ServiceName);
            if (state != ServiceState.Running || pid != server.ProcessId)
                throw new RouterRefused("what answered on the router's pipe is not the Sallyport service");
        }
    }

    /// <summary>Asks for a run of these programs and attaches to it; the lease is kept from here on.</summary>
    public async Task<RouterRun> PrepareAsync(IReadOnlyList<string> executables, LossPolicy loss, string folder, CancellationToken ct = default)
    {
        await _control.SendAsync(new PrepareRun([.. executables], loss.Mode, loss.DirectAfter is { } after ? (int)after.TotalSeconds : null), ct);
        var reply = await _replies.Reader.ReadAsync(ct);
        if (reply is Rejected rejected) throw new RouterRefused(rejected.Reason);
        if (reply is not RunPrepared prepared) throw new RouterRefused("the service answered with something this client does not understand");
        _beating ??= BeatAsync();
        return await RouterRun.AttachAsync(prepared, folder, ct);
    }

    public ValueTask StopAsync(string runId) => _control.SendAsync(new StopRun(runId));

    private async Task ReadAsync()
    {
        try
        {
            while (await _control.ReadAsync(_stop.Token) is { } frame)
            {
                if (frame.Message is RunEnded ended) RunEnded?.Invoke(ended);
                else if (frame.Message is { } message) _replies.Writer.TryWrite(message);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        _replies.Writer.TryComplete();
    }

    private async Task BeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_stop.Token)) await _control.SendAsync(new Heartbeat(), _stop.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _stop.CancelAsync();
        await _control.DisposeAsync();
        await _reading;
        if (_beating is not null) await _beating;
        _stop.Dispose();
    }

    private int _disposed;
}

/// <summary>
/// One run, from the client's side: the tunnel's generations committed and retired, the packets a tunnel returns handed
/// in, and what the router routed for each generation handed out (<see cref="ToTunnel"/>).
/// </summary>
public sealed class RouterRun : IAsyncDisposable
{
    private readonly FrameStream _frames;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reading;

    private RouterRun(string runId, NamedPipeClientStream pipe, Attached attached)
    {
        RunId = runId;
        _frames = new FrameStream(pipe);
        Attached = attached;
        _reading = ReadAsync();
    }

    public string RunId { get; }
    public Attached Attached { get; }
    /// <summary>The last state the worker reported.</summary>
    public RouteState? State { get; private set; }

    /// <summary>What the router routed, by the generation it was routed for. Lossy at its bound: a reader too slow to keep
    /// up drops the oldest, as the tunnel's own queue does.</summary>
    public Channel<(int Generation, byte[] Packet)> ToTunnel { get; } =
        Channel.CreateBounded<(int, byte[])>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    public event Action<RouteState>? StateChanged;

    /// <summary>Completes when the worker's end closes: the run is over.</summary>
    public Task Completion => _reading;

    internal static async Task<RouterRun> AttachAsync(RunPrepared prepared, string folder, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", prepared.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), ct);
            RouterClient.Verify(PipePeer.ServerOf(pipe), folder, expectService: false);
            var frames = new FrameStream(pipe);
            await frames.SendAsync(new Attach(prepared.RunId), ct);
            return (await frames.ReadAsync(ct))?.Message switch
            {
                Attached attached => new RouterRun(prepared.RunId, pipe, attached),
                Rejected rejected => throw new RouterRefused(rejected.Reason),
                _ => throw new RouterRefused("the run answered with something this client does not understand"),
            };
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    public ValueTask CommitAsync(int generation, IPAddress address, int mtu) => _frames.SendAsync(new CommitGeneration(generation, address.ToString(), mtu));

    public ValueTask RetireAsync(int generation) => _frames.SendAsync(new RetireGeneration(generation));

    /// <summary>A packet a generation's tunnel returned.</summary>
    public ValueTask FromTunnelAsync(int generation, byte[] packet) => _frames.SendPacketAsync(generation, packet);

    private async Task ReadAsync()
    {
        try
        {
            while (await _frames.ReadAsync(_stop.Token) is { } frame)
            {
                if (frame.Kind == RouterProtocol.PacketFrame) ToTunnel.Writer.TryWrite((frame.Generation, frame.Packet));
                else if (frame.Message is RouteState state)
                {
                    State = state;
                    StateChanged?.Invoke(state);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or EndOfStreamException) { }
        ToTunnel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _stop.CancelAsync();
        await _frames.DisposeAsync();
        await _reading;
        _stop.Dispose();
    }

    private int _disposed;
}
