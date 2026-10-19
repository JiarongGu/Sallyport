using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Sallyport;
using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Net;
using Sallyport.Protocol;
using Sallyport.Windows.PacketFilter;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

// Sallyport.Driver status | fetch | install [--location <folder>] | uninstall | adapters | pass | inject | rtt | reset | guard
//   The packet driver (Windows Packet Filter, D18) as Sallyport manages it: NT Kernel's pinned release, fetched into
//   a drivers folder beside this tool and checked, installed (its own files in --location, if given) or removed with
//   one administrator prompt.
//   adapters, pass, inject, rtt, reset and guard are the router's first gates (docs/design/router.md): the driver's
//   interface, what it costs, and what undoes it. They need administrator, as opening the driver does.
// Exit codes: 0 done; 1 failed or refused; 2 bad arguments.

// Beside the tool, not in %LOCALAPPDATA%: the owner keeps nothing of Sallyport's on the system drive (D19).
var setup = new PacketFilterSetup(Path.Combine(AppContext.BaseDirectory, "drivers"));
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
        var outcome = await setup.InstallAsync(Text("--location"));
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
        return await RouteAsync(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null, Option("--seconds", 60), Text("--folder"),
            Text("--workload"), Text("--peer6") is { } peer6 ? IPAddress.Parse(peer6) : null, Text("--oversized") == "split",
            new LossPolicy(Text("--loss") switch { "direct" => LossMode.Direct, "stop" => LossMode.Stop, _ => LossMode.Hold },
                Option("--direct-after", 0) is > 0 and var after ? TimeSpan.FromSeconds(after) : null),
            Option("--cut-after", 0), Option("--cut-for", 0), Option("--ping-restart", 0));
    case "service-route":
        return await ServiceRouteAsync(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null, Option("--seconds", 60),
            Text("--child"), Text("--folder"), Text("--workload"),
            new LossPolicy(Text("--loss") switch { "direct" => LossMode.Direct, "stop" => LossMode.Stop, _ => LossMode.Hold },
                Option("--direct-after", 0) is > 0 and var directAfter ? TimeSpan.FromSeconds(directAfter) : null),
            Option("--cut-after", 0), Option("--cut-for", 0), Option("--ping-restart", 0), Option("--proxy", 0));
    case "service-hello":
        return await ServiceHelloAsync();
    case "service-malformed":
        return await ServiceMalformedAsync();
    case "service-failure":
        return await ServiceFailureAsync(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null, Text("--child"), Text("--case"));
    case "reconnect":
        return await ReconnectAsync(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null,
            Option("--block-after", 15), Option("--block-for", 150));
    case "stop":
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out var running)) { Console.WriteLine("no routing session is running"); return 1; }
        using (running) running.Set();
        Console.WriteLine("asked the routing session to stop");
        return 0;
    }
    case "mixed-to":
    {
        // mixed-to <seconds> <file> [home address]: the "mixed" workload, written to a file — for a copy started by
        // something else (Sallyport's Launch), which no console of this one sees.
        if (args.Length < 3 || !int.TryParse(args[1], out var mixedSeconds)) return 2;
        using (var file = new StreamWriter(args[2], append: false) { AutoFlush = true })
        {
            Console.SetOut(file);
            Console.WriteLine($"{Now()} started");
            await MixedAsync(mixedSeconds, args.Length > 3 ? args[3] : null, null, " launched");
        }
        return 0;
    }
    case "routed-workload":
    {
        // "go", or "go boundaries <tunnel gateway> <LAN gateway> <local address> <IPv6 neighbour> <IPv6 scope>" ("-" for
        // none), or "go steady <seconds> <home address>", or "go mixed <seconds> <home address>".
        var go = Console.ReadLine()?.Split(' ');
        if (go is null || go[0] != "go") return 2;
        if (go.Length > 1 && go[1] == "boundaries") await BoundariesAsync(Word(go, 2), Word(go, 3), Word(go, 4), Word(go, 5), go.Length > 6 ? long.Parse(go[6]) : 0);
        else if (go.Length > 2 && go[1] == "steady") await SteadyAsync(int.Parse(go[2]), go.Length > 3 ? go[3] : null);
        else if (go.Length > 2 && go[1] == "mixed") await MixedAsync(int.Parse(go[2]), go.Length > 3 ? go[3] : null, null, " routed");
        else if (go.Length > 1 && go[1] == "cost") await CostAsync();
        else await ExerciseAsync();
        return 0;

        static IPAddress? Word(string[] words, int at) => at < words.Length && IPAddress.TryParse(words[at], out var a) ? a : null;
    }
    case "reset":
        return Reset();
    case "guard":
        return await GuardAsync(Option("--seconds", 120));
    default:
        Console.Error.WriteLine("usage: Sallyport.Driver status | fetch | install | uninstall | adapters"
            + " | pass [--seconds N] [--crash-after S] [--batch N] [--mode send|both] | inject [--mode send|both]"
            + " | rtt [--count N] | attribute [--seconds N] | guard [--seconds N] [--may-remove]"
            + " | route <profile.ovpn> [--folder <game folder>] [--seconds N] [--workload boundaries|steady [--peer6 <link-local address>] [--oversized split|whole]]"
            + " [--loss hold|direct|stop] [--direct-after S] [--cut-after S --cut-for S] [--ping-restart S]"
            + " | reconnect <profile.ovpn> [--block-after S] [--block-for S]"
            + " | service-route <profile.ovpn> --child <a copy of this exe elsewhere> | --folder <game folder> [--workload steady] [--loss ...] [--seconds N]"
            + " | service-hello | service-malformed | service-failure <profile.ovpn> --child <exe> --case kill-worker|hang-worker|kill-supervisor|lease"
            + " | stop | reset");
        return 2;
}

// --mode send: only what Windows sends is taken; what arrives is left alone.
bool ReceivePath()
{
    var at = Array.IndexOf(args, "--mode");
    return !(at >= 0 && at + 1 < args.Length && args[at + 1] == "send");
}

