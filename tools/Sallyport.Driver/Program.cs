using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Sallyport;
using Sallyport.Profile;
using Sallyport.Net;
using Sallyport.Windows.PacketFilter;
using Sallyport.Windows.Routing;

// Sallyport.Driver status | fetch | install | uninstall | adapters | pass | inject | rtt | reset | guard
//   The packet driver (Windows Packet Filter, D18) as Sallyport manages it: NT Kernel's pinned release, fetched into
//   %LOCALAPPDATA%\Sallyport\drivers and checked, installed or removed with one administrator prompt.
//   adapters, pass, inject, rtt, reset and guard are the router's first gates (docs/design/router.md): the driver's
//   interface, what it costs, and what undoes it. They need administrator, as opening the driver does.
// Exit codes: 0 done; 1 failed or refused; 2 bad arguments.

var setup = new PacketFilterSetup(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sallyport", "drivers"));
var internet = IPAddress.Parse("1.1.1.1");

switch (args.FirstOrDefault())
{
    case "status":
        Print(setup.Status());
        return 0;
    case "fetch":
        Console.WriteLine($"fetched and checked: {await FetchAsync()}");
        return 0;
    case "install":
    {
        if (!PacketFilterRelease.FitsThisMachine) { Console.WriteLine("this machine is not x64: the pinned installer does not fit it"); return 1; }
        await FetchAsync();
        var outcome = await setup.InstallAsync();
        Report(outcome);
        Print(setup.Status());
        return outcome.Succeeded ? 0 : 1;
    }
    case "uninstall":
    {
        await FetchAsync();
        var outcome = await setup.UninstallAsync();
        Report(outcome);
        Print(setup.Status());
        return outcome.Succeeded ? 0 : 1;
    }
    case "adapters":
        return Adapters();
    case "pass":
        return Pass(Option("--seconds", 15), Option("--crash-after", 0), Option("--batch", 64), ReceivePath());
    case "inject":
        return await InjectAsync(ReceivePath());
    case "rtt":
        return await RttAsync(Option("--count", 500));
    case "attribute":
        return await AttributeAsync(Option("--seconds", 20));
    case "workload":
        return await WorkloadAsync();
    case "route":
        return await RouteAsync(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null, Option("--seconds", 60));
    case "routed-workload":
        if (Console.ReadLine() != "go") return 2;
        await ExerciseAsync();
        return 0;
    case "reset":
        return Reset();
    case "guard":
        return await GuardAsync(Option("--seconds", 120));
    default:
        Console.Error.WriteLine("usage: Sallyport.Driver status | fetch | install | uninstall | adapters"
            + " | pass [--seconds N] [--crash-after S] [--batch N] [--mode send|both] | inject [--mode send|both]"
            + " | rtt [--count N] | attribute [--seconds N] | route <profile.ovpn> | reset | guard [--seconds N]");
        return 2;
}

// --mode send: only what Windows sends is taken; what arrives is left alone.
bool ReceivePath()
{
    var at = Array.IndexOf(args, "--mode");
    return !(at >= 0 && at + 1 < args.Length && args[at + 1] == "send");
}

int Option(string name, int fallback)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var value) ? value : fallback;
}

async Task<string> FetchAsync()
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    return await setup.FetchAsync(http);
}

static void Report(SetupOutcome outcome) =>
    Console.WriteLine($"{outcome.Text} (exit {outcome.ExitCode}){(outcome.LogPath is { } log ? $"; log {log}" : "")}");

static void Print(PacketFilterStatus status)
{
    Console.WriteLine($"service: {status.Service}");
    Console.WriteLine(status.DriverVersion is { } v
        ? $"driver: answers, version word {status.VersionText}"
        : $"driver: not reached (Win32 error {status.DeviceError}{(status.DeviceError == 5 ? ": access denied — opening it needs administrator" : status.DeviceError == 2 ? ": no such device" : "")})");
    Console.WriteLine($"installer kept: {status.InstallerPath ?? "no"}");
}

static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

PacketDriver? OpenDriver()
{
    try { return PacketDriver.Open(); }
    catch (Win32Exception e)
    {
        Console.WriteLine($"driver not opened: {e.Message} (Win32 error {e.NativeErrorCode})");
        return null;
    }
}

