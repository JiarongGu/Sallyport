using System.ComponentModel;
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
            ServiceState state;
            int pid;
            (string Account, string Executable)? config;
            try
            {
                (state, pid) = ServiceControl.Status(RouterProtocol.ServiceName);
                config = ServiceControl.Config(RouterProtocol.ServiceName);
            }
            catch (Win32Exception e) { throw new RouterRefused($"Windows did not say what runs the Sallyport service (error {e.NativeErrorCode})"); }
            if (Distrust(PipePeer.ServerOf(pipe), folder, new ServiceRecord(state, pid, config?.Account, config?.Executable)) is { } why)
                throw new RouterRefused(why);
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

    /// <summary>What the service manager says of the router service: the process running it, and the account and
    /// executable it runs it as.</summary>
    internal sealed record ServiceRecord(ServiceState State, int ProcessId, string? Account, string? Executable);

    // The supervisor must be the very process the service manager says runs the service, so a process that merely took the
    // pipe's name first is not believed; and the service must be set to run as LocalSystem from the client's own folder.
    // The manager's record says so to the person, who cannot open the service's process itself: its DACL lets in only the
    // system and elevated administrators (read 2026-10-21), and the client runs as the person, not elevated.
    internal static string? Distrust(PipePeer server, string folder, ServiceRecord service) =>
        service.State != ServiceState.Running || service.ProcessId != server.ProcessId ? "what answered on the router's pipe is not the Sallyport service"
        : !IsLocalSystem(service.Account) ? "the Sallyport service is not set to run as the system"
        : service.Executable is null || !new PipePeer(0, 0, null, service.Executable).RunsFrom(folder) ? "the Sallyport service does not run from Sallyport's folder"
        : Disagrees(server, folder);

    // A run's worker must be the process the supervisor, believed already, says it started for this run.
    internal static string? Distrust(PipePeer server, string folder, int worker) =>
        server.ProcessId != worker ? "what answered on the run's pipe is not the router the service started" : Disagrees(server, folder);

    // Where the client can read the process itself (run elevated), what Windows says of it must agree; where it cannot,
    // that is not a refusal — the manager has answered for it.
    private static string? Disagrees(PipePeer server, string folder) =>
        server.User is not null && !server.IsSystem ? "what answered on the router's pipe is not running as the system"
        : server.Image is not null && !server.RunsFrom(folder) ? "what answered on the router's pipe does not run from Sallyport's folder"
        : null;

    private static bool IsLocalSystem(string? account) =>
        string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase) || string.Equals(account, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks for a run of these programs, all under one loss policy, and attaches to it.</summary>
    public Task<RouterRun> PrepareAsync(IReadOnlyList<string> executables, LossPolicy loss, string folder, CancellationToken ct = default) =>
        PrepareAsync([.. executables.Select(e => new ProgramRule(e, loss.Mode, loss.DirectAfter is { } after ? (int)after.TotalSeconds : null))], folder, ct);

    /// <summary>Asks for a run of these programs and attaches to it; the lease is kept from here on.</summary>
    public async Task<RouterRun> PrepareAsync(IReadOnlyList<ProgramRule> programs, string folder, CancellationToken ct = default)
    {
        await _control.SendAsync(new PrepareRun([.. programs]), ct);
        var reply = await _replies.Reader.ReadAsync(ct);
        if (reply is Rejected rejected) throw new RouterRefused(rejected.Reason);
        if (reply is not RunPrepared prepared) throw new RouterRefused("the service answered with something this client does not understand");
        _beating ??= BeatAsync();
        try { return await RouterRun.AttachAsync(prepared, folder, ct); }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The worker gone before it was attached: the supervisor says why on its own pipe, a moment later — the reason
            // to give, not the broken pipe it left.
            try { throw new RouterRefused((await Ending(prepared.RunId).Task.WaitAsync(TimeSpan.FromSeconds(2), ct)).Reason); }
            catch (TimeoutException) { throw new RouterRefused("the router stopped before it could be reached"); }
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<RunEnded>> _endings = new();

    private TaskCompletionSource<RunEnded> Ending(string runId) =>
        _endings.GetOrAdd(runId, _ => new TaskCompletionSource<RunEnded>(TaskCreationOptions.RunContinuationsAsynchronously));

    public ValueTask StopAsync(string runId) => _control.SendAsync(new StopRun(runId));

    private async Task ReadAsync()
    {
        try
        {
            while (await _control.ReadAsync(_stop.Token) is { } frame)
            {
                if (frame.Message is RunEnded ended)
                {
                    Ending(ended.RunId).TrySetResult(ended);
                    RunEnded?.Invoke(ended);
                }
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
        _writing = Task.Run(async () =>
        {
            try { await _frames.PumpPacketsAsync(_fromTunnel.Reader, _stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        });
    }

    // What the tunnels returned, on its way to the worker; lossy at its bound like every queue on the packet path.
    private readonly Channel<(int, byte[])> _fromTunnel =
        Channel.CreateBounded<(int, byte[])>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _writing;

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
            if (RouterClient.Distrust(PipePeer.ServerOf(pipe), folder, prepared.Worker) is { } why) throw new RouterRefused(why);
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

    /// <summary>Routes these programs from now on instead of the run's own (<see cref="SelectPrograms"/>).</summary>
    public ValueTask SelectAsync(IReadOnlyList<ProgramRule> programs) => _frames.SendAsync(new SelectPrograms([.. programs]));

    /// <summary>A packet a generation's tunnel returned: queued for the worker, never waited on.</summary>
    public void FromTunnel(int generation, byte[] packet) => _fromTunnel.Writer.TryWrite((generation, packet));

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
        _fromTunnel.Writer.TryComplete();
        await _frames.DisposeAsync();
        await _reading;
        await _writing;
        _stop.Dispose();
    }

    private int _disposed;
}
