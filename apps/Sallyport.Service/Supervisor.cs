using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sallyport.Windows.PacketFilter;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

/// <summary>
/// The service's supervisor (docs/design/product.md §1): it answers the control pipe, lets only Sallyport's own client
/// in an interactive session prepare a run, starts each run's worker in a job object that dies with it, keeps the run's
/// lease, and guards the direct path (D13) from outside the worker that intercepts. One run on the machine at a time.
/// </summary>
sealed class Supervisor(Log log)
{
    private static readonly IPEndPoint[] Probes = [new(IPAddress.Parse("1.1.1.1"), 443), new(IPAddress.Parse("8.8.8.8"), 443)];
    /// <summary>A worker silent this long, or whose router has not drained this long, is taken to have hung: while it
    /// hangs, everything Windows sends on its adapter waits in the driver, the whole machine's traffic included
    /// (measured, step 3) — so this is inside the guard's 3 s. 1 s ended a run on a machine merely starved for a moment
    /// (step 4: a worker that took 5 s to start, then stood still just over 1 s).</summary>
    private static readonly TimeSpan WorkerSilence = TimeSpan.FromSeconds(2), RouterStill = TimeSpan.FromSeconds(2);
    /// <summary>The direct path dead this long, with a run intercepting, ends the run (the tool's guard, measured).</summary>
    private static readonly TimeSpan DeadPath = TimeSpan.FromSeconds(3);

    private readonly string _folder = AppContext.BaseDirectory;
    private readonly string _build = typeof(Supervisor).Assembly.GetName().Version?.ToString() ?? "0";
    private readonly Lock _lock = new();
    private readonly JobObject _job = new();
    private Run? _run;
    // A run being prepared holds the machine's one place too: two clients asking at once get one run between them.
    private bool _preparing;

