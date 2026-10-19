using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security;
using Windows.ApplicationModel;
using Windows.Networking.Vpn;
using Windows.Storage;

// sallyport-vpnspike add <name> <server> <port> --prefix <a.b.c.d/n>... [--deadline <s>] <routed.exe>...
//                    connect <name> --for <s> [--probe <ip:port>]  |  disconnect|remove <name>  |  list  |  log [clear]  |  panic
// Run through the package's execution alias, so it has the package's identity and its networkingVpnProvider capability.
//
// The first run of this spike took the whole machine's network down (2026-10-19), so connecting is guarded twice over:
// the plug-in ends its own connection at a deadline and on any packet outside its prefixes, and `connect` here watches
// the DIRECT path — this console is never a routed program — and tears the connection down when it fails.

var agent = new VpnManagementAgent();
switch (args.FirstOrDefault())
{
    case "add" when args.Length >= 5:
    {
        var prefixes = Values("--prefix");
        if (prefixes.Count == 0) return Fail("add needs at least one --prefix");
        if (prefixes.Any(p => p.EndsWith("/0")) && Values("--default-route").FirstOrDefault() != "allow")
            return Fail("a default route force-tunnels the machine: refused unless --default-route allow");
        var deadline = Values("--deadline").FirstOrDefault() ?? "20";
        var filter = Values("--filter").FirstOrDefault() ?? "app";
        var allow = Values("--allow").FirstOrDefault() ?? "true";
        var policy = Values("--policy").FirstOrDefault() ?? "split";
        var protocols = Values("--protocols").FirstOrDefault() ?? "";
        var defaultRoute = Values("--default-route").FirstOrDefault() ?? "refuse";
        // An environment-variable or device-path form is passed through as written: the spike asks which forms match.
        var programs = Rest(4).Select(p => p.StartsWith('%') || p.StartsWith('\\') ? p : Path.GetFullPath(p)).ToList();
        var xml = $"<spike><server>{Esc(args[2])}</server><port>{Esc(args[3])}</port><deadline>{Esc(deadline)}</deadline><filter>{Esc(filter)}</filter><allow>{Esc(allow)}</allow><policy>{Esc(policy)}</policy><protocols>{Esc(protocols)}</protocols><defaultRoute>{Esc(defaultRoute)}</defaultRoute>"
            + string.Concat(prefixes.Select(p => $"<prefix>{Esc(p)}</prefix>"))
            + string.Concat(programs.Select(p => $"<route>{Esc(p)}</route>")) + "</spike>";
        var profile = new VpnPlugInProfile
        {
            ProfileName = args[1],
            VpnPluginPackageFamilyName = Package.Current.Id.FamilyName,
            CustomConfiguration = xml,
            RememberCredentials = false,
            RequireVpnClientAppUI = false,
        };
        profile.ServerUris.Add(new Uri($"https://{args[2]}"));
        // --profile-filter: the app filters and routes set on the PROFILE, as management (VPNv2's TrafficFilterList)
        // sets them, rather than by the plug-in at start — the plug-in's own app filters matched no desktop program
        // under any form of its path (measured).
        if (args.Contains("--profile-filter"))
        {
            foreach (var path in programs)
                profile.TrafficFilters.Add(new VpnTrafficFilter(new VpnAppId(VpnAppIdType.FilePath, path)));
            foreach (var prefix in prefixes)
            {
                var parts = prefix.Split('/');
                profile.Routes.Add(new VpnRoute(new Windows.Networking.HostName(parts[0]), byte.Parse(parts[1])));
            }
        }
        Console.WriteLine($"add: {await agent.AddProfileFromObjectAsync(profile)}");
        return 0;
    }
    case "connect" when args.Length >= 2:
        return await GuardedConnect(args[1]);
    case "disconnect" when args.Length >= 2:
        Console.WriteLine($"disconnect: {await agent.DisconnectProfileAsync(await Find(args[1]))}");
        return 0;
    case "remove" when args.Length >= 2:
        Console.WriteLine($"remove: {await agent.DeleteProfileAsync(await Find(args[1]))}");
        return 0;
    case "list":
        foreach (var p in await agent.GetProfilesAsync())
            Console.WriteLine($"{p.ProfileName}: {(p is VpnPlugInProfile plugin ? plugin.ConnectionStatus.ToString() : "not ours")}");
        return 0;
    case "panic":
        // Every profile of this plug-in, disconnected and removed: the one command to run when anything looks wrong.
        foreach (var p in await agent.GetProfilesAsync())
        {
            Console.WriteLine($"{p.ProfileName}: disconnect {await agent.DisconnectProfileAsync(p)}, remove {await agent.DeleteProfileAsync(p)}");
        }
        return 0;
    case "log":
    {
        var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "plugin.log");
        if (args.Length >= 2 && args[1] == "clear") { File.Delete(path); Console.WriteLine("log cleared"); return 0; }
        Console.WriteLine(File.Exists(path) ? File.ReadAllText(path) : $"no log yet at {path}");
        return 0;
    }
    default:
        return Fail("usage: sallyport-vpnspike add <name> <server> <port> --prefix <a.b.c.d/n>... [--deadline <s>] <routed.exe>... | "
            + "connect <name> --for <s> [--probe <ip:port>] | disconnect|remove <name> | list | log [clear] | panic");
}