// The adapter carrying the person's internet traffic: the one Windows sends 1.1.1.1 through.
DriverAdapter? RouteAdapter(PacketDriver driver)
{
    var id = PacketDriver.InterfaceFor(internet);
    return id is null ? null : driver.Adapters().FirstOrDefault(a => a.InterfaceId == id);
}

NetworkInterface? Nic(DriverAdapter adapter) =>
    NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => Guid.TryParse(n.Id, out var id) && id == adapter.InterfaceId);

int Adapters()
{
    using var driver = OpenDriver();
    if (driver is null) return 1;
    var names = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => Guid.TryParse(n.Id, out _)).ToDictionary(n => Guid.Parse(n.Id), n => n.Name);
    var route = PacketDriver.InterfaceFor(internet);
    var adapters = driver.Adapters();
    Console.WriteLine($"{adapters.Count} adapters bound");
    foreach (var a in adapters)
    {
        var name = a.InterfaceId is { } id && names.TryGetValue(id, out var n) ? n : a.Name;
        Console.WriteLine($"  0x{a.Handle:X16}  medium {a.Medium}  mtu {a.Mtu,5}  mac {a.Address}  mode {driver.Mode(a.Handle)}"
            + $"  queue {driver.QueueSize(a.Handle)}  {name}{(a.InterfaceId == route ? "  <- route to the internet" : "")}");
    }
    return 0;
}

// Every packet of the internet adapter taken from the driver and handed back unchanged: what interception costs before
// it does anything. --crash-after ends the process without a word to the driver, as a crash would. --mode send takes
// only what Windows sends; arriving packets reach Windows untouched.
int Pass(int seconds, int crashAfter, int batchSize, bool receivePath)
{
    using var driver = OpenDriver();
    if (driver is null) return 1;
    var adapter = RouteAdapter(driver);
    if (adapter is null) { Console.WriteLine("no adapter carries the route to the internet"); return 1; }

    var cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
    using var forwarder = new Interception(driver, adapter.Handle, batchSize, receivePath);
    Console.WriteLine($"{Now()} pass: adapter 0x{adapter.Handle:X} in mode {driver.Mode(adapter.Handle)}, {seconds} s"
        + (crashAfter > 0 ? $", crashing at {crashAfter} s" : ""));
    var clock = Stopwatch.StartNew();
    var nextLine = 1;
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        if (crashAfter > 0 && clock.Elapsed.TotalSeconds >= crashAfter)
        {
            Console.WriteLine($"{Now()} pass: crashing now; the handle is left to Windows to close");
            Console.Out.Flush();
            Process.GetCurrentProcess().Kill();
        }
        forwarder.Wait(100);
        forwarder.Drain();
        if (clock.Elapsed.TotalSeconds >= nextLine) Console.WriteLine($"{Now()} t={nextLine++,3}s  {forwarder}");
    }
    forwarder.Stop();
    Console.WriteLine($"{Now()} pass: done, mode {driver.Mode(adapter.Handle)}, queue {driver.QueueSize(adapter.Handle)}; {forwarder};"
        + $" CPU {(Process.GetCurrentProcess().TotalProcessorTime - cpuAtStart).TotalMilliseconds:F0} ms over {clock.Elapsed.TotalSeconds:F1} s");
    return 0;
}