string? Text(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
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

// Gate 4 and on: programs routed through the real VPN from their first packet, now across the tunnel's generations
// (item 2, steps 2 and 5): a TunnelSession keeps the tunnel up by the profile's policy, and each tunnel's pump commits
// its generation to the GameRoute and retires it when the tunnel goes, the loss policy (--loss, --direct-after) saying
// what the game's traffic does in between. Without --folder, a child process is selected and plays a workload while this
// one does the same direct (--workload: the exercise of gate 4, gate 5's boundaries, or steady STUN for an outage). With
// --folder, every program the game folder's scan includes is selected — a game played by its owner. --cut-after and
// --cut-for drop the server's packets at the adapter both ways for a while, inside the router's own interception (a
// second handle would reset its mode, gate 1): an outage, measured. The profile is read where its owner keeps it and
// nothing of it is printed (rule 2).
async Task<int> RouteAsync(string? profilePath, int seconds, string? folder, string? workload, IPAddress? peer6, bool split,
    LossPolicy loss, int cutAfter, int cutFor, int pingRestart)
{
    if (profilePath is null) { Console.Error.WriteLine("usage: Sallyport.Driver route <profile.ovpn> [--folder <game folder>] [--seconds N]"); return 2; }
    OvpnProfile profile;
    // The exception's type only: a parse error's message may quote the profile.
    try { profile = OvpnProfile.Parse(File.ReadAllText(profilePath)); }
    catch (Exception e) { Console.WriteLine($"could not read the profile ({e.GetType().Name})"); return 2; }

    ProgramSelection? selection = null;
    if (folder is not null)
    {
        var included = GameFolder.Scan(folder).Where(e => e.Included).Select(e => e.Path).ToList();
        if (included.Count == 0) { Console.WriteLine("the folder holds no program to route"); return 2; }
        selection = new ProgramSelection(included);
        Console.WriteLine($"selected: {included.Count} programs — {string.Join(", ", included.Select(Path.GetFileName))}");
    }

    using var driver = OpenDriver();
    if (driver is null) return 1;
    var adapter = RouteAdapter(driver);
    if (adapter is null) { Console.WriteLine("no adapter carries the route to the internet"); return 1; }
    var nic = Nic(adapter)?.GetIPProperties();
    var onLink = nic?.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(u => (BinaryPrimitives.ReadUInt32LittleEndian(u.Address.GetAddressBytes()), BinaryPrimitives.ReadUInt32LittleEndian(u.IPv4Mask.GetAddressBytes())))
        .ToList() ?? [];

    // The server's addresses, for a measurement's cut only; never printed.
    var server = new HashSet<uint>();
    if (cutFor > 0)
        foreach (var remote in profile.Remotes)
            try
            {
                foreach (var address in await Dns.GetHostAddressesAsync(remote.Host, AddressFamily.InterNetwork))
                    server.Add(BinaryPrimitives.ReadUInt32LittleEndian(address.GetAddressBytes()));
            }
            catch (SocketException) { }
    var cutting = false;
    bool Cut(DriverPacket packet)
    {
        if (!Volatile.Read(ref cutting)) return false;
        var frame = packet.Frame;
        if (frame.Length < 34 || BinaryPrimitives.ReadUInt16BigEndian(frame[12..]) != 0x0800) return false;
        return server.Contains(BinaryPrimitives.ReadUInt32LittleEndian(frame[(packet.Direction == PacketDirection.Send ? 30 : 26)..]));
    }

    var child = 0;
    Func<int, bool> selects = selection is not null ? selection.Selects : pid => pid == Volatile.Read(ref child);
    TunnelPump? current = null;
    var clock = Stopwatch.StartNew();
    string T() => $"{Now()} t={clock.Elapsed.TotalSeconds,5:F1}s";
    using var route = new GameRoute(driver, adapter, selects, loss, (generation, packet) =>
    {
        if (Volatile.Read(ref current) is { } pump && pump.Generation == generation) pump.Send(packet);
    }, onLink, cutFor > 0 ? Cut : null, receivePath: cutFor > 0);
    route.Router.SplitOversized = split;
    var routing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    route.PhaseChanged += phase =>
    {
        Console.WriteLine($"{T()}  route: {phase}");
        if (phase == RoutePhase.Routing) routing.TrySetResult();
    };

    // --ping-restart: this side's own, over the server's (pull-filter ignore).
    var policy = pingRestart > 0
        ? profile.Policy with { Ping = Math.Max(1, pingRestart / 4), PingRestart = pingRestart, KeepaliveOverridesServer = true }
        : profile.Policy;
    await using var session = new TunnelSession(profile, new TunnelSessionOptions
    {
        Policy = policy,
        Consumer = (tunnel, generation) =>
        {
            // What one outer datagram carries on this adapter: its MTU less the outer IPv4 header, the transport's (UDP
            // 8; TCP 20 and the 2-byte length each packet is framed with) and the data channel's own.
            var overhead = 20 + (tunnel.Remote.Proto == "tcp" ? 20 + 2 : 8) + DataChannel.Overhead;
            var pump = new TunnelPump(tunnel, generation, route, adapter.Mtu - overhead);
            Volatile.Write(ref current, pump);
            return pump;
        },
    });
    session.StatusChanged += status => Console.WriteLine($"{T()}  tunnel: {status.State} {status.Remote ?? "-"}, attempts {status.Attempts}, reconnects {status.Reconnects}"
        + (status.LastError is { } error ? $" ({error})" : ""));
    session.TunnelUp += (tunnel, generation) => Console.WriteLine($"{T()}  tunnel: generation {generation} up in {tunnel.ConnectTime.TotalSeconds:F1} s over {tunnel.Remote.Proto},"
        + $" tunnel address {tunnel.Config.Address}");
    session.TunnelDown += (generation, why) => Console.WriteLine($"{T()}  tunnel: generation {generation} down ({why ?? "stopped"})");
    session.ConsumerFailed += e => Console.WriteLine($"{T()}  tunnel: A GENERATION'S PUMP FAILED as it went ({e.GetType().Name}: {e.Message})");

    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    using var stopRouter = new CancellationTokenSource();
    // Under the Stop policy a lost tunnel ends the route, and the tunnel's retries with it.
    route.PhaseChanged += phase =>
    {
        if (phase == RoutePhase.Stopped && !stop.IsCancellationRequested) _ = Task.Run(() => stop.Cancel());
    };
    var running = Task.Factory.StartNew(() => route.Run(stopRouter.Token), TaskCreationOptions.LongRunning);
    var tunnelling = session.RunAsync(stop.Token);
    Console.WriteLine($"{T()}  route: adapter 0x{adapter.Handle:X} in mode {driver.Mode(adapter.Handle)}; loss: {loss.Mode}"
        + (loss.DirectAfter is { } d ? $", direct after {d.TotalSeconds:F0} s" : "") + $"; {onLink.Count} on-link network(s) blocked for selected programs");
    if (await Task.WhenAny(routing.Task, Task.Delay(TimeSpan.FromSeconds(60))) != routing.Task)
        Console.WriteLine($"{T()}  route: no tunnel within 60 s; the selected programs' traffic is held");

    var cut = cutFor > 0 ? Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(cutAfter));
        Volatile.Write(ref cutting, true);
        Console.WriteLine($"{T()}  CUT: the server's {server.Count} address(es) dropped both ways for {cutFor} s");
        await Task.Delay(TimeSpan.FromSeconds(cutFor));
        Volatile.Write(ref cutting, false);
        Console.WriteLine($"{T()}  FREED: {route.Router.Discarded} packets dropped by the cut");
    }) : Task.CompletedTask;

    // Each path's hops toward the game's server, by hop count: who answered there, and how soon.
    var hops = new Dictionary<(bool Vpn, int Ttl), (IPAddress From, RoundTrips Times)>();
    var tunnelTimes = new RoundTrips();
    string farthest = "(not traced yet)";
    IPAddress? target = null;

    if (selection is null)
    {
        var lanGateway = nic?.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var local = nic?.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var scope6 = nic?.GetIPv6Properties()?.Index ?? 0;
        var gateway = Volatile.Read(ref current)?.Tunnel.Config.Gateway;
        Console.WriteLine("direct — this process, not selected:");
        string go;
        switch (workload)
        {
            case "boundaries":
                await BoundariesAsync(gateway, lanGateway, local, peer6, scope6);
                go = $"go boundaries {gateway?.ToString() ?? "-"} {lanGateway?.ToString() ?? "-"} {local?.ToString() ?? "-"} {peer6?.ToString() ?? "-"} {scope6}";
                break;
            case "steady":
                var home = await SeenAsync();
                Console.WriteLine($"  udp: the far end sees this machine at {home ?? "-"}");
                go = $"go steady {Math.Max(10, seconds - 15)} {home ?? "-"}";
                break;
            case "cost":
                await CostAsync();
                go = "go cost";
                break;
            default:
                await ExerciseAsync();
                go = "go";
                break;
        }
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "routed-workload")
            { RedirectStandardInput = true, RedirectStandardOutput = true })!;
        // Selected before it sends anything: its first packet is the one this gate is about.
        Volatile.Write(ref child, process.Id);
        Console.WriteLine($"routed — child {process.Id}, selected:");
        await process.StandardInput.WriteLineAsync(go);
        while (await process.StandardOutput.ReadLineAsync() is { } line) Console.WriteLine($"{T()} {line}");
        await process.WaitForExitAsync();
        await Task.Delay(500);
        await stop.CancelAsync();
    }
    else
    {
        var pinging = PingAsync(stop.Token);
        long lastRouted = 0, lastUp = 0;
        // `Sallyport.Driver stop` ends the session from outside, summary and all: a game is played for as long as it is
        // played, not for a number of seconds chosen before.
        using var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        using var stopped = stop.Token.Register(() => stopSignal.Set());
        var router = route.Router;
        while (!stop.IsCancellationRequested)
        {
            if (stopSignal.WaitOne(10_000) && !stop.IsCancellationRequested)
            {
                Console.WriteLine($"{T()}  route: asked to stop");
                await stop.CancelAsync();
            }
            var routed = router.RoutedPackets;
            var up = router.HandedUp;
            Console.WriteLine($"{T()}  {route.Phase} (generation {route.Generation}): routed {routed} (+{routed - lastRouted}), handed up {up} (+{up - lastUp}),"
                + $" flows routed {router.RoutedFlows} (TCP {router.RoutedTcpFlows}, UDP {router.RoutedFlows - router.RoutedTcpFlows}),"
                + $" held {router.HeldWhileDown}, direct while down {router.DirectWhileDown}, late {router.LateFlows}, blocked {router.Blocked.Values.Sum()},"
                + $" mappings {router.Nat?.Count ?? 0}, refused from tunnel {router.Refused.Values.Sum()}, stale {route.Stale}");
            Console.WriteLine($"{T()}  server {Masked(target)}: {farthest}; the VPN server {tunnelTimes.Recent()};"
                + $" TCP handshakes {router.Handshakes.Recent()}; UDP replies {router.UdpReplies.Recent()}");
            lastRouted = routed;
            lastUp = up;
        }
        await pinging;
    }
    await tunnelling;
    route.Stop();
    await stopRouter.CancelAsync();
    await running;
    await cut;

    var r = route.Router;
    Console.WriteLine($"{Now()} route: routed {r.RoutedPackets}, direct {r.DirectPackets}, unowned {r.UnownedPackets}, held back {r.HeldBack},"
        + $" late flows {r.LateFlows} ({r.LatePackets} packets); handed up {r.HandedUp}, hand-up refused {r.HandUpRefused}; expired flows {r.ExpiredFlows},"
        + $" mappings {r.ExpiredMappings}; from the tunnel refused: " + (r.Refused.Count == 0 ? "none" : string.Join(", ", r.Refused.Select(x => $"{x.Key} {x.Value}"))));
    Console.WriteLine($"{Now()} route: while down: held {r.HeldWhileDown}, direct {r.DirectWhileDown}; with no tunnel to take them {r.NoTunnel}; stale generations {route.Stale};"
        + $" cut {r.Discarded}; owner-table refreshes {r.Owners.Refreshes}; {r.Interception}");
    Console.WriteLine($"{Now()} route: blocked {(r.Blocked.Count == 0 ? "none" : string.Join(", ", r.Blocked.Select(b => $"{b.Key} {b.Value}")))}"
        + $" ({r.BlockedFlows} flows), resets {r.ResetsSent}; too big {r.TooBig}, split {r.SplitPackets}, sent whole {r.OversizedWhole}; pieces routed {r.PiecesRouted},"
        + $" lone {r.LonePieces}, ahead from the tunnel {r.PiecesAhead} (lost {r.PiecesAheadLost}); ICMP errors handed up {r.ErrorsHandedUp}"
        + $" ({string.Join(", ", r.Errors.Select(e => $"type {e.Key.Type} code {e.Key.Code}: {e.Value}"))}); largest routed {r.LargestRouted} bytes");
    if (selection is not null)
    {
        Console.WriteLine($"selected processes: {string.Join(", ", selection.Selected.Select(pid => $"{pid} {ProcessName(pid)}"))}");
        Console.WriteLine($"processes whose program could not be read: {(selection.Unreadable.Count == 0 ? "none" : string.Join(", ", selection.Unreadable))}");
    }
    foreach (var (type, code, about) in r.LastErrors) Console.WriteLine($"  ICMP type {type} code {code} handed up, about {about}");
    foreach (var miss in r.SynMisses) Console.WriteLine($"  a SYN with no owner: {miss}");
    bool Mine(FlowDecision d) => d.Verdict is Verdict.Routed or Verdict.Blocked || (selection is null ? d.Pid == child : selection.Selected.Contains(d.Pid));
    var selected = r.Flows.Where(f => Mine(f.Value)).Select(f => (Text: $"{(f.Key.Protocol == Flow.Tcp ? "TCP" : "UDP")} :{f.Key.LocalPort} -> {f.Key.Remote}:{f.Key.RemotePort}", f.Value))
        .Concat(r.Flows6.Where(f => Mine(f.Value)).Select(f => (Text: $"{(f.Key.Protocol == Flow.Tcp ? "TCP" : "UDP")} :{f.Key.LocalPort} -> [{f.Key.Remote}]:{f.Key.RemotePort}", f.Value)))
        .ToList();
    Console.WriteLine($"the selected programs' flows ({selected.Count}), most packets first:");
    foreach (var (text, decision) in selected.OrderByDescending(f => f.Value.Packets).Take(40))
        Console.WriteLine($"  {decision.Verdict,-8} {decision.Packets,6} packets  {text}  pid {decision.Pid}"
            + (decision.Verdict == Verdict.Blocked ? $"  ({decision.Reason})" : decision.Verdict != Verdict.Routed ? "  <- LEFT DIRECT" : ""));
    if (selection is not null)
    {
        Console.WriteLine($"the whole session: the VPN server {tunnelTimes.All()}; TCP handshakes {r.Handshakes.All()}; UDP replies {r.UdpReplies.All()}");
        foreach (var vpn in new[] { true, false })
        {
            Console.WriteLine($"hops toward {Masked(target)} {(vpn ? "through the VPN" : "direct")}:");
            lock (hops)
                foreach (var ((_, ttl), (from, times)) in hops.Where(h => h.Key.Vpn == vpn).OrderBy(h => h.Key.Ttl))
                    Console.WriteLine($"  {ttl,2}  {Masked(from),-16} {times.All()}");
        }
    }
    Console.WriteLine($"tunnel: {session.Status.State}, {session.Status.Reconnects} reconnect(s), {session.Status.BytesOut} bytes out, {session.Status.BytesIn} in");
    return 0;

    // Every 5 s, the game's busiest UDP peer — its server, by its own traffic — is traced both ways at once, all hop
    // counts together: echoes through the tunnel from the tunnel's address, and direct ones. A game's server need not
    // answer a ping (Zenless Zone Zero's answers nothing but the game, measured), so the farthest hop that does answer
    // on each path stands for the game's ping on it. And the tunnel's own round trip, to its gateway.
    async Task PingAsync(CancellationToken ct)
    {
        const int MostHops = 24;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
            if (Volatile.Read(ref current) is not { } pump || route.Router.Nat?.BusiestPeer(Flow.Udp) is not { } peer) continue;
            target = new IPAddress(peer);
            var echo = pump.Echo;
            var sent = Enumerable.Range(1, MostHops).ToDictionary(ttl => ttl, ttl => echo.Send(peer, (byte)ttl));
            var gateway = pump.Tunnel.Config.Gateway is { } g ? echo.Send(BinaryPrimitives.ReadUInt32LittleEndian(g.GetAddressBytes())) : (ushort?)null;
            var direct = await Task.WhenAll(Enumerable.Range(1, MostHops).Select(ttl => DirectHopAsync(target, ttl)));
            await Task.Delay(500, CancellationToken.None);

            (int Ttl, IPAddress From, double Ms)? far = null, farDirect = null;
            lock (hops)
            {
                foreach (var (ttl, sequence) in sent)
                    if (echo.Answer(sequence) is { } answer)
                    {
                        Note(true, ttl, answer.From, answer.Ms);
                        if (far is null || ttl > far.Value.Ttl) far = (ttl, answer.From, answer.Ms);
                    }
                foreach (var (ttl, from, ms) in direct.OfType<(int, IPAddress, double)>())
                {
                    Note(false, ttl, from, ms);
                    if (farDirect is null || ttl > farDirect.Value.Ttl) farDirect = (ttl, from, ms);
                }
            }
            if (gateway is { } at && echo.Answer(at) is { } reply) tunnelTimes.Add(reply.Ms);
            farthest = $"farthest hop answering through the VPN {Hop(far)}, direct {Hop(farDirect)}";
        }

        void Note(bool vpn, int ttl, IPAddress from, double ms)
        {
            if (!hops.TryGetValue((vpn, ttl), out var hop) || !hop.From.Equals(from)) hops[(vpn, ttl)] = hop = (from, new RoundTrips());
            hop.Times.Add(ms);
        }

        static string Hop((int Ttl, IPAddress From, double Ms)? hop) => hop is { } h ? $"{h.Ttl} ({Masked(h.From)}) {h.Ms:F0} ms" : "none";
    }

    static async Task<(int, IPAddress, double)?> DirectHopAsync(IPAddress target, int ttl)
    {
        using var ping = new Ping();
        var started = Stopwatch.GetTimestamp();
        try
        {
            // RoundtripTime is only filled for a reply from the target itself: timed here instead.
            var reply = await ping.SendPingAsync(target, 2000, new byte[32], new PingOptions(ttl, dontFragment: true));
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return reply.Status is IPStatus.TtlExpired or IPStatus.Success ? (ttl, reply.Address, ms) : null;
        }
        catch (PingException) { return null; }
    }

    // A server's address with its last octet hidden: enough to tell one from another in a log.
    static string Masked(IPAddress? address) => address is null ? "(no UDP peer yet)" : string.Join('.', address.ToString().Split('.')[..3]) + ".x";

    static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch (ArgumentException) { return "(exited)"; }
    }
}