// Connects, then watches the direct path until the time is up: three failed probes in a row, the platform dropping the
// connection, Ctrl+C or the end of --for all end in a disconnect and the profile's removal.
async Task<int> GuardedConnect(string name)
{
    if (!int.TryParse(Values("--for").FirstOrDefault(), out var seconds) || seconds is < 1 or > 300)
        return Fail("connect needs --for <1..300 seconds>: an unguarded connection is not offered");
    var probe = IPEndPoint.Parse(Values("--probe").FirstOrDefault() ?? "1.1.1.1:443");
    var profile = await Find(name);

    // A direct path that fails before connecting says nothing about the VPN; refuse rather than judge by it.
    if (!await Reaches(probe) && !await Reaches(probe) && !await Reaches(probe))
        return Fail($"the direct path to {probe} fails before connecting: not connecting");

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.WriteLine($"connect: {await agent.ConnectProfileAsync(profile)}");
    var clock = Stopwatch.StartNew();
    var failures = 0;
    var recent = new Queue<bool>();
    var trace = new System.Text.StringBuilder();
    string verdict;
    while (true)
    {
        if (stop.IsCancellationRequested) { verdict = "stopped by Ctrl+C"; break; }
        if (clock.Elapsed.TotalSeconds >= seconds) { verdict = $"--for {seconds} s elapsed"; break; }
        // Read afresh: a profile object's status is not promised to be live.
        if (clock.Elapsed.TotalSeconds > 3 && (await Find(name)) is VpnPlugInProfile p && p.ConnectionStatus != VpnManagementConnectionStatus.Connected)
        {
            verdict = $"the platform reports {p.ConnectionStatus}";
            break;
        }
        var probed = Stopwatch.StartNew();
        var ok = await Reaches(probe);
        trace.Append(ok ? '+' : '-');
        failures = ok ? 0 : failures + 1;
        recent.Enqueue(ok);
        if (recent.Count > 6) recent.Dequeue();
        // Three in a row, or most of the last six: a path that fails on and off is failing too.
        if (failures >= 3 || recent.Count == 6 && recent.Count(r => !r) >= 4)
        {
            verdict = $"the direct path to {probe} failed while connected ({failures} in a row, {recent.Count(r => !r)} of the last 6): the VPN is taking traffic it must not";
            failures = Math.Max(failures, 3);
            break;
        }
        try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { }
    }
    var decided = clock.Elapsed.TotalSeconds;
    var disconnected = await agent.DisconnectProfileAsync(profile);
    // Removed only once the platform says it is down: removing while it still tears down is refused (measured:
    // AlreadyConnected), and a profile left behind could be connected again from Settings.
    for (var i = 0; i < 20 && (await Find(name)) is VpnPlugInProfile { ConnectionStatus: not VpnManagementConnectionStatus.Disconnected }; i++)
        await Task.Delay(500);
    var removed = VpnManagementErrorStatus.Other;
    for (var i = 0; i < 5 && (removed = await agent.DeleteProfileAsync(profile)) != VpnManagementErrorStatus.Ok; i++)
        await Task.Delay(1000);
    // Windows withdraws the VPN's routes a moment after the disconnect: the direct path is given time to come back.
    var recovery = Stopwatch.StartNew();
    var after = false;
    while (!(after = await Reaches(probe)) && recovery.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(500);
    Console.WriteLine($"guard: {verdict}, {decided:0.0} s after connecting; torn down by {clock.Elapsed.TotalSeconds:0.0} s: disconnect {disconnected}, remove {removed}; "
        + $"direct path {(after ? $"works again within {recovery.Elapsed.TotalSeconds:0.0} s" : "STILL FAILS after 10 s")}");
    Console.WriteLine($"guard: probes (+ reached, - failed): {trace}");
    return failures >= 3 || !after ? 1 : 0;
}

static async Task<bool> Reaches(IPEndPoint endpoint)
{
    using var client = new TcpClient(endpoint.AddressFamily);
    // Half a second: a direct path that is up answers well inside it, and three misses must not take long to count.
    using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    try { await client.ConnectAsync(endpoint, limit.Token); return true; }
    catch (Exception) { return false; }
}

async Task<IVpnProfile> Find(string name) =>
    (await agent.GetProfilesAsync()).FirstOrDefault(p => p.ProfileName == name) ?? throw new ArgumentException($"no VPN profile named {name}");

List<string> Values(string option)
{
    var found = new List<string>();
    for (var i = 0; i < args.Length - 1; i++) if (args[i] == option) found.Add(args[i + 1]);
    return found;
}

// The positional arguments from `start` on, without the options and their values.
IEnumerable<string> Rest(int start)
{
    for (var i = start; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal)) { i++; continue; }
        yield return args[i];
    }
}

static string Esc(string text) => SecurityElement.Escape(text);

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