// A reply of this program's making handed up to Windows — what the router does with every packet the tunnel returns.
// A UDP socket sends to TEST-NET-1 (192.0.2.1, RFC 5737: never routed); that datagram is kept off the network, and a
// reply from 192.0.2.1 built from it is handed up. Whether the socket receives it is the answer. --mode send leaves
// the adapter's receive path untouched: whether the router needs that path at all.
async Task<int> InjectAsync(bool receivePath)
{
    using var driver = OpenDriver();
    if (driver is null) return 1;
    var adapter = RouteAdapter(driver);
    var local = adapter is null ? null : Nic(adapter)?.GetIPProperties().UnicastAddresses
        .Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    if (adapter is null || local is null) { Console.WriteLine("no adapter with an IPv4 address carries the route to the internet"); return 1; }

    var remote = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 40000);
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Bind(new IPEndPoint(local, 0));
    var port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;

    using var forwarder = new Interception(driver, adapter.Handle, 64, receivePath);
    using var reply = new PacketBatch(1);
    Console.WriteLine($"{Now()} inject: adapter 0x{adapter.Handle:X} in mode {driver.Mode(adapter.Handle)}; socket {local}:{port} -> {remote}");
    var injected = "nothing captured to answer";
    bool? handleInUnion = null;
    var buffer = new byte[64];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var answer = socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token).AsTask();
    socket.SendTo("sallyport out"u8, remote);
    var clock = Stopwatch.StartNew();
    TimeSpan? injectedAt = null;
    while (!answer.IsCompleted)
    {
        forwarder.Wait(20);
        forwarder.Drain(packet =>
        {
            if (injectedAt is not null || packet.Direction != PacketDirection.Send || !IsTo(packet.Frame, port, remote)) return false;
            handleInUnion = packet.Adapter == adapter.Handle;
            var ip = Ipv4.UdpPacket(remote.Address, (ushort)remote.Port, local, port, "sallyport in"u8, id: 1);
            var frame = new byte[14 + ip.Length];
            packet.Frame[6..12].CopyTo(frame);           // to this machine: the captured frame's source
            packet.Frame[..6].CopyTo(frame.AsSpan(6));    // from its gateway: the captured frame's destination
            frame[12] = 0x08;                             // EtherType IPv4
            ip.CopyTo(frame, 14);
            reply.Write(0, adapter.Handle, frame, PacketDirection.Receive);
            try { injected = driver.Send(adapter.Handle, reply, [0], toNetwork: false) is { } took ? $"driver took {took}" : "driver wrote no count"; }
            catch (Win32Exception e) { injected = $"driver refused, Win32 error {e.NativeErrorCode}"; }
            injectedAt = clock.Elapsed;
            return true;
        });
    }
    forwarder.Stop();

    string outcome;
    try
    {
        var got = await answer;
        outcome = $"DELIVERED {got.ReceivedBytes} bytes from {got.RemoteEndPoint}: \"{Encoding.ASCII.GetString(buffer, 0, got.ReceivedBytes)}\""
            + $" {(clock.Elapsed - injectedAt)?.TotalMilliseconds:F1} ms after injection";
    }
    catch (OperationCanceledException) { outcome = "NOT delivered within 3 s"; }
    catch (SocketException e) { outcome = $"socket error {e.SocketErrorCode}"; }
    Console.WriteLine($"{Now()} inject: outbound kept {forwarder.Kept}; reply: {injected}; a read leaves the adapter's handle in the buffer: "
        + $"{(handleInUnion is { } h ? (h ? "yes" : "no") : "not seen")}; {outcome}");
    Console.WriteLine($"{Now()} inject: {forwarder}");
    return outcome.StartsWith("DELIVERED", StringComparison.Ordinal) ? 0 : 1;

    static bool IsTo(ReadOnlySpan<byte> frame, ushort sourcePort, IPEndPoint to)
    {
        if (frame.Length < 14 + 20 + 8 || frame[12] != 0x08 || frame[13] != 0x00) return false;
        var ip = frame[14..];
        var headerLength = (ip[0] & 0x0F) * 4;
        if (ip[0] >> 4 != 4 || ip[9] != Ipv4.Udp || ip.Length < headerLength + 8 || !ip.Slice(16, 4).SequenceEqual(to.Address.GetAddressBytes()))
            return false;
        var udp = ip[headerLength..];
        return BinaryPrimitives.ReadUInt16BigEndian(udp) == sourcePort && BinaryPrimitives.ReadUInt16BigEndian(udp[2..]) == to.Port;
    }
}