// The pipe's cost (item 2, step 4): twenty STUN round trips one after another, and 20 MB downloaded over HTTPS —
// the same from a program routed in one process and one routed through the service.
static async Task CostAsync()
{
    var stun = new IPEndPoint((await Dns.GetHostAddressesAsync("stun.l.google.com", AddressFamily.InterNetwork))[0], 19302);
    using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
    {
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        var buffer = new byte[512];
        var times = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await udp.SendToAsync(StunBinding(), stun);
            using var give = new CancellationTokenSource(2000);
            try
            {
                await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), give.Token);
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (OperationCanceledException) { }
        }
        times.Sort();
        Console.WriteLine($"  cost: STUN {times.Count}/20 answered, round trip p50 {(times.Count > 0 ? times[times.Count / 2] : double.NaN):F1} ms,"
            + $" min {(times.Count > 0 ? times[0] : double.NaN):F1}, max {(times.Count > 0 ? times[^1] : double.NaN):F1}");
    }
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    var begun = Stopwatch.GetTimestamp();
    try
    {
        var bytes = await http.GetByteArrayAsync("https://speed.cloudflare.com/__down?bytes=20000000");
        var seconds = Stopwatch.GetElapsedTime(begun).TotalSeconds;
        Console.WriteLine($"  cost: downloaded {bytes.Length / 1e6:F1} MB in {seconds:F1} s, {bytes.Length / 1e6 / seconds:F2} MB/s");
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Console.WriteLine($"  cost: download failed ({e.GetType().Name})"); }
}