    private sealed class Run(string id, Process worker, FrameStream owner, int ownerPid, ulong adapter)
    {
        public string Id { get; } = id;
        public Process Worker { get; } = worker;
        public FrameStream Owner { get; } = owner;
        public int OwnerPid { get; } = ownerPid;
        /// <summary>The adapter the worker intercepts on: the one the supervisor resets when the run ends.</summary>
        public ulong Adapter { get; } = adapter;
        public long LastBeat = Environment.TickCount64;
        public long LastWorkerBeat = Environment.TickCount64;
        public long Drains = -1;
        public long LastDrain = Environment.TickCount64;
        public bool Ended;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        log.Say($"supervisor {_build} from {_folder}");
        // Whatever a supervisor before this one left intercepting is let go before anything is accepted.
        ResetAdapters("start");
        var accepting = AcceptAsync(ct);
        var watching = WatchAsync(ct);
        try { await Task.WhenAll(accepting, watching); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        lock (_lock) End("the service is stopping");
        _job.Dispose();
        log.Say("supervisor stopped");
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // The first instance is created with FILE_FLAG_FIRST_PIPE_INSTANCE: if anything already holds the name,
                // this fails rather than joining it.
                pipe = NamedPipeServerStreamAcl.Create(RouterProtocol.ControlPipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0), 65536, 65536, PipePeer.Security());
            }
            catch (UnauthorizedAccessException) when (first)
            {
                log.Say("the control pipe's name is already held by another process: refusing to serve");
                throw;
            }
            first = false;
            try { await pipe.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                throw;
            }
            _ = Task.Run(() => ServeAsync(pipe, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var frames = new FrameStream(pipe);
        PipePeer peer;
        try { peer = PipePeer.ClientOf(pipe); }
        catch (Win32Exception) { return; }
        if (Refusal(peer) is { } why)
        {
            log.Say($"refused pid {peer.ProcessId} (session {peer.SessionId}, {peer.Image ?? "unknown image"}): {why}");
            await TrySend(frames, new Rejected(why));
            return;
        }
        try
        {
            var hello = (await frames.ReadAsync(ct))?.Message as Hello;
            if (hello is null || hello.Version != RouterProtocol.Version)
            {
                await frames.SendAsync(new Rejected($"this service speaks version {RouterProtocol.Version}; update Sallyport"), ct);
                return;
            }
            var (ready, problem) = Driver();
            string? current;
            lock (_lock) current = _run?.Id;
            await frames.SendAsync(new Welcome(RouterProtocol.Version, _build, ready, problem, current), ct);
            log.Say($"client pid {peer.ProcessId} in session {peer.SessionId} connected");

            while (await frames.ReadAsync(ct) is { } frame)
                switch (frame.Message)
                {
                    case PrepareRun prepare:
                        await frames.SendAsync(await PrepareAsync(prepare, peer, frames), ct);
                        break;
                    case Heartbeat:
                        lock (_lock)
                            if (_run is { } run && run.Owner == frames) run.LastBeat = Environment.TickCount64;
                        break;
                    case StopRun stop:
                        lock (_lock)
                            if (_run is { } run && run.Owner == frames && run.Id == stop.RunId) End("stopped by the client");
                        break;
                    default:
                        await frames.SendAsync(new Rejected("not understood"), ct);
                        break;
                }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidDataException or EndOfStreamException) { }
        finally
        {
            lock (_lock)
                if (_run is { } run && run.Owner == frames) End("the client went away");
            log.Say($"client pid {peer.ProcessId} disconnected");
        }
    }

    // Only Sallyport's own client may ask, from a person's session: the folder it runs from is one only administrators
    // can write (product design change 2), and the system's session 0 is no person's.
    private string? Refusal(PipePeer peer) =>
        peer.Image is null || peer.User is null ? "its process could not be read"
        : peer.SessionId == 0 ? "it is not in a person's session"
        : peer.IsSystem ? "it runs as the system"
        : !peer.RunsFrom(_folder) ? "it is not Sallyport's own client"
        : null;

    private async Task<RouterMessage> PrepareAsync(PrepareRun prepare, PipePeer peer, FrameStream owner)
    {
        lock (_lock)
        {
            if (_run is not null || _preparing) return new Rejected("Game routing is in use already — by another window or another person on this computer.");
            _preparing = true;
        }
        try { return await StartAsync(prepare, peer, owner); }
        finally { lock (_lock) _preparing = false; }
    }

    private async Task<RouterMessage> StartAsync(PrepareRun prepare, PipePeer peer, FrameStream owner)
    {
        var programs = RouterProtocol.Routable(prepare.Programs ?? [], _folder);
        if (programs.Length == 0) return new Rejected("There is no program to route.");
        if (Driver() is (false, var problem)) return new Rejected(problem ?? "The packet driver is not ready.");

        var id = Guid.NewGuid().ToString("N");
        var worker = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["worker", id, peer.ProcessId.ToString()])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        _job.Add(worker);
        await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new WorkerSetup(programs), RouterProtocol.Json));
        await worker.StandardInput.FlushAsync();

        // The worker says "ready" once its pipe exists, or why it could not start.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string? line;
        try
        {
            while ((line = await worker.StandardOutput.ReadLineAsync(limit.Token)) is not null && line.StartsWith("log ", StringComparison.Ordinal))
                log.Say($"worker {worker.Id}: {line[4..]}");
        }
        catch (OperationCanceledException) { line = null; }
        var adapter = 0UL;
        if (line?.StartsWith("ready ", StringComparison.Ordinal) != true || !ulong.TryParse(line[6..], System.Globalization.NumberStyles.HexNumber, null, out adapter))
        {
            Kill(worker);
            var why = line?.StartsWith("error ", StringComparison.Ordinal) == true ? line[6..] : "The router did not start.";
            log.Say($"run {id} refused: {why}");
            return new Rejected(why);
        }
        var run = new Run(id, worker, owner, peer.ProcessId, adapter);
        lock (_lock) _run = run;
        _ = Task.Run(() => FollowAsync(run));
        log.Say($"run {id}: worker {worker.Id} for client pid {peer.ProcessId}, {programs.Length} program(s), loss {Losses(programs)}");
        return new RunPrepared(id, RouterProtocol.RunPipe(id), worker.Id);
    }

    // The worker's lines after "ready": "beat" and the router's drains, "log ..." to be written here.
    private async Task FollowAsync(Run run)
    {
        try
        {
            while (await run.Worker.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("beat ", StringComparison.Ordinal) && long.TryParse(line[5..], out var drains))
                {
                    var now = Environment.TickCount64;
                    Interlocked.Exchange(ref run.LastWorkerBeat, now);
                    if (Interlocked.Exchange(ref run.Drains, drains) != drains) Interlocked.Exchange(ref run.LastDrain, now);
                }
                else if (line.StartsWith("log ", StringComparison.Ordinal)) log.Say($"worker {run.Worker.Id}: {line[4..]}");
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        lock (_lock)
            if (_run == run) End($"the router stopped (exit {ExitCode(run.Worker)})");
    }

    // Twice a second: the client's lease, the worker's life, and — while a run intercepts — the direct path.
    private async Task WatchAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        long? deadSince = null;
        while (await timer.WaitForNextTickAsync(ct))
        {
            Run? run;
            lock (_lock) run = _run;
            if (run is null) { deadSince = null; continue; }
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref run.LastBeat) > RouterProtocol.Lease.TotalMilliseconds)
            {
                lock (_lock) if (_run == run) End("the client's lease ran out");
                continue;
            }
            if (now - Interlocked.Read(ref run.LastWorkerBeat) > WorkerSilence.TotalMilliseconds)
            {
                lock (_lock) if (_run == run) End("the router stopped answering");
                continue;
            }
            if (now - Interlocked.Read(ref run.LastDrain) > RouterStill.TotalMilliseconds)
            {
                lock (_lock) if (_run == run) End("the router stopped moving packets");
                continue;
            }
            var alive = (await Task.WhenAll(Probes.Select(ProbeAsync))).Any(ok => ok);
            if (alive) { deadSince = null; continue; }
            deadSince ??= now;
            if (now - deadSince.Value >= DeadPath.TotalMilliseconds)
            {
                lock (_lock) if (_run == run) End("the network stopped answering, so routing stopped to let it recover");
                ResetAdapters("guard");
                deadSince = null;
            }
        }
    }

    private static async Task<bool> ProbeAsync(IPEndPoint target)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await socket.ConnectAsync(target, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return false; }
    }

    // Under _lock. The worker is killed, and its adapter reset through a handle of this process's own: the driver gives an
    // adapter back by itself only when the last handle to it closes (gate 1 had no other open; step 3, with one open,
    // measured 4.2 s of the machine's traffic held in the driver until something reset it). The owner is told why.
    private void End(string why)
    {
        if (_run is not { } run || run.Ended) return;
        run.Ended = true;
        _run = null;
        Kill(run.Worker);
        try { run.Worker.WaitForExit(2000); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        ResetAdapter(run.Adapter, "end");
        log.Say($"run {run.Id} ended: {why}");
        _ = TrySend(run.Owner, new RunEnded(run.Id, why));
    }

    private void ResetAdapter(ulong adapter, string why)
    {
        try
        {
            using var driver = PacketDriver.Open();
            var before = driver.Mode(adapter);
            if (before != AdapterMode.None) driver.SetMode(adapter, AdapterMode.None);
            log.Say($"{why}: adapter 0x{adapter:X} {before} -> {driver.Mode(adapter)}, {driver.QueueSize(adapter)} queued");
        }
        catch (Win32Exception e) { log.Say($"{why}: adapter 0x{adapter:X} not reset (Windows error {e.NativeErrorCode})"); }
    }

    /// <summary>The loss policies of a run's programs, for the log: "hold", or "hold 2, direct 1".</summary>
    internal static string Losses(IReadOnlyCollection<ProgramRule> programs)
    {
        var kinds = programs.GroupBy(p => (p.DirectAfterSeconds is { } s ? $"{p.Loss} then direct after {s} s" : p.Loss.ToString()).ToLowerInvariant()).ToList();
        return kinds.Count == 1 ? kinds[0].Key : string.Join(", ", kinds.Select(k => $"{k.Key} {k.Count()}"));
    }

    private static void Kill(Process worker)
    {
        try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    private static string ExitCode(Process worker)
    {
        try { return worker.HasExited ? worker.ExitCode.ToString() : "still running"; }
        catch (InvalidOperationException) { return "unknown"; }
    }

    // With its own handle, every adapter back to no interception: the second remedy, after killing the worker (gate 1
    // measured a second handle resetting a first's mode).
    private void ResetAdapters(string why)
    {
        try
        {
            using var driver = PacketDriver.Open();
            foreach (var adapter in driver.Adapters())
                if (driver.Mode(adapter.Handle) != AdapterMode.None)
                {
                    driver.SetMode(adapter.Handle, AdapterMode.None);
                    log.Say($"{why}: adapter {adapter.Name} given back");
                }
        }
        catch (Win32Exception) { }
    }

    private static (bool Ready, string? Problem) Driver()
    {
        try
        {
            using var driver = PacketDriver.Open();
            return driver.InternetAdapter() is null ? (false, "No network adapter carries the route to the internet.") : (true, null);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 2) { return (false, "The packet driver is not installed."); }
        catch (Win32Exception e) { return (false, $"The packet driver did not open (Windows error {e.NativeErrorCode})."); }
    }

    private static async Task TrySend(FrameStream frames, RouterMessage message)
    {
        try { await frames.SendAsync(message); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}

/// <summary>What the supervisor hands a new worker on its standard input: the programs, each with its loss policy.</summary>
sealed record WorkerSetup(ProgramRule[] Programs);