// Every packet Windows sends, matched to its process through the owner tables, a miss refreshed once per drain — while a
// child process plays a game whose cases are known (WorkloadAsync), tagged in its payloads, so each answer is scored
// against the truth. Test traffic goes to TEST-NET addresses (RFC 5737) and is kept off the network.
async Task<int> AttributeAsync(int seconds)
{
    using var driver = OpenDriver();
    if (driver is null) return 1;
    var adapter = RouteAdapter(driver);
    if (adapter is null) { Console.WriteLine("no adapter carries the route to the internet"); return 1; }

    var owners = new OwnerTable();
    owners.Refresh();
    var kinds = new Dictionary<FrameKind, int>();
    var verdicts = new Dictionary<string, int>();          // "TCP owned at first look", "UDP unowned", ...
    var cases = new SortedDictionary<string, Dictionary<string, int>>();
    var unownedFlows = new Dictionary<string, int>();
    var ownerPackets = new Dictionary<int, int>();
    var refreshTimes = new List<double>();
    var self = Environment.ProcessId;
    var child = 0;
    var refreshedThisDrain = false;

    using var forwarder = new Interception(driver, adapter.Handle, 64, receivePath: false);
    Console.WriteLine($"{Now()} attribute: adapter 0x{adapter.Handle:X} in mode {driver.Mode(adapter.Handle)}, {seconds} s;"
        + $" owner tables read in {owners.LastRefresh.TotalMilliseconds:F2} ms");

    var workload = Task.Run(async () =>
    {
        await Task.Delay(2000);
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "workload") { RedirectStandardOutput = true })!;
        child = process.Id;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            Console.WriteLine($"{Now()} workload: {line}");
            // Case E's second half: this process takes the port the child just let go.
            if (line.StartsWith("E released ", StringComparison.Ordinal) && int.TryParse(line[11..], out var port))
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                for (var i = 0; i < 5; i++)
                {
                    socket.SendTo("case=E owner=parent"u8, new IPEndPoint(IPAddress.Parse("192.0.2.14"), 9));
                    await Task.Delay(50);
                }
            }
        }
        await process.WaitForExitAsync();
    });

    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        forwarder.Wait(100);
        refreshedThisDrain = false;
        forwarder.Drain(Attribute);
    }
    forwarder.Stop();
    await workload;

    Console.WriteLine($"{Now()} attribute: {forwarder}");
    Console.WriteLine($"frames: {string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}");
    Console.WriteLine($"verdicts: {string.Join(", ", verdicts.OrderBy(v => v.Key).Select(v => $"{v.Key} {v.Value}"))}");
    refreshTimes.Sort();
    Console.WriteLine($"refreshes: {owners.Refreshes}; ms p50 {Pick(.5):F2} p99 {Pick(.99):F2} max {Pick(1):F2}");
    Console.WriteLine("test cases (child " + child + ", parent " + self + "):");
    foreach (var (name, tally) in cases) Console.WriteLine($"  {name}: {string.Join(", ", tally.OrderBy(t => t.Key).Select(t => $"{t.Key} {t.Value}"))}");
    Console.WriteLine("unowned flows, most packets first:");
    foreach (var (flow, count) in unownedFlows.OrderByDescending(u => u.Value).Take(12)) Console.WriteLine($"  {count,5}  {flow}");
    Console.WriteLine("owners, most packets first:");
    foreach (var (pid, count) in ownerPackets.OrderByDescending(o => o.Value).Take(8)) Console.WriteLine($"  {count,6}  {pid} {Name(pid)}");
    return 0;

    double Pick(double q) => refreshTimes.Count == 0 ? double.NaN : refreshTimes[Math.Min(refreshTimes.Count - 1, (int)(q * refreshTimes.Count))];

    static string Name(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch (ArgumentException) { return "(exited)"; }
    }

    bool Attribute(DriverPacket packet)
    {
        var kind = Frames.TryReadOutbound(packet.Frame, out var flow);
        kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        if (kind != FrameKind.Flow) return false;

        var look = "at first look";
        var answer = owners.Owner(flow, out var pid);
        if (answer == Ownership.Unowned && !refreshedThisDrain)
        {
            owners.Refresh();
            refreshTimes.Add(owners.LastRefresh.TotalMilliseconds);
            refreshedThisDrain = true;
            look = "after a refresh";
            answer = owners.Owner(flow, out pid);
        }
        var protocol = flow.Protocol == Flow.Tcp ? "TCP" : "UDP";
        var verdict = answer switch
        {
            Ownership.Owned => $"{protocol} owned {look}",
            Ownership.Ambiguous => $"{protocol} ambiguous",
            _ => $"{protocol} unowned",
        };
        verdicts[verdict] = verdicts.GetValueOrDefault(verdict) + 1;
        if (answer == Ownership.Owned) ownerPackets[pid] = ownerPackets.GetValueOrDefault(pid) + 1;

        var test = TestCase(packet.Frame, flow);
        if (test is null)
        {
            if (answer == Ownership.Unowned)
            {
                var key = $"{protocol} :{flow.LocalPort} -> :{flow.RemotePort}";
                unownedFlows[key] = unownedFlows.GetValueOrDefault(key) + 1;
            }
            return false;
        }
        var (name, expected) = test.Value;
        var score = answer switch
        {
            Ownership.Owned when pid == (expected == "parent" ? self : child) => $"right {look}",
            Ownership.Owned => "WRONG",
            Ownership.Ambiguous => "ambiguous",
            _ => "unowned",
        };
        var tally = cases.TryGetValue(name, out var t) ? t : cases[name] = [];
        tally[score] = tally.GetValueOrDefault(score) + 1;
        return true;
    }

    // The case a packet of the workload belongs to: its payload's tag, or for a bare TCP SYN its destination.
    static (string Name, string Owner)? TestCase(ReadOnlySpan<byte> frame, in Flow flow)
    {
        var remote = flow.Remote.GetAddressBytes();
        var testNet = (remote[0], remote[1], remote[2]) is (192, 0, 2) or (198, 51, 100);
        if (!testNet) return null;
        if (flow.Protocol == Flow.Tcp) return ("D TCP connect", "child");
        var text = Encoding.ASCII.GetString(frame[Math.Min(frame.Length, 14 + 20 + 8)..]);
        var at = text.IndexOf("case=", StringComparison.Ordinal);
        if (at < 0) return ("untagged", "child");
        var name = text[(at + 5)..(at + 6)] switch
        {
            "A" => "A UDP, closed at once",
            "B" => "B UDP, 100 ms",
            "C" => "C UDP, one socket, 50 peers",
            "E" => "E port reused",
            var other => other,
        };
        return (name, text.Contains("owner=parent", StringComparison.Ordinal) ? "parent" : "child");
    }
}

