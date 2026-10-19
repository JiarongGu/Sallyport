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
            var selection = new ProgramSelection(setup.Executables);
            var loss = new LossPolicy(setup.Loss, setup.DirectAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null);
            // What the router routed waits here for the pipe, so the router never waits on the pipe (product design §2:
            // never block forwarding behind it); the oldest goes first when the client falls behind.
            var outbound = Channel.CreateBounded<(int, byte[])>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
            using var route = new GameRoute(driver, adapter, selection.Selects, loss, (generation, packet) => outbound.Writer.TryWrite((generation, packet)), PacketDriver.OnLink(adapter));
            route.PhaseChanged += phase => Say($"log route {phase}");

            await using var pipe = NamedPipeServerStreamAcl.Create(RouterProtocol.RunPipe(runId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 1 << 20, 1 << 20, PipePeer.Security());
            using var stop = new CancellationTokenSource();
            var routing = Task.Factory.StartNew(() => route.Run(stop.Token), TaskCreationOptions.LongRunning);
            Say($"ready {adapter.Handle:X}");
            Say($"log on {adapter.Name}, MTU {adapter.Mtu}, {setup.Executables.Length} program(s), loss {loss.Mode}");
            // The router's drains, idle or not (it waits at most 50 ms between two): a count that stops is a router stuck,
            // whatever this thread still says.
            var beating = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                var interception = route.Router.Interception;
                while (await timer.WaitForNextTickAsync(stop.Token)) Say($"beat {interception.Reads + interception.EmptyReads}");
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

                var sending = Task.Run(async () =>
                {
                    await foreach (var (generation, packet) in outbound.Reader.ReadAllAsync(stop.Token)) await frames.SendPacketAsync(generation, packet, stop.Token);
                });
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
                                Say($"log generation {commit.Generation} committed");
                                break;
                            case RetireGeneration retire:
                                route.Retire(retire.Generation);
                                Say($"log generation {retire.Generation} retired");
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
            Say($"log ended: routed {r.RoutedPackets}, handed up {r.HandedUp}, held {r.HeldWhileDown}, direct while down {r.DirectWhileDown},"
                + $" late flows {r.LateFlows}, stale {route.Stale}, selected {selection.Selected.Count()}");
            return 0;
        }
    }

    private static RouteState State(GameRoute route, ProgramSelection selection)
    {
        var r = route.Router;
        return new RouteState(route.Phase, route.Generation, r.RoutedPackets, r.HandedUp, r.DirectPackets, r.UnownedPackets, r.HeldWhileDown,
            r.DirectWhileDown, r.LateFlows, r.Blocked.Values.Sum(), route.Stale, r.RoutedFlows, selection.Selected.Count(), selection.Unreadable.Count());
    }
}