// What the far end of a UDP flow sees this machine as: one STUN binding request, its XOR-MAPPED-ADDRESS.
static async Task<string?> SeenAsync()
{
    var stun = new IPEndPoint((await Dns.GetHostAddressesAsync("stun.l.google.com", AddressFamily.InterNetwork))[0], 19302);
    using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    udp.Bind(new IPEndPoint(IPAddress.Any, 0));
    var buffer = new byte[512];
    for (var i = 0; i < 3; i++)
    {
        await udp.SendToAsync(StunBinding(), stun);
        using var give = new CancellationTokenSource(1500);
        try
        {
            var got = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), give.Token);
            return StunMapped(buffer.AsSpan(0, got.ReceivedBytes))?.Split(':')[0];
        }
        catch (OperationCanceledException) { }
    }
    return null;
}

// An outage seen from a selected program: a STUN request every 500 ms from one socket for as long as asked, and every
// 2 s how many were answered and at which address the far end saw it — the VPN's, or this machine's own (home).
static async Task SteadyAsync(int seconds, string? home)
{
    var stun = new IPEndPoint((await Dns.GetHostAddressesAsync("stun.l.google.com", AddressFamily.InterNetwork))[0], 19302);
    using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    udp.Bind(new IPEndPoint(IPAddress.Any, 0));
    var buffer = new byte[512];
    var clock = Stopwatch.StartNew();
    int sent = 0, answered = 0;
    var seen = new SortedSet<string>();
    var receiving = Task.Run(async () =>
    {
        while (clock.Elapsed.TotalSeconds < seconds + 2)
        {
            using var give = new CancellationTokenSource(1000);
            try
            {
                var got = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), give.Token);
                var address = StunMapped(buffer.AsSpan(0, got.ReceivedBytes))?.Split(':')[0];
                lock (seen)
                {
                    answered++;
                    if (address is not null) seen.Add(address == home ? "home" : "VPN");
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }
    });
    var next = 2.0;
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        await udp.SendToAsync(StunBinding(), stun);
        sent++;
        await Task.Delay(500);
        if (clock.Elapsed.TotalSeconds < next) continue;
        lock (seen)
        {
            Console.WriteLine($" steady: {answered} of {sent} answered in the last 2 s, the far end saw {(seen.Count == 0 ? "-" : string.Join(" and ", seen))}");
            answered = sent = 0;
            seen.Clear();
        }
        next += 2;
    }
    await receiving;
}

// UDP and TCP at once, from one program: a STUN request every 500 ms, and every 2 s an HTTPS request for the address the
// far end sees — said as the VPN's or this machine's own (home), never printed. With a proxy, only the HTTPS part, through it.
static async Task MixedAsync(int seconds, string? home, IWebProxy? proxy, string label)
{
    using var http = new HttpClient(new SocketsHttpHandler { Proxy = proxy, UseProxy = proxy is not null, PooledConnectionLifetime = TimeSpan.FromSeconds(10) })
    { Timeout = TimeSpan.FromSeconds(5) };
    var clock = Stopwatch.StartNew();
    int tcpOk = 0, tcpFailed = 0, udpSent = 0, udpAnswered = 0;
    var seen = new SortedSet<string>();
    string? lastFailure = null;
    Socket? udp = null;
    IPEndPoint? stun = null;
    Task receiving = Task.CompletedTask;
    if (proxy is null)
    {
        stun = new IPEndPoint((await Dns.GetHostAddressesAsync("stun.l.google.com", AddressFamily.InterNetwork))[0], 19302);
        udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        receiving = Task.Run(async () =>
        {
            var buffer = new byte[512];
            while (clock.Elapsed.TotalSeconds < seconds + 2)
            {
                using var give = new CancellationTokenSource(1000);
                try
                {
                    await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), give.Token);
                    Interlocked.Increment(ref udpAnswered);
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            }
        });
    }
    var asking = Task.Run(async () =>
    {
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            try
            {
                var address = (await http.GetStringAsync("https://api.ipify.org")).Trim();
                lock (seen) { tcpOk++; seen.Add(address == home ? "home" : "VPN"); }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                lock (seen) { tcpFailed++; lastFailure = e.GetType().Name; }
            }
            await Task.Delay(500);
        }
    });
    var next = 2.0;
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        if (udp is not null)
        {
            await udp.SendToAsync(StunBinding(), stun!);
            udpSent++;
        }
        await Task.Delay(500);
        if (clock.Elapsed.TotalSeconds < next) continue;
        lock (seen)
        {
            Console.WriteLine($"{label}: " + (udp is null ? "" : $"udp {Interlocked.Exchange(ref udpAnswered, 0)} of {udpSent} answered, ")
                + $"tcp {tcpOk} ok, {tcpFailed} failed{(tcpFailed > 0 ? $" ({lastFailure})" : "")}, the far end saw {(seen.Count == 0 ? "-" : string.Join(" and ", seen))}");
            udpSent = tcpOk = tcpFailed = 0;
            seen.Clear();
        }
        next += 2;
    }
    await asking;
    await receiving;
    udp?.Dispose();
}

// A STUN binding request (RFC 5389): type 0x0001, length 0, the magic cookie, a 12-byte transaction ID.
static byte[] StunBinding()
{
    var request = new byte[20];
    BinaryPrimitives.WriteUInt16BigEndian(request, 0x0001);
    BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), 0x2112A442);
    Random.Shared.NextBytes(request.AsSpan(8, 12));
    return request;
}

// XOR-MAPPED-ADDRESS (0x0020): family, port and IPv4 address, each XORed with the magic cookie's leading bytes.
static string? StunMapped(ReadOnlySpan<byte> response)
{
    for (var at = 20; at + 4 <= response.Length;)
    {
        var type = BinaryPrimitives.ReadUInt16BigEndian(response[at..]);
        var length = BinaryPrimitives.ReadUInt16BigEndian(response[(at + 2)..]);
        if (type == 0x0020 && length >= 8 && at + 12 <= response.Length && response[at + 5] == 1)
        {
            var port = BinaryPrimitives.ReadUInt16BigEndian(response[(at + 6)..]) ^ 0x2112;
            var address = BinaryPrimitives.ReadUInt32BigEndian(response[(at + 8)..]) ^ 0x2112A442;
            return $"{new IPAddress(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(address)))}:{port}";
        }
        at += 4 + ((length + 3) & ~3);
    }
    return null;
}