// The game the attribution is scored against: each case's packets carry their case and expected owner.
async Task<int> WorkloadAsync()
{
    var any = new IPEndPoint(IPAddress.Any, 0);
    for (var i = 0; i < 20; i++)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(any);
        socket.SendTo("case=A owner=child"u8, new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9));
    }
    Console.WriteLine("A sent");
    for (var i = 0; i < 20; i++)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(any);
        socket.SendTo("case=B owner=child"u8, new IPEndPoint(IPAddress.Parse("192.0.2.11"), 9));
        await Task.Delay(100);
    }
    Console.WriteLine("B sent");
    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
    {
        socket.Bind(any);
        for (var peer = 1; peer <= 50; peer++)
        {
            socket.SendTo("case=C owner=child"u8, new IPEndPoint(IPAddress.Parse($"198.51.100.{peer}"), 9));
            await Task.Delay(20);
        }
    }
    Console.WriteLine("C sent");
    for (var i = 0; i < 10; i++)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var give = new CancellationTokenSource(300);
        try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("192.0.2.13"), 443), give.Token); }
        catch (Exception e) when (e is OperationCanceledException or SocketException) { }
    }
    Console.WriteLine("D sent");
    // Long enough in the tables to be in a snapshot, then let go for the parent to take at once: a stale snapshot
    // would still name this process.
    int port;
    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
    {
        socket.Bind(any);
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.SendTo("case=E owner=child"u8, new IPEndPoint(IPAddress.Parse("192.0.2.14"), 9));
        await Task.Delay(300);
    }
    Console.WriteLine($"E released {port}");
    await Task.Delay(1500);
    return 0;
}

