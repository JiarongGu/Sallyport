using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Sallyport.Windows.PacketFilter;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

/// <summary>
/// One run's router (docs/design/product.md §1): it holds the packet driver, routes the selected programs through
/// whichever tunnel generation the client commits, and serves the run's own pipe to the one client the supervisor
/// prepared it for. It speaks to the supervisor on its standard output — "ready" and the adapter's handle, then "beat"
/// with the router's progress four times a second, and "log" lines — and ends when the client's pipe closes or the supervisor goes (its standard input closes; the job object kills
/// it if the supervisor dies outright). Ending gives the adapter back: the game goes direct.
/// </summary>
static class Worker
{
    public static async Task<int> RunAsync(string runId, int clientPid, Log _)
    {
        var output = Console.Out;
        void Say(string line)
        {
            lock (output) { output.WriteLine(line); output.Flush(); }
        }
        WorkerSetup? setup;
        try { setup = JsonSerializer.Deserialize<WorkerSetup>(await Console.In.ReadLineAsync() ?? "", RouterProtocol.Json); }
        catch (JsonException) { setup = null; }
        if (setup is null) { Say("error The router was started without its programs."); return 2; }

        PacketDriver driver;
        try { driver = PacketDriver.Open(); }
        catch (Win32Exception e) { Say($"error The packet driver did not open (Windows error {e.NativeErrorCode})."); return 1; }
        using (driver)
        {
            if (driver.InternetAdapter() is not { } adapter) { Say("error No network adapter carries the route to the internet."); return 1; }
            var selection = new ProgramSelection(setup.Programs, setup.Session);
            // What the router routed waits here for the pipe, so the router never waits on the pipe (product design §2:
            // never block forwarding behind it); the oldest goes first when the client falls behind.
            var outbound = Channel.CreateBounded<(int, byte[])>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
            // Each program's own loss policy (D23): the route holds while the tunnel is lost, and lets go what says otherwise.
            // Each into its profile's lane (D24).
            using var route = new GameRoute(driver, adapter, selection.Selects, new LossPolicy(), (generation, packet) => outbound.Writer.TryWrite((generation, packet)),
                PacketDriver.OnLink(adapter), lossOf: pid => selection.RuleOf(pid)?.Policy, laneOf: selection.LaneOf);
            route.LanePhaseChanged += (lane, phase) => Say(lane == 0 ? $"log route {phase}" : $"log route lane {lane} {phase}");

            await using var pipe = NamedPipeServerStreamAcl.Create(RouterProtocol.RunPipe(runId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 1 << 20, 1 << 20, PipePeer.Security());
            using var stop = new CancellationTokenSource();
            // The router stands in the path of every packet the adapter carries, so a busy machine must not starve it: on a
            // loaded CPU (measured 2026-10-20) it stood still for 1.4 s and the guard ended the run. Idle, it waits on the
            // driver, so its priority costs nothing then.
            using (var self = System.Diagnostics.Process.GetCurrentProcess()) self.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
            var routed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                try
                {
                    route.Run(stop.Token);
                    routed.SetResult();
                }
                catch (Exception e) { routed.SetException(e); }
            }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "router" }.Start();
            var routing = routed.Task;
            Say($"ready {adapter.Handle:X}");
            Say($"log on {adapter.Name}, MTU {adapter.Mtu}, {setup.Programs.Length} program(s), loss {Supervisor.Losses(setup.Programs)}");
            // The router's drains, idle or not (it waits at most 50 ms between two): a count that stops is a router stuck,
            // whatever this thread still says.
            var beating = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                var interception = route.Router.Interception;
                var stallsSaid = 0;
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    Say($"beat {interception.Reads + interception.EmptyReads}");
                    var stalls = route.Router.Stalls;
                    foreach (var stall in stalls.Skip(Math.Max(0, stallsSaid - Math.Max(0, stalls.Count - 32)))) Say($"log the router stood still: {stall}");
                    stallsSaid = stalls.Count;
                }
            });
            // The supervisor gone (its end of our standard input closed): nothing is left to answer to.
            var orphaned = Task.Run(async () =>
            {
                while (await Console.In.ReadLineAsync() is not null) { }
                await stop.CancelAsync();
            });

            try
            {
                using (var attach = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    attach.CancelAfter(TimeSpan.FromSeconds(15));
                    await pipe.WaitForConnectionAsync(attach.Token);
                }
                await using var frames = new FrameStream(pipe);
                var peer = PipePeer.ClientOf(pipe);
                if (peer.ProcessId != clientPid)
                {
                    Say($"log refused pid {peer.ProcessId} on the run's pipe: not the client it was prepared for");
                    await frames.SendAsync(new Rejected("This run was prepared for another client."));
                    return 1;
                }
                if ((await frames.ReadAsync(stop.Token))?.Message is not Attach attach2 || attach2.RunId != runId)
                {
                    await frames.SendAsync(new Rejected("Not this run."));
                    return 1;
                }
                await frames.SendAsync(new Attached(adapter.Name, adapter.Mtu), stop.Token);
                Say("log client attached");

                var sending = Task.Run(() => frames.PumpPacketsAsync(outbound.Reader, stop.Token));
                var reporting = Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    while (await timer.WaitForNextTickAsync(stop.Token)) await frames.SendAsync(State(route, selection), stop.Token);
                });
                while (await frames.ReadAsync(stop.Token) is { } frame)
                {
                    if (frame.Kind == RouterProtocol.PacketFrame) route.FromTunnel(frame.Generation, frame.Packet);
                    else
                        switch (frame.Message)
                        {
                            case CommitGeneration commit when IPAddress.TryParse(commit.Address, out var address) && address.GetAddressBytes().Length == 4:
                                route.Commit(commit.Generation, BinaryPrimitives.ReadUInt32LittleEndian(address.GetAddressBytes()), Math.Clamp(commit.Mtu, 576, adapter.Mtu));
                                Say($"log generation {Generation(commit.Generation)} committed");
                                break;
                            case RetireGeneration retire:
                                route.Retire(retire.Generation);
                                Say($"log generation {Generation(retire.Generation)} retired");
                                break;
                            // Checked as the supervisor checks a run's first: nothing of Sallyport's own, ever.
                            case SelectPrograms select when RouterProtocol.Routable(select.Programs ?? [], AppContext.BaseDirectory) is { Length: > 0 } programs:
                                route.Router.Reselect(() => selection.Replace(programs));
                                Say($"log selection now {programs.Length} program(s), loss {Supervisor.Losses(programs)}");
                                break;
                        }
                }
                Say("log the client closed the run's pipe");
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or JsonException or InvalidDataException or EndOfStreamException)
            {
                Say($"log the run's pipe ended: {e.GetType().Name}");
            }
            finally
            {
                route.Stop();
                await stop.CancelAsync();
                await routing;
            }
            var r = route.Router;
            foreach (var miss in r.SynMisses) Say($"log a SYN with no owner: {miss}");
            Say($"log ended: routed {r.RoutedPackets}, handed up {r.HandedUp}, held {r.HeldWhileDown}, direct while down {r.DirectWhileDown},"
                + $" late flows {r.LateFlows}, reselected {r.Reselected}, stale {route.Stale}, selected {selection.Selected.Count()}");
            return 0;
        }
    }

    // "3", or "3 of lane 1": the log says a lane only where there is more than one.
    private static string Generation(int wire) => GameRoute.FromWire(wire) is var (lane, generation) && lane != 0 ? $"{generation} of lane {lane}" : $"{generation}";

    private static RouteState State(GameRoute route, ProgramSelection selection)
    {
        var r = route.Router;
        // By program, as the window shows each: the router counts by process, the selection knows each one's executable.
        var programs = r.ByProcess
            .Select(p => (Path: selection.PathOf(p.Key), Routed: p.Value))
            .Where(p => p.Path is not null)
            .GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase)
            // A program's round trip is its busiest process's: the one most of what it sends goes through.
            .Select(g => new ProgramRouted(g.Key, g.Count(), g.Sum(p => p.Routed.Flows), g.Sum(p => p.Routed.Packets),
                g.Sum(p => p.Routed.BytesOut), g.Sum(p => p.Routed.BytesIn),
                g.Where(p => p.Routed.Latency is not null).OrderByDescending(p => p.Routed.Packets).Select(p => p.Routed.Latency).FirstOrDefault()))
            .ToList();
        // Of the processes whose executable could not be read, those still running refused it (a protected process);
        // the others had closed before their first packet was looked at.
        var unreadable = selection.Unreadable.ToArray();
        var running = unreadable.Length == 0 ? 0 : RunningOf(unreadable);
        return new RouteState(route.Phase, route.Generation, r.RoutedPackets, r.HandedUp, r.DirectPackets, r.UnownedPackets, r.HeldWhileDown,
            r.DirectWhileDown, r.LateFlows, r.Blocked.Values.Sum(), route.Stale, r.RoutedFlows, selection.Selected.Count(), unreadable.Length,
            programs, [.. Enumerable.Range(0, Router.Lanes).Select(l => new LaneRoute(l, route.PhaseOf(l), route.GenerationOf(l)))], running);
    }

    private static int RunningOf(int[] pids)
    {
        var processes = System.Diagnostics.Process.GetProcesses();
        var alive = processes.Select(p => p.Id).ToHashSet();
        foreach (var process in processes) process.Dispose();
        return pids.Count(alive.Contains);
    }
}