// Gate 5: the packet boundaries a game can reach, the same calls direct (this process) and routed (the child). What
// each far end does is beside it; what the router did with them is in the parent's counters after.
static async Task BoundariesAsync(IPAddress? tunnelGateway, IPAddress? lanGateway, IPAddress? local, IPAddress? peer6, long scope6)
{
    var stun = new IPEndPoint((await Dns.GetHostAddressesAsync("stun.l.google.com", AddressFamily.InterNetwork))[0], 19302);

    // A datagram as big as Ethernet carries whole (1472 + 28 = 1500): bigger than the tunnel's, so routed it is split
    // in two. Google's STUN server answers one this size (measured direct, 2026-10-20) and none bigger.
    using (var udp = Udp())
        Console.WriteLine($"  udp 1500 bytes:                   {await AskAsync(udp, stun, StunRequest(1472))}");
    // The same with don't-fragment: routed, it may not be split, so the router answers "fragmentation needed"; then once
    // more, as a program would after.
    using (var udp = Udp())
    {
        udp.DontFragment = true;
        Console.WriteLine($"  udp 1500 bytes, don't fragment:   {await AskAsync(udp, stun, StunRequest(1472))}; again: {await AskAsync(udp, stun, StunRequest(1472))}");
    }
    // A reply bigger than any one packet: Akamai's name server answers akamai.com's TXT records in 3,959 bytes, which
    // arrive in three pieces (measured direct, 2026-10-20).
    var akamai = new IPEndPoint((await Dns.GetHostAddressesAsync("a1-128.akam.net", AddressFamily.InterNetwork))[0], 53);
    using (var udp = Udp())
        for (var i = 0; i < 3; i++)
            Console.WriteLine($"  udp reply of 3,959 bytes:         {await AskAsync(udp, akamai, DnsQuery("akamai.com", 16), 3000)}");
    // A datagram bigger than Ethernet carries: Windows sends it in pieces, which the router routes after their first.
    // Quad9 answers a query padded to 3,000 bytes, so sent in pieces (measured direct, 7 of 7, 2026-10-20).
    var quad9 = new IPEndPoint(IPAddress.Parse("9.9.9.9"), 53);
    using (var udp = Udp())
        for (var i = 0; i < 3; i++)
            Console.WriteLine($"  udp of 3,000 bytes, in pieces:    {await AskAsync(udp, quad9, DnsQuery("example.com", 1, padTo: 3000))}");

    // ICMP errors: one hop's time exceeded (from the server routed; from the home router direct — a UDP socket does not
    // see it either way, the router's count does), and a closed port's unreachable, which a Windows UDP socket sees as
    // a reset on its next receive. Spaced past the one a second a Linux host sends each destination (icmp_ratelimit).
    using (var udp = Udp())
    {
        udp.Ttl = 1;
        for (var i = 0; i < 3; i++)
        {
            await udp.SendToAsync(new byte[32], new IPEndPoint(IPAddress.Parse("8.8.8.8"), 33434));
            await Task.Delay(1200);
        }
        Console.WriteLine("  udp with one hop to live:         sent 3, 1.2 s apart");
    }
    // Connected, as a game's socket to its server usually is: Windows reports the unreachable on the next receive.
    if (tunnelGateway is not null)
        using (var udp = Udp())
        {
            udp.Connect(new IPEndPoint(tunnelGateway, 33434));
            for (var i = 0; i < 5; i++)
                Console.WriteLine($"  udp to a closed port on the tunnel's gateway: {await AskAsync(udp, new IPEndPoint(tunnelGateway, 33434), new byte[32], 1200)}");
        }

    // TCP the size of an upload: every segment Windows sends is as big as the MSS the far end offered.
    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
    {
        var begun = Stopwatch.GetTimestamp();
        try
        {
            using var response = await http.PostAsync("https://speed.cloudflare.com/__up", new ByteArrayContent(new byte[2 * 1024 * 1024]));
            Console.WriteLine($"  tcp upload of 2 MB:               {(int)response.StatusCode} in {Stopwatch.GetElapsedTime(begun).TotalSeconds:F1} s");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Console.WriteLine($"  tcp upload of 2 MB:               failed: {e.GetType().Name}"); }
    }

    // This side of the tunnel: every host on the link, a group, this machine's own network. Bound to the adapter's own
    // address: unbound, Windows sends both by the adapter of lowest metric, not necessarily the one the router takes.
    // Each socket lives half a second past its send, as a program's does; one closed at once may leave no owner to
    // find (gate 3).
    using (var udp = Udp(local))
    {
        udp.EnableBroadcast = true;
        await udp.SendToAsync(new byte[32], new IPEndPoint(IPAddress.Broadcast, 9));
        await Task.Delay(500);
        Console.WriteLine("  udp broadcast:                    sent");
    }
    using (var udp = Udp(local))
    {
        if (local is not null) udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
        var search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\n\r\n");
        Console.WriteLine($"  udp multicast (SSDP search):      {await AskAsync(udp, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), search)}");
    }
    if (lanGateway is not null) Console.WriteLine($"  tcp to the home router, port 80:  {await ConnectAsync(new IPEndPoint(lanGateway, 80))}");

    // IPv6, which the tunnel does not carry: a link's every node, and one neighbour by TCP.
    using (var udp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp))
    {
        try
        {
            await udp6.SendToAsync(new byte[32], new IPEndPoint(new IPAddress(IPAddress.Parse("ff02::1").GetAddressBytes(), scope6), 9));
            await Task.Delay(500);
            Console.WriteLine("  udp6 to every node on the link:   sent");
        }
        catch (SocketException e) { Console.WriteLine($"  udp6 to every node on the link:   {e.SocketErrorCode}"); }
    }
    if (peer6 is not null) Console.WriteLine($"  tcp6 to a neighbour, port 80:     {await ConnectAsync(new IPEndPoint(new IPAddress(peer6.GetAddressBytes(), scope6), 80))}");

    static Socket Udp(IPAddress? bound = null)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(bound ?? IPAddress.Any, 0));
        return socket;
    }

    static async Task<string> AskAsync(Socket socket, IPEndPoint to, byte[] request, int waitMs = 2000)
    {
        var buffer = new byte[65536];
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var give = new CancellationTokenSource(waitMs);
            int received;
            if (socket.Connected)
            {
                await socket.SendAsync(request);
                received = await socket.ReceiveAsync(buffer, SocketFlags.None, give.Token);
            }
            else
            {
                await socket.SendToAsync(request, to);
                received = (await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), give.Token)).ReceivedBytes;
            }
            return $"answered, {received} bytes in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms";
        }
        catch (OperationCanceledException) { return $"no answer in {waitMs / 1000.0:F0} s"; }
        catch (SocketException e) { return $"{e.SocketErrorCode} after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"; }
    }

    static async Task<string> ConnectAsync(IPEndPoint to)
    {
        using var socket = new Socket(to.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var give = new CancellationTokenSource(5000);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await socket.ConnectAsync(to, give.Token);
            return $"connected in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms";
        }
        catch (OperationCanceledException) { return "no answer in 5 s"; }
        catch (SocketException e) { return $"{e.SocketErrorCode} after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"; }
    }

    // A binding request padded to size with an attribute of a comprehension-optional type (0x8000-0xFFFF), which a
    // server ignores (RFC 5389 §15).
    static byte[] StunRequest(int size)
    {
        var request = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(request, 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)(size - 20));
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), 0x2112A442);
        Random.Shared.NextBytes(request.AsSpan(8, 12));
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(20), 0x8FFF);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(22), (ushort)(size - 24));
        return request;
    }

    // A query without recursion, with EDNS(0) (RFC 6891) offering 4,096 bytes and asking for DNSSEC records; padded to
    // padTo bytes with the padding option (12, RFC 7830).
    static byte[] DnsQuery(string name, ushort type, int padTo = 0)
    {
        var query = new List<byte>();
        var header = new byte[12];
        Random.Shared.NextBytes(header.AsSpan(0, 2));
        header[5] = 1;    // one question
        header[11] = 1;   // one additional record: the OPT
        query.AddRange(header);
        foreach (var label in name.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(Encoding.ASCII.GetBytes(label));
        }
        query.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        var fill = Math.Max(0, padTo - query.Count - 11 - 4);
        var options = padTo == 0 ? 0 : 4 + fill;
        query.AddRange([0, 0, 41, 0x10, 0x00, 0, 0, 0x80, 0, (byte)(options >> 8), (byte)options]);
        if (padTo != 0)
        {
            query.AddRange([0, 12, (byte)(fill >> 8), (byte)fill]);
            query.AddRange(new byte[fill]);
        }
        return [.. query];
    }
}