// Gate 4: one program routed through the real VPN from its first packet, while this one stays direct. The profile is
// read where its owner keeps it and nothing of it is printed (rule 2). The tunnel lives here, as the client will hold
// it (docs/design/router.md change 1); the router takes the child's packets from the driver and hands its replies up.
async Task<int> RouteAsync(string? profilePath, int seconds)
{
    if (profilePath is null) { Console.Error.WriteLine("usage: Sallyport.Driver route <profile.ovpn>"); return 2; }
    OvpnProfile profile;
    // The exception's type only: a parse error's message may quote the profile.
    try { profile = OvpnProfile.Parse(File.ReadAllText(profilePath)); }
    catch (Exception e) { Console.WriteLine($"could not read the profile ({e.GetType().Name})"); return 2; }

    using var driver = OpenDriver();
    if (driver is null) return 1;
    var adapter = RouteAdapter(driver);
    if (adapter is null) { Console.WriteLine("no adapter carries the route to the internet"); return 1; }

    Tunnel tunnel;
    try { tunnel = await Tunnel.ConnectAsync(profile, profile.Remotes[0], TimeSpan.FromSeconds(30)); }
    catch (HandshakeFailed e) { Console.WriteLine($"tunnel: {e.Outcome} at {e.Stage}"); return 1; }
    await using (tunnel)
    {
        if (tunnel.Config.Address is not { AddressFamily: AddressFamily.InterNetwork } tunnelAddress)
        {
            Console.WriteLine("tunnel: the server pushed no IPv4 address");
            return 1;
        }
        Console.WriteLine($"{Now()} route: tunnel up in {tunnel.ConnectTime.TotalSeconds:F1} s over {profile.Remotes[0].Proto}");

        var outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var child = 0;
        var nat = new Nat(BinaryPrimitives.ReadUInt32LittleEndian(tunnelAddress.GetAddressBytes()));
        using var router = new Router(driver, adapter.Handle, pid => pid == Volatile.Read(ref child), nat, p => outbound.Writer.TryWrite(p));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var routing = Task.Factory.StartNew(() => router.Run(stop.Token), TaskCreationOptions.LongRunning);
        var sending = Task.Run(async () =>
        {
            await foreach (var packet in outbound.Reader.ReadAllAsync())
                try { await tunnel.SendAsync(packet); }
                catch (TunnelEnded) { return; }
        });
        var receiving = Task.Run(async () =>
        {
            try { await foreach (var packet in tunnel.Packets.ReadAllAsync(stop.Token)) router.FromTunnel(packet); }
            catch (OperationCanceledException) { }
        });
        Console.WriteLine($"{Now()} route: adapter 0x{adapter.Handle:X} in mode {driver.Mode(adapter.Handle)}");

        Console.WriteLine("direct — this process, not selected:");
        await ExerciseAsync();

        using (var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "routed-workload")
               { RedirectStandardInput = true, RedirectStandardOutput = true })!)
        {
            // Selected before it sends anything: its first packet is the one this gate is about.
            Volatile.Write(ref child, process.Id);
            Console.WriteLine($"routed — child {process.Id}, selected:");
            await process.StandardInput.WriteLineAsync("go");
            while (await process.StandardOutput.ReadLineAsync() is { } line) Console.WriteLine(line);
            await process.WaitForExitAsync();
        }
        await Task.Delay(500);
        await stop.CancelAsync();
        outbound.Writer.Complete();
        await Task.WhenAll(routing, sending, receiving);

        Console.WriteLine($"{Now()} route: routed {router.RoutedPackets}, direct {router.DirectPackets}, unowned {router.UnownedPackets},"
            + $" held back {router.HeldBack}, late flows {router.LateFlows} ({router.LatePackets} packets); handed up {router.HandedUp},"
            + $" hand-up refused {router.HandUpRefused}; from the tunnel refused: "
            + (router.Refused.Count == 0 ? "none" : string.Join(", ", router.Refused.Select(r => $"{r.Key} {r.Value}"))));
        Console.WriteLine($"{Now()} route: NAT mappings {nat.Count}; owner-table refreshes {router.Owners.Refreshes}; {router.Interception}");
        var childFlows = router.Flows.Where(f => f.Value.Pid == child || f.Value.Verdict == Verdict.Routed).ToList();
        Console.WriteLine($"the child's flows ({childFlows.Count}):");
        foreach (var (flow, decision) in childFlows)
            Console.WriteLine($"  {decision.Verdict,-8} {decision.Packets,5} packets  {(flow.Protocol == Flow.Tcp ? "TCP" : "UDP")} :{flow.LocalPort} -> {flow.Remote}:{flow.RemotePort}"
                + (decision.Pid == child && decision.Verdict != Verdict.Routed ? "  <- LEFT DIRECT" : ""));
        var counters = tunnel.Counters;
        Console.WriteLine($"tunnel: {counters}");
        return 0;
    }
}

