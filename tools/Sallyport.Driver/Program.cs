using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sallyport.Windows.PacketFilter;

// Sallyport.Driver status | fetch | install | uninstall | adapters | pass | reset | guard
//   The packet driver (Windows Packet Filter, D18) as Sallyport manages it: NT Kernel's pinned release, fetched into
//   %LOCALAPPDATA%\Sallyport\drivers and checked, installed or removed with one administrator prompt.
//   adapters, pass, reset and guard are the router's first gate (docs/design/router.md): the driver's interface and
//   what undoes it. They need administrator, as opening the driver does.
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
        return Pass(Option("--seconds", 15), Option("--crash-after", 0), Option("--batch", 64));
    case "reset":
        return Reset();
    case "guard":
        return await GuardAsync(Option("--seconds", 120));
    default:
        Console.Error.WriteLine("usage: Sallyport.Driver status | fetch | install | uninstall | adapters"
            + " | pass [--seconds N] [--crash-after S] [--batch N] | reset | guard [--seconds N]");
        return 2;
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
// it does anything. --crash-after ends the process without a word to the driver, as a crash would.
int Pass(int seconds, int crashAfter, int batchSize)
{
    using var driver = OpenDriver();
    if (driver is null) return 1;
    var adapter = RouteAdapter(driver);
    if (adapter is null) { Console.WriteLine("no adapter carries the route to the internet"); return 1; }

    using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
    using var batch = new PacketBatch(batchSize);
    var up = new int[batchSize];
    var down = new int[batchSize];
    long sent = 0, sentBytes = 0, received = 0, receivedBytes = 0, reads = 0, emptyReads = 0, unknown = 0;
    long confirmed = 0, unconfirmed = 0, refused = 0;
    int maxBatch = 0, largestOut = 0, largestIn = 0;
    var readErrors = new Dictionary<int, int>();
    var cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;

    driver.SetPacketEvent(adapter.Handle, signal);
    driver.SetMode(adapter.Handle, AdapterMode.SentTunnel | AdapterMode.ReceiveTunnel);
    Console.WriteLine($"{Now()} pass: adapter 0x{adapter.Handle:X} in tunnel mode (now {driver.Mode(adapter.Handle)}), {seconds} s"
        + (crashAfter > 0 ? $", crashing at {crashAfter} s" : ""));

    var clock = Stopwatch.StartNew();
    var nextLine = 1;
    try
    {
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (crashAfter > 0 && clock.Elapsed.TotalSeconds >= crashAfter)
            {
                Console.WriteLine($"{Now()} pass: crashing now; the handle is left to Windows to close");
                Console.Out.Flush();
                Process.GetCurrentProcess().Kill();
            }
            signal.WaitOne(100);
            // Reset before draining: a packet queued after the reset signals again, so none is left waiting.
            signal.Reset();
            Drain();
            if (clock.Elapsed.TotalSeconds >= nextLine)
            {
                Console.WriteLine($"{Now()} t={nextLine,3}s  out {sent} ({sentBytes / 1024} KiB)  in {received} ({receivedBytes / 1024} KiB)"
                    + $"  reads {reads} (+{emptyReads} empty)  max batch {maxBatch}  passed on: confirmed {confirmed}, unconfirmed {unconfirmed}, refused {refused}"
                    + $"  unknown {unknown}  largest frame out {largestOut} in {largestIn}"
                    + (readErrors.Count > 0 ? $"  read errors {string.Join(",", readErrors.Select(e => $"{e.Key}x{e.Value}"))}" : ""));
                nextLine++;
            }
        }
    }
    finally
    {
        // New packets bypass this program first; then what was already queued goes on.
        driver.SetMode(adapter.Handle, AdapterMode.None);
        Drain();
        driver.SetPacketEvent(adapter.Handle, null);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuAtStart;
        Console.WriteLine($"{Now()} pass: done, mode {driver.Mode(adapter.Handle)}, queue {driver.QueueSize(adapter.Handle)};"
            + $" out {sent} in {received}; passed on: confirmed {confirmed}, unconfirmed {unconfirmed}, refused {refused}; unknown {unknown};"
            + $" largest frame out {largestOut} in {largestIn}; CPU {cpu.TotalMilliseconds:F0} ms over {clock.Elapsed.TotalSeconds:F1} s");
    }
    return 0;

    void Drain()
    {
        while (driver.Read(adapter.Handle, batch) is var count && count > 0)
        {
            reads++;
            maxBatch = Math.Max(maxBatch, count);
            int ups = 0, downs = 0;
            for (var i = 0; i < count; i++)
            {
                var packet = batch[i];
                switch (packet.Direction)
                {
                    case PacketDirection.Send:
                        down[downs++] = i; sent++; sentBytes += packet.Length; largestOut = Math.Max(largestOut, packet.Length);
                        break;
                    case PacketDirection.Receive:
                        up[ups++] = i; received++; receivedBytes += packet.Length; largestIn = Math.Max(largestIn, packet.Length);
                        break;
                    default: unknown++; break;
                }
            }
            Count(downs, () => driver.Send(adapter.Handle, batch, down.AsSpan(0, downs), toNetwork: true));
            Count(ups, () => driver.Send(adapter.Handle, batch, up.AsSpan(0, ups), toNetwork: false));
        }
        if (driver.LastReadError == PacketDriver.EmptyQueue) emptyReads++;
        else if (driver.LastReadError != 0) readErrors[driver.LastReadError] = readErrors.GetValueOrDefault(driver.LastReadError) + 1;
    }

    // Three answers a send can have (rule 6): the driver's own count, its silence, or its refusal.
    void Count(int asked, Func<int?> send)
    {
        if (asked == 0) return;
        try
        {
            if (send() is { } took) { confirmed += took; refused += asked - took; }
            else unconfirmed += asked;
        }
        catch (Win32Exception) { refused += asked; }
    }
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