// Item 2, steps 3 and 4: a run through the router service, this process as its client — the tunnel held here, by a
// TunnelSession, as the client holds it; each generation committed over the run's pipe and the packets crossing it both
// ways. The selected program is a copy of this executable kept outside Sallyport's folder (--child; the service
// refuses to route Sallyport's own), or a game folder's programs (--folder). The profile is never printed (rule 2).
async Task<int> ServiceRouteAsync(string? profilePath, int seconds, string? childExe, string? folder, string? workload, LossPolicy loss,
    int cutAfter, int cutFor, int pingRestart, int proxyPort)
{
    if (profilePath is null || (childExe is null && folder is null)) { Console.Error.WriteLine("usage: Sallyport.Driver service-route <profile.ovpn> --child <exe> | --folder <game folder>"); return 2; }
    OvpnProfile profile;
    try { profile = OvpnProfile.Parse(File.ReadAllText(profilePath)); }
    catch (Exception e) { Console.WriteLine($"could not read the profile ({e.GetType().Name})"); return 2; }
    var executables = folder is not null ? GameFolder.Scan(folder).Where(e => e.Included).Select(e => e.Path).ToList() : [Path.GetFullPath(childExe!)];
    var clock = Stopwatch.StartNew();
    string T() => $"{Now()} t={clock.Elapsed.TotalSeconds,5:F1}s";

    await using var client = await RouterClient.ConnectAsync(AppContext.BaseDirectory, "driver-tool", TimeSpan.FromSeconds(5));
    Console.WriteLine($"{T()}  service: version {client.Welcome.Version}, build {client.Welcome.Build}, driver {(client.Welcome.DriverReady ? "ready" : client.Welcome.DriverProblem)}");
    client.RunEnded += ended => Console.WriteLine($"{T()}  service: RUN ENDED — {ended.Reason}");
    var prepared = Stopwatch.StartNew();
    await using var run = await client.PrepareAsync(executables, loss, AppContext.BaseDirectory);
    Console.WriteLine($"{T()}  service: run {run.RunId[..8]} prepared and attached in {prepared.ElapsedMilliseconds} ms, on {run.Attached.Adapter} (MTU {run.Attached.AdapterMtu}),"
        + $" {executables.Count} program(s), loss {loss.Mode}");
    var lastPhase = (RoutePhase?)null;
    run.StateChanged += state =>
    {
        if (state.Phase != lastPhase) Console.WriteLine($"{T()}  route: {state.Phase} (generation {state.Generation})");
        lastPhase = state.Phase;
    };

    RunPump? current = null;
    var policy = pingRestart > 0
        ? profile.Policy with { Ping = Math.Max(1, pingRestart / 4), PingRestart = pingRestart, KeepaliveOverridesServer = true }
        : profile.Policy;
    await using var session = new TunnelSession(profile, new TunnelSessionOptions { Policy = policy });
    TunnelDispatcher? lastDispatcher = null;
    session.DispatcherUp += dispatcher =>
    {
        lastDispatcher = dispatcher;
        Volatile.Write(ref current, new RunPump(dispatcher, run));
    };
    session.StatusChanged += status => Console.WriteLine($"{T()}  tunnel: {status.State} {status.Remote ?? "-"}, attempts {status.Attempts}, reconnects {status.Reconnects}"
        + (status.LastError is { } error ? $" ({error})" : ""));
    // --proxy: the same login serves a SOCKS5 proxy beside the routed programs (docs/design/window.md, step 3).
    await using var proxy = proxyPort > 0 ? new Sallyport.Proxy.Socks5Server(new SessionConnector(session), new IPEndPoint(IPAddress.Loopback, proxyPort)) : null;
    proxy?.Start();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    session.TunnelUp += (tunnel, generation) => Console.WriteLine($"{T()}  tunnel: generation {generation} up, tunnel address {tunnel.Config.Address}");
    // Under the Stop policy the route is over when its tunnel is lost: so are the tunnel's retries.
    run.StateChanged += state =>
    {
        if (state.Phase == RoutePhase.Stopped && !stop.IsCancellationRequested) _ = Task.Run(() => stop.Cancel());
    };
    var tunnelling = session.RunAsync(stop.Token);
    // An outage, measured: the server's addresses blocked both ways by Windows Firewall for a while — the router's
    // adapter belongs to the service's worker, which a second handle would disturb (gate 1). Never printed.
    var cut = cutFor > 0 ? Task.Run(async () =>
    {
        var addresses = new List<string>();
        foreach (var remote in profile.Remotes)
            try { addresses.AddRange((await Dns.GetHostAddressesAsync(remote.Host, AddressFamily.InterNetwork)).Select(a => a.ToString())); }
            catch (SocketException) { }
        await Task.Delay(TimeSpan.FromSeconds(cutAfter));
        Firewall("delete rule name=\"Sallyport measurement cut\"");
        foreach (var direction in new[] { "out", "in" })
            Firewall($"add rule name=\"Sallyport measurement cut\" dir={direction} action=block protocol=any remoteip={string.Join(',', addresses.Distinct())}");
        Console.WriteLine($"{T()}  CUT: the server's {addresses.Distinct().Count()} address(es) blocked both ways by the firewall for {cutFor} s");
        try { await Task.Delay(TimeSpan.FromSeconds(cutFor)); }
        finally
        {
            Firewall("delete rule name=\"Sallyport measurement cut\"");
            Console.WriteLine($"{T()}  FREED");
        }
    }) : Task.CompletedTask;
    var sending = RunPump.ForwardAsync(run, () => Volatile.Read(ref current));

    if (childExe is not null)
    {
        while (lastPhase != RoutePhase.Routing && !stop.IsCancellationRequested) await Task.Delay(100);
        Console.WriteLine("direct — this process, not selected:");
        string go;
        Task proxying = Task.CompletedTask;
        if (workload is "steady" or "mixed")
        {
            var home = await SeenAsync();
            Console.WriteLine($"  udp: the far end sees this machine at {(home is null ? "-" : "its own address (home)")}");
            go = $"go {workload} {Math.Max(10, seconds - 15)} {home ?? "-"}";
            if (proxyPort > 0)
                proxying = MixedAsync(Math.Max(10, seconds - 15), home, new WebProxy($"socks5://127.0.0.1:{proxyPort}"), "  proxy");
        }
        else if (workload == "cost")
        {
            await CostAsync();
            go = "go cost";
        }
        else
        {
            await ExerciseAsync();
            go = "go";
        }
        using var process = Process.Start(new ProcessStartInfo(childExe, "routed-workload") { RedirectStandardInput = true, RedirectStandardOutput = true })!;
        Console.WriteLine($"routed — child {process.Id} ({Path.GetFileName(Path.GetDirectoryName(childExe))}\\{Path.GetFileName(childExe)}), selected by its path:");
        await process.StandardInput.WriteLineAsync(go);
        while (await process.StandardOutput.ReadLineAsync() is { } line) Console.WriteLine($"{T()} {line}");
        await process.WaitForExitAsync();
        await proxying;
        await Task.Delay(500);
        if (lastDispatcher is { } d)
        {
            var c = d.Counters;
            var n = d.Network.Counters;
            Console.WriteLine($"{T()}  dispatcher (generation {d.Generation}): to the proxy {c.ToProxy}, to the router {c.ToRouter}, unclaimed {c.Unclaimed}, not for us {c.NotForUs};"
                + $" proxy: {n.ConnectionsOpened} connections, {n.ConnectsFailed} failed, {n.Lookups} lookups ({n.LookupsFromCache} cached), {n.PacketsDropped} dropped");
        }
        await stop.CancelAsync();
    }
    else
    {
        using var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        using var stopped = stop.Token.Register(() => stopSignal.Set());
        while (!stop.IsCancellationRequested)
        {
            if (stopSignal.WaitOne(10_000) && !stop.IsCancellationRequested) await stop.CancelAsync();
            if (run.State is { } s)
                Console.WriteLine($"{T()}  {s.Phase} (generation {s.Generation}): routed {s.Routed}, handed up {s.HandedUp}, flows routed {s.RoutedFlows},"
                    + $" held {s.HeldWhileDown}, direct while down {s.DirectWhileDown}, late {s.LateFlows}, blocked {s.Blocked}, stale {s.Stale}, selected {s.Selected}");
        }
    }
    await tunnelling;
    await cut;
    var final = run.State;
    await client.StopAsync(run.RunId);
    await run.DisposeAsync();
    await sending;
    if (final is { } f)
        Console.WriteLine($"{Now()} route: {f.Phase}; routed {f.Routed}, handed up {f.HandedUp}, flows routed {f.RoutedFlows}, unowned packets {f.Unowned}, held while down {f.HeldWhileDown},"
            + $" direct while down {f.DirectWhileDown}, late flows {f.LateFlows}, blocked {f.Blocked}, stale {f.Stale}; selected processes {f.Selected}, unreadable {f.Unreadable}");
    Console.WriteLine($"tunnel: {session.Status.State}, {session.Status.Reconnects} reconnect(s), {session.Status.BytesOut} bytes out, {session.Status.BytesIn} in");
    return 0;
}