// What a game does, measured from both ends: UDP to a STUN server (which says the address it saw) and HTTPS to an
// address echo. IPv4 only, as the router carries.
static async Task ExerciseAsync()
{
    var stun = (await Dns.GetHostAddressesAsync("stun.l.google.com", AddressFamily.InterNetwork))[0];
    using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
    {
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        var times = new List<double>();
        string? seen = null;
        var buffer = new byte[512];
        for (var i = 0; i < 5; i++)
        {
            // A STUN binding request (RFC 5389): type 0x0001, length 0, the magic cookie, a 12-byte transaction ID.
            var request = new byte[20];
            BinaryPrimitives.WriteUInt16BigEndian(request, 0x0001);
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), 0x2112A442);
            Random.Shared.NextBytes(request.AsSpan(8, 12));
            var started = Stopwatch.GetTimestamp();
            await udp.SendToAsync(request, new IPEndPoint(stun, 19302));
            using var give = new CancellationTokenSource(2000);
            try
            {
                var got = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), give.Token);
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                seen ??= Mapped(buffer.AsSpan(0, got.ReceivedBytes));
            }
            catch (OperationCanceledException) { }
        }
        times.Sort();
        Console.WriteLine($"  udp: STUN from local port {((IPEndPoint)udp.LocalEndPoint!).Port}: {times.Count}/5 answered, far end saw {seen ?? "-"},"
            + $" round trip p50 {(times.Count > 0 ? times[times.Count / 2] : double.NaN):F1} ms");
    }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var begun = Stopwatch.GetTimestamp();
    try
    {
        var body = (await http.GetStringAsync("https://api4.ipify.org")).Trim();
        Console.WriteLine($"  tcp: HTTPS address echo saw {body} in {Stopwatch.GetElapsedTime(begun).TotalMilliseconds:F0} ms");
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Console.WriteLine($"  tcp: HTTPS failed: {e.GetType().Name} {e.Message}"); }

    // XOR-MAPPED-ADDRESS (0x0020): family, port and IPv4 address, each XORed with the magic cookie's leading bytes.
    static string? Mapped(ReadOnlySpan<byte> response)
    {
        for (var at = 20; at + 4 <= response.Length;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[at..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(response[(at + 2)..]);
            if (type == 0x0020 && length >= 8 && response[at + 5] == 1)
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(response[(at + 6)..]) ^ 0x2112;
                var address = BinaryPrimitives.ReadUInt32BigEndian(response[(at + 8)..]) ^ 0x2112A442;
                return $"{new IPAddress(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(address)))}:{port}";
            }
            at += 4 + ((length + 3) & ~3);
        }
        return null;
    }
}

// Round trips to the gateway, 50 a second: Wi-Fi's own jitter is the floor that any latency the router adds is read
// against.
async Task<int> RttAsync(int count)
{
    using var driver = OpenDriver();
    var adapter = driver is null ? null : RouteAdapter(driver);
    var gateway = adapter is null ? null : Nic(adapter)?.GetIPProperties().GatewayAddresses
        .Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    if (gateway is null) { Console.WriteLine("no IPv4 gateway on the route to the internet"); return 1; }
    using var ping = new Ping();
    var times = new List<double>(count);
    var lost = 0;
    for (var i = 0; i < count; i++)
    {
        var started = Stopwatch.GetTimestamp();
        var answer = await ping.SendPingAsync(gateway, 1000);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (answer.Status == IPStatus.Success) times.Add(elapsed); else lost++;
        if (elapsed < 20) await Task.Delay(TimeSpan.FromMilliseconds(20 - elapsed));
    }
    times.Sort();
    double At(double q) => times.Count == 0 ? double.NaN : times[Math.Min(times.Count - 1, (int)(q * times.Count))];
    Console.WriteLine($"{Now()} rtt to the gateway: {times.Count} answered, {lost} lost;"
        + $" p50 {At(.5):F2} ms  p95 {At(.95):F2} ms  p99 {At(.99):F2} ms  max {At(1):F2} ms");
    return 0;
}