static void Firewall(string arguments)
{
    using var netsh = Process.Start(new ProcessStartInfo("netsh", "advfirewall firewall " + arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
    netsh.WaitForExit();
}

// Step 3: the service's front door, as this process meets it — who it says it is, or why it refused.
async Task<int> ServiceHelloAsync()
{
    try
    {
        await using var client = await RouterClient.ConnectAsync(AppContext.BaseDirectory, "driver-tool", TimeSpan.FromSeconds(5));
        Console.WriteLine($"welcomed: version {client.Welcome.Version}, build {client.Welcome.Build}, driver {(client.Welcome.DriverReady ? "ready" : client.Welcome.DriverProblem)},"
            + $" run {client.Welcome.RunId ?? "none"}");
        return 0;
    }
    catch (RouterRefused e) { Console.WriteLine($"refused: {e.Message}"); return 1; }
    catch (TimeoutException) { Console.WriteLine("no answer: the service's pipe did not open within 5 s"); return 1; }
}

// Step 3: what the service does with a client of its own that misbehaves — a version it does not speak, a frame past
// the bound, JSON it cannot read. Each must end that connection and nothing else.
async Task<int> ServiceMalformedAsync()
{
    foreach (var (what, send) in new (string, Func<Stream, Task>)[]
    {
        ("hello in version 99", async pipe => await new FrameStream(pipe).SendAsync(new Hello(99, "driver-tool"))),
        ("a frame of 50,000,000 bytes", async pipe =>
        {
            await new FrameStream(pipe).SendAsync(new Hello(RouterProtocol.Version, "driver-tool"));
            await new FrameStream(pipe).ReadAsync(CancellationToken.None);
            var head = new byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(head, 50_000_000);
            head[4] = RouterProtocol.ControlFrame;
            await pipe.WriteAsync(head);
        }),
        ("JSON it cannot read", async pipe =>
        {
            await new FrameStream(pipe).SendAsync(new Hello(RouterProtocol.Version, "driver-tool"));
            await new FrameStream(pipe).ReadAsync(CancellationToken.None);
            var body = "{\"type\":\"prepareRun\",\"executables\":"u8.ToArray();
            var head = new byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(head, 1 + body.Length);
            head[4] = RouterProtocol.ControlFrame;
            await pipe.WriteAsync(head);
            await pipe.WriteAsync(body);
        }),
    })
    {
        using var pipe = new NamedPipeClientStream(".", RouterProtocol.ControlPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await send(pipe);
        var answer = new List<string>();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var frames = new FrameStream(pipe);
            while (await frames.ReadAsync(wait.Token) is { } frame) answer.Add(frame.Message?.GetType().Name ?? "packet");
            answer.Add("then closed");
        }
        catch (OperationCanceledException) { answer.Add("still open after 5 s"); }
        catch (Exception e) when (e is IOException or EndOfStreamException) { answer.Add($"then closed ({e.GetType().Name})"); }
        Console.WriteLine($"{what}: {string.Join(", ", answer)}");
    }
    // And the front door still opens for a well-behaved client.
    return await ServiceHelloAsync();
}

// Step 3: a run that fails from outside. A client process routes a steady STUN child through the service; ten seconds
// into routing, the case is done to it, and this process watches the adapter through its own handle (reading, never
// setting) and the time until it holds no interception.
async Task<int> ServiceFailureAsync(string? profilePath, string? childExe, string? failure)
{
    if (profilePath is null || childExe is null || failure is null) { Console.Error.WriteLine("usage: Sallyport.Driver service-failure <profile.ovpn> --child <exe> --case <case>"); return 2; }
    var log = Path.Combine(Path.GetTempPath(), $"sallyport-failure-{Environment.ProcessId}.log");
    using var client = Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
        ["service-route", profilePath, "--child", childExe, "--workload", "steady", "--seconds", "70"]) { RedirectStandardOutput = true })!;
    var lines = new List<string>();
    var routing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var reading = Task.Run(async () =>
    {
        while (await client.StandardOutput.ReadLineAsync() is { } line)
        {
            lock (lines) lines.Add(line);
            if (line.Contains("route: Routing")) routing.TrySetResult();
        }
    });
    if (await Task.WhenAny(routing.Task, Task.Delay(TimeSpan.FromSeconds(60))) != routing.Task) { Console.WriteLine("never routing"); client.Kill(); return 1; }
    await Task.Delay(TimeSpan.FromSeconds(10));

    // Each look at the adapter opens a handle of its own and closes it: one held open would itself keep the driver from
    // giving the adapter back (measured, step 3) — and --hold keeps one open, to measure exactly that.
    var hold = args.Contains("--hold") ? OpenDriver() : null;
    ulong adapterHandle;
    using (var look = OpenDriver())
    {
        if (look is null) return 1;
        adapterHandle = look.InternetAdapter()!.Handle;
    }
    AdapterMode Mode()
    {
        using var look = PacketDriver.Open();
        return look.Mode(adapterHandle);
    }
    var serviceBefore = ServiceControl.Status(RouterProtocol.ServiceName).ProcessId;
    var worker = Process.GetProcessesByName("Sallyport.Service").FirstOrDefault(p => p.Id != serviceBefore);
    Console.WriteLine($"{Now()} before: adapter mode {Mode()}, service pid {serviceBefore}, worker pid {worker?.Id}{(hold is null ? "" : "; a handle of this process's own held open")}");
    var clock = Stopwatch.StartNew();
    switch (failure)
    {
        case "kill-worker": worker?.Kill(); break;
        case "hang-worker": if (worker is not null) Suspend(worker); break;
        case "kill-supervisor": Process.GetProcessById(serviceBefore).Kill(); break;
        case "lease": Suspend(client); break;
        default: Console.WriteLine($"no case {failure}"); client.Kill(); return 2;
    }
    Console.WriteLine($"{Now()} {failure}: done");
    TimeSpan? given = null;
    while (clock.Elapsed < TimeSpan.FromSeconds(20))
    {
        if (Mode() == AdapterMode.None) { given = clock.Elapsed; break; }
        await Task.Delay(50);
    }
    Console.WriteLine(given is { } g ? $"{Now()} the adapter held no interception {g.TotalSeconds:F2} s after" : $"{Now()} THE ADAPTER STILL INTERCEPTS 20 s after");
    if (failure == "lease") Resume(client);
    if (failure == "kill-supervisor")
    {
        var back = Stopwatch.StartNew();
        while (back.Elapsed < TimeSpan.FromSeconds(30) && ServiceControl.Status(RouterProtocol.ServiceName) is var (state, pid) && (state != ServiceState.Running || pid == serviceBefore))
            await Task.Delay(100);
        var (stateAfter, pidAfter) = ServiceControl.Status(RouterProtocol.ServiceName);
        Console.WriteLine($"{Now()} the service: {stateAfter}, pid {pidAfter}, {back.Elapsed.TotalSeconds:F1} s after the kill");
    }
    if (failure == "hang-worker" && worker is not null) Console.WriteLine($"{Now()} the frozen worker {(worker.HasExited ? "was ended" : "STILL EXISTS")}");
    hold?.Dispose();
    await client.WaitForExitAsync();
    await reading;
    Console.WriteLine("the client said:");
    foreach (var line in lines.Where(l => l.Contains("RUN ENDED") || l.Contains("route:") || l.Contains("steady:")).Select(l => "  " + l))
        Console.WriteLine(line);
    return given is null ? 1 : 0;

    static void Suspend(Process process) => _ = NtSuspendProcess(process.SafeHandle);
    static void Resume(Process process) => _ = NtResumeProcess(process.SafeHandle);
}

// Item 2, step 1: the connection policy against the real server. A remote that never answers (TEST-NET-1) is put in
// front of the profile's own, so every round must time out on it and go on; once the tunnel is up, the server's
// addresses are cut off at the adapter both ways for longer than its ping-restart, then freed — the tunnel must notice,
// keep trying in rounds, and come back. Nothing of the profile is printed, the server's addresses included (rule 2).
async Task<int> ReconnectAsync(string? profilePath, int blockAfter, int blockFor)
{
    if (profilePath is null) { Console.Error.WriteLine("usage: Sallyport.Driver reconnect <profile.ovpn> [--block-after S] [--block-for S]"); return 2; }
    OvpnProfile profile;
    try { profile = OvpnProfile.Parse(File.ReadAllText(profilePath)); }
    catch (Exception e) { Console.WriteLine($"could not read the profile ({e.GetType().Name})"); return 2; }
    using var driver = OpenDriver();
    if (driver is null) return 1;
    var adapter = RouteAdapter(driver);
    if (adapter is null) { Console.WriteLine("no adapter carries the route to the internet"); return 1; }

    var dead = new Remote("192.0.2.1", 1194, "udp");
    var policy = profile.Policy with { AttemptTimeout = TimeSpan.FromSeconds(5) };
    var withDead = profile.WithRemotes([dead, .. profile.Remotes]);
    Console.WriteLine($"{Now()} reconnect: {profile.Remotes.Count} remote(s) of the profile behind one that never answers; policy: wait {policy.RetryWait.TotalSeconds:F0} s"
        + $" to {policy.RetryWaitMax.TotalSeconds:F0} s, rounds {policy.RoundsMax?.ToString() ?? "unlimited"}, {(policy.RandomOrder ? "random order" : "in order")},"
        + $" attempt timeout {policy.AttemptTimeout.TotalSeconds:F0} s");

    var clock = Stopwatch.StartNew();
    await using var session = new TunnelSession(withDead, new TunnelSessionOptions { Policy = policy });
    var ups = 0;
    var up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var upAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    session.StatusChanged += status =>
        Console.WriteLine($"{Now()} t={clock.Elapsed.TotalSeconds,5:F1}s  {status.State,-12} {status.Remote ?? "-",-9} attempts {status.Attempts,3}, reconnects {status.Reconnects}"
            + (status.LastError is { } error ? $"  ({error})" : ""));
    session.TunnelUp += (tunnel, generation) =>
    {
        Console.WriteLine($"{Now()} t={clock.Elapsed.TotalSeconds,5:F1}s  generation {generation} up in {tunnel.ConnectTime.TotalSeconds:F1} s;"
            + $" the server pushed ping {tunnel.Config.Ping} s, ping-restart {tunnel.Config.PingRestart} s");
        (++ups == 1 ? up : upAgain).TrySetResult();
    };
    session.TunnelDown += (generation, why) => Console.WriteLine($"{Now()} t={clock.Elapsed.TotalSeconds,5:F1}s  generation {generation} down: {why ?? "stopped"}");
    using var stop = new CancellationTokenSource();
    var running = session.RunAsync(stop.Token);
    if (await Task.WhenAny(up.Task, Task.Delay(TimeSpan.FromSeconds(120))) != up.Task) { Console.WriteLine("never up in 120 s"); await stop.CancelAsync(); await running; return 1; }

    await Task.Delay(TimeSpan.FromSeconds(blockAfter));
    var blocked = new HashSet<uint>();
    foreach (var remote in profile.Remotes)
        try
        {
            foreach (var address in await Dns.GetHostAddressesAsync(remote.Host, AddressFamily.InterNetwork))
                blocked.Add(BinaryPrimitives.ReadUInt32LittleEndian(address.GetAddressBytes()));
        }
        catch (SocketException) { }
    long droppedOut = 0, droppedIn = 0;
    var cutAt = clock.Elapsed;
    Console.WriteLine($"{Now()} t={cutAt.TotalSeconds,5:F1}s  CUT: the server's {blocked.Count} address(es) dropped both ways for {blockFor} s");
    using (var cut = new Interception(driver, adapter.Handle, 64, receivePath: true))
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(blockFor))
        {
            cut.Wait(50);
            cut.Drain(packet =>
            {
                var frame = packet.Frame;
                if (frame.Length < 34 || BinaryPrimitives.ReadUInt16BigEndian(frame[12..]) != 0x0800) return false;
                var to = packet.Direction == PacketDirection.Send;
                if (!blocked.Contains(BinaryPrimitives.ReadUInt32LittleEndian(frame[(to ? 30 : 26)..]))) return false;
                if (to) droppedOut++; else droppedIn++;
                return true;
            });
        }
        cut.Stop();
    }
    var freedAt = clock.Elapsed;
    Console.WriteLine($"{Now()} t={freedAt.TotalSeconds,5:F1}s  FREED: dropped {droppedOut} out, {droppedIn} in");
    var back = await Task.WhenAny(upAgain.Task, Task.Delay(TimeSpan.FromSeconds(180))) == upAgain.Task;
    Console.WriteLine(back
        ? $"{Now()} reconnect: up again {(clock.Elapsed - freedAt).TotalSeconds:F1} s after the cut was freed"
        : $"{Now()} reconnect: NOT up again within 180 s of the cut being freed");
    await Task.Delay(TimeSpan.FromSeconds(5));
    await stop.CancelAsync();
    await running;
    return back ? 0 : 1;
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
// interception — its own handle first, then killing the interceptors (both measured to give the adapter back, gate
// 1). Removing the driver, also measured to give the network back (D18), only with --may-remove: over a game session
// the direct path can die for reasons of its own (Wi-Fi), and removal is not undone by the path coming back.
async Task<int> GuardAsync(int seconds)
{
    var mayRemove = args.Contains("--may-remove");
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
            else if (stage == 2 && mayRemove && dead >= TimeSpan.FromSeconds(18))
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

// One tunnel generation's side of a game route: it commits the generation, hands the route what the tunnel returns
// (but the echoes it sent itself), sends the tunnel what the route routed for it, and retires the generation when the
// session disposes it — the tunnel gone.
sealed class TunnelPump : IAsyncDisposable
{
    private readonly GameRoute _route;
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sending, _receiving;

    public TunnelPump(Tunnel tunnel, int generation, GameRoute route, int mtu)
    {
        Tunnel = tunnel;
        Generation = generation;
        _route = route;
        var address = tunnel.Config.Address is { AddressFamily: AddressFamily.InterNetwork } a ? BinaryPrimitives.ReadUInt32LittleEndian(a.GetAddressBytes()) : 0u;
        Echo = new TunnelEcho(address, Send);
        _sending = Task.Run(SendAsync);
        _receiving = Task.Run(ReceiveAsync);
        // A server that pushed no IPv4 address cannot take a NAT'd flow: the route stays as it is (held, or direct).
        if (address != 0) route.Commit(generation, address, mtu);
    }

    public Tunnel Tunnel { get; }
    public int Generation { get; }
    public TunnelEcho Echo { get; }

    public void Send(byte[] packet) => _out.Writer.TryWrite(packet);

    private async Task SendAsync()
    {
        try
        {
            await foreach (var packet in _out.Reader.ReadAllAsync(_stop.Token)) await Tunnel.SendAsync(packet, _stop.Token);
        }
        catch (Exception e) when (e is TunnelEnded or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            await foreach (var packet in Tunnel.Packets.ReadAllAsync(_stop.Token))
                if (!Echo.Answered(packet)) _route.FromTunnel(Generation, packet);
        }
        // The tunnel's packets end with what ended it; the session says why.
        catch (Exception e) when (e is OperationCanceledException or TunnelEnded) { }
    }

    public async ValueTask DisposeAsync()
    {
        _route.Retire(Generation);
        _out.Writer.TryComplete();
        await _stop.CancelAsync();
        await Task.WhenAll(_sending, _receiving);
        _stop.Dispose();
    }
}

// ICMP echoes sent into the tunnel from the tunnel's own address, outside the router, and what answers them picked out
// of what the tunnel returns before the router sees it (which would refuse it: it belongs to no mapping) — the echo
// reply, or a router's "time exceeded" quoting the echo when its hop limit ran out there.
sealed class TunnelEcho(uint tunnelAddress, Action<byte[]> send)
{
    private readonly ushort _id = (ushort)Environment.ProcessId;
    private readonly Lock _lock = new();
    private readonly Dictionary<ushort, long> _sent = [];
    private readonly Dictionary<ushort, (IPAddress From, double Ms)> _answers = [];
    private ushort _sequence;

    /// <summary>Sends an echo with <paramref name="ttl"/> hops to live; its sequence number, to look the answer up by.</summary>
    public ushort Send(uint destination, byte ttl = 64)
    {
        // IPv4 (20) and an echo request (type 8): identifier, sequence, 32 bytes.
        var packet = new byte[20 + 8 + 32];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[8] = ttl;
        packet[9] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), tunnelAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), destination);
        var icmp = packet.AsSpan(20);
        icmp[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(icmp[4..], _id);
        ushort sequence;
        lock (_lock)
        {
            sequence = ++_sequence;
            _sent[sequence] = Stopwatch.GetTimestamp();
        }
        BinaryPrimitives.WriteUInt16BigEndian(icmp[6..], sequence);
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], Fold(Sum(icmp)));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Fold(Sum(packet.AsSpan(0, 20))));
        send(packet);
        return sequence;
    }

    /// <summary>Who answered an echo and how soon; null while none has.</summary>
    public (IPAddress From, double Ms)? Answer(ushort sequence)
    {
        lock (_lock) return _answers.Remove(sequence, out var answer) ? answer : null;
    }

    /// <summary>Whether the packet answers one of these echoes: an echo reply (type 0) with this identifier, or a time
    /// exceeded (11) or unreachable (3) quoting one. Timed if so.</summary>
    public bool Answered(byte[] packet)
    {
        if (packet.Length < 28 || packet[0] != 0x45 || packet[9] != 1) return false;
        int at;
        if (packet[20] == 0) at = 24;
        // The quote: the echo's own IPv4 header (20), then its type, code, checksum, identifier, sequence.
        else if (packet[20] is 3 or 11 && packet.Length >= 28 + 20 + 8 && packet[28] == 0x45 && packet[28 + 9] == 1 && packet[48] == 8) at = 52;
        else return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at)) != _id) return false;
        var from = new IPAddress(packet.AsSpan(12, 4));
        lock (_lock)
            if (_sent.Remove(BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at + 2)), out var sent))
                _answers[BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at + 2))] = (from, Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
        return true;
    }

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
        return sum;
    }

    private static ushort Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}

static partial class Program
{
    // Freezing a process whole, for the measurements of a hung worker or client: ntdll's documented-by-use pair, here
    // only to measure (never in the product).
    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process);

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process);

    // A session-local name: only this user's own console can end its routing session.
    const string StopEventName = @"Local\Sallyport.Driver.route.stop";
}