// From a handle of its own: whether a second opener of the driver can take an adapter out of another's tunnel mode.
int Reset()
{
    using var driver = OpenDriver();
    if (driver is null) return 1;
    var adapter = RouteAdapter(driver);
    if (adapter is null) { Console.WriteLine("no adapter carries the route to the internet"); return 1; }
    var before = driver.Mode(adapter.Handle);
    try { driver.SetMode(adapter.Handle, AdapterMode.None); }
    catch (Win32Exception e) { Console.WriteLine($"{Now()} reset: refused, Win32 error {e.NativeErrorCode}"); }
    Console.WriteLine($"{Now()} reset: adapter 0x{adapter.Handle:X} mode {before} -> {driver.Mode(adapter.Handle)}, queue {driver.QueueSize(adapter.Handle)}");
    return 0;
}

// Outside every interceptor (D13): probes the direct path twice a second and, when it has been dead 3 s, undoes
// interception — its own handle first, then killing the interceptors, then removing the driver, which is measured to
// give the network back (D18).
async Task<int> GuardAsync(int seconds)
{
    var targets = new[] { new IPEndPoint(internet, 443), new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443) };
    var clock = Stopwatch.StartNew();
    TimeSpan? deadSince = null;
    var stage = 0;
    int rounds = 0, failed = 0;
    var longest = TimeSpan.Zero;
    Console.WriteLine($"{Now()} guard: {seconds} s, probing {string.Join(" and ", targets.Select(t => t.ToString()))}");
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        var round = clock.Elapsed;
        var times = await Task.WhenAll(targets.Select(ProbeAsync));
        rounds++;
        var best = times.Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(-1).Min();
        if (best >= 0)
        {
            Console.WriteLine($"{Now()} ok {best:F1} ms");
            if (deadSince is { } since)
            {
                var outage = round - since;
                longest = outage > longest ? outage : longest;
                Console.WriteLine($"{Now()} guard: direct path back after {outage.TotalSeconds:F1} s");
            }
            deadSince = null;
            stage = 0;
        }
        else
        {
            failed++;
            deadSince ??= round;
            var dead = round - deadSince.Value;
            Console.WriteLine($"{Now()} FAIL (dead {dead.TotalSeconds:F1} s)");
            if (stage == 0 && dead >= TimeSpan.FromSeconds(3)) { stage = 1; Reset(); }
            else if (stage == 1 && dead >= TimeSpan.FromSeconds(8)) { stage = 2; KillInterceptors(); }
            else if (stage == 2 && dead >= TimeSpan.FromSeconds(18))
            {
                stage = 3;
                Console.WriteLine($"{Now()} guard: removing the driver");
                Report(await setup.UninstallAsync());
            }
        }
        var wait = TimeSpan.FromMilliseconds(500) - (clock.Elapsed - round);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
    }
    Console.WriteLine($"{Now()} guard: {rounds} rounds, {failed} failed, longest outage {longest.TotalSeconds:F1} s"
        + (deadSince is not null ? ", STILL DEAD at the end" : ""));
    return deadSince is null ? 0 : 1;

    static async Task<double?> ProbeAsync(IPEndPoint target)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = Stopwatch.GetTimestamp();
        try
        {
            await socket.ConnectAsync(target, timeout.Token);
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return null; }
    }

    static void KillInterceptors()
    {
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("Sallyport.Driver").Where(p => p.Id != self))
        {
            Console.WriteLine($"{Now()} guard: killing interceptor {p.Id}");
            try { p.Kill(); } catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        }
    }
}
