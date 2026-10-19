using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Xml.Linq;
using Sallyport;
using Sallyport.Profile;
using Windows.ApplicationModel;
using Windows.Networking.Vpn;
using Windows.Storage;

// sallyport-vpn route <profile.ovpn> --app <program.exe>... --prefix <a.b.c.d/n>... --for <1..600 s> [--probe <ip:port>]
//                     [--policy split|force] [--default-route allow]
//               list | log [clear] | panic
// Run through the package's execution alias, so it has the package's identity and its networkingVpnProvider capability.
//
// `route` connects the OpenVPN tunnel here, relays the listed programs' packets between it and the plug-in over
// loopback, and connects the VPN profile — under the guard (D13): the direct path is probed from this process, which is
// never a routed program, and a failure tears everything down.

const string VpnProfileName = "Sallyport";
var agent = new VpnManagementAgent();

switch (args.FirstOrDefault())
{
    case "route" when args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal):
        return await RouteAsync(args[1]);
    case "list":
        foreach (var p in await agent.GetProfilesAsync())
            Console.WriteLine($"{p.ProfileName}: {(p is VpnPlugInProfile plugin ? plugin.ConnectionStatus.ToString() : "not a plug-in profile")}");
        return 0;
    case "panic":
        // Every plug-in profile, disconnected and removed: the one command to run when anything looks wrong.
        foreach (var p in (await agent.GetProfilesAsync()).OfType<VpnPlugInProfile>())
            Console.WriteLine($"{p.ProfileName}: disconnect {await agent.DisconnectProfileAsync(p)}, remove {await agent.DeleteProfileAsync(p)}");
        return 0;
    case "log":
    {
        var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "plugin.log");
        if (args.Length >= 2 && args[1] == "clear") { File.Delete(path); Console.WriteLine("log cleared"); return 0; }
        Console.WriteLine(File.Exists(path) ? File.ReadAllText(path) : "no log yet");
        return 0;
    }
    default:
        return Fail("usage: sallyport-vpn route <profile.ovpn> --app <program.exe>... --prefix <a.b.c.d/n>... --for <1..600 s> [--probe <ip:port>]"
            + " | list | log [clear] | panic");
}

async Task<int> RouteAsync(string profilePath)
{
    var apps = Values("--app").Select(Path.GetFullPath).ToList();
    if (apps.Count == 0) return Fail("route needs at least one --app");
    if (apps.FirstOrDefault(a => !File.Exists(a)) is { } missing) return Fail($"no program at {missing}");
    List<(IPAddress Network, int Length)> routes;
    try { routes = [.. Values("--prefix").Select(Prefix)]; }
    catch (FormatException) { return Fail("a --prefix is a.b.c.d/n"); }
    if (routes.Count == 0) return Fail("route needs at least one --prefix");
    // A default route took the whole machine in the spike (D12): it is offered only to measure whether the app filter
    // confines it now, and only by name.
    var defaultRoute = routes.Any(r => r.Length == 0);
    if (defaultRoute && Values("--default-route").FirstOrDefault() != "allow")
        return Fail("a default route took the whole machine in the spike (D12): refused unless --default-route allow");
    var policy = Values("--policy").FirstOrDefault() ?? "split";
    if (policy is not ("split" or "force")) return Fail("--policy is split or force");
    if (!int.TryParse(Values("--for").FirstOrDefault(), out var seconds) || seconds is < 1 or > 600)
        return Fail("route needs --for <1..600 seconds>: an unguarded, unbounded route is not offered");
    var probe = IPEndPoint.Parse(Values("--probe").FirstOrDefault() ?? "1.1.1.1:443");
    if (routes.Any(r => r.Length > 0 && Covers(r, probe.Address))) return Fail("a --prefix covers the guard's probe: it must stay direct to mean anything");
    // Kept off the VPN whatever the routes: the guard's probe, which measures the direct path only while it is one, and
    // (below) the VPN server, which carries this bridge's own tunnel.
    var exclusions = new List<IPAddress> { probe.Address };

    OvpnProfile profile;
    // The profile's text is never echoed: it holds a private key.
    try { profile = OvpnProfile.Parse(await File.ReadAllTextAsync(profilePath)); }
    catch (Exception e) { return Fail($"could not read the profile: {e.GetType().Name}"); }
    if (Tunnel.Unsupported(profile) is { } why) return Fail($"this version cannot connect the profile: {why}");

    // A direct path that fails before connecting says nothing about the VPN; refuse rather than judge by it.
    if (!await Reaches(probe) && !await Reaches(probe) && !await Reaches(probe))
        return Fail($"the direct path to {probe} fails before connecting: not connecting");

    Tunnel? tunnel = null;
    foreach (var remote in profile.Remotes)
    {
        // Under the app filter an unlisted program's traffic to a routed prefix is refused, not sent direct (D12): a
        // prefix over the server would cut this bridge's own tunnel.
        IPAddress[] server;
        try { server = await Dns.GetHostAddressesAsync(remote.Host); }
        catch (SocketException) { Console.WriteLine($"tunnel: {remote.Proto}/{remote.Port}: its server name does not resolve"); continue; }
        if (server.Any(a => routes.Any(r => r.Length > 0 && Covers(r, a))))
            return Fail($"a --prefix covers the VPN server of {remote.Proto}/{remote.Port}: its tunnel would be cut");
        try
        {
            tunnel = await Tunnel.ConnectAsync(profile, remote, TimeSpan.FromSeconds(30));
            exclusions.AddRange(server.Where(a => a.AddressFamily == AddressFamily.InterNetwork));
            break;
        }
        catch (Exception e) { Console.WriteLine($"tunnel: {remote.Proto}/{remote.Port} did not connect ({e.GetType().Name})"); }
    }
    if (tunnel is null) return Fail("no remote connected");
    await using var connected = tunnel;
    if (tunnel.Config.Address is not { } address) return Fail("the server assigned no address");
    Console.WriteLine($"tunnel: up over {tunnel.Remote.Proto}/{tunnel.Remote.Port} in {tunnel.ConnectTime.TotalSeconds:0.0} s");

    using var relay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    // A datagram to a port that has gone makes Windows fail the NEXT receive with ConnectionReset; this socket must outlive
    // the plug-in's restarts.
    relay.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
    var token = RandomNumberGenerator.GetBytes(16);
    using var pumps = new CancellationTokenSource();
    var counts = new Counts();
    var relaying = Relay(relay, tunnel, token, counts, pumps.Token);

    foreach (var old in (await agent.GetProfilesAsync()).Where(p => p.ProfileName == VpnProfileName))
        await agent.DeleteProfileAsync(old);
    var configuration = new XElement("sallyport",
        new XElement("relay", new XAttribute("port", ((IPEndPoint)relay.Client.LocalEndPoint!).Port), new XAttribute("token", Convert.ToHexString(token))),
        new XElement("address", address.ToString()),
        new XElement("mtu", 1400),
        new XElement("policy", policy),
        defaultRoute ? new XElement("defaultRoute", "allow") : null,
        routes.Select(r => new XElement("prefix", $"{r.Network}/{r.Length}")),
        exclusions.Distinct().Select(a => new XElement("exclude", $"{a}/32")),
        apps.Select(a => new XElement("app", a)));
    var vpn = new VpnPlugInProfile
    {
        ProfileName = VpnProfileName,
        VpnPluginPackageFamilyName = Package.Current.Id.FamilyName,
        CustomConfiguration = configuration.ToString(SaveOptions.DisableFormatting),
        RememberCredentials = false,
        RequireVpnClientAppUI = false,
    };
    // The plug-in reads nothing from here (a server URI that is an address made ServerHostNameList throw, D12), but a
    // profile must name one.
    vpn.ServerUris.Add(new Uri("https://localhost"));
    var added = await agent.AddProfileFromObjectAsync(vpn);
    if (added != VpnManagementErrorStatus.Ok) return Fail($"the VPN profile was not added: {added}");
    Console.WriteLine($"vpn: {(defaultRoute ? "the default route" : $"{routes.Count} prefix(es)")}, {policy} policy, for {string.Join(", ", apps.Select(Path.GetFileName))}");

    int verdict;
    try { verdict = await GuardAsync(vpn, probe, seconds, tunnel); }
    catch (Exception e)
    {
        // The guard itself failed: whatever it was doing, the VPN comes down (D13).
        Console.WriteLine($"guard: failed ({e.GetType().Name}: {e.Message}); tearing down");
        verdict = 1;
    }
    finally
    {
        // Every profile of ours, whatever state the guard left it in: none may outlive this bridge.
        await TearDownAsync();
    }

    pumps.Cancel();
    // A tunnel that ended (the guard's own verdict, often) ends the relay with its reason; by now that is only worth saying.
    try { await relaying; }
    catch (OperationCanceledException) { }
    catch (Exception e) { Console.WriteLine($"relay: ended with the tunnel ({e.GetType().Name}: {e.Message})"); }
    Console.WriteLine($"relay: {counts.ToTunnel} packet(s) to the tunnel, {counts.ToPlugin} to the plug-in, {counts.Refused} refused (no hello, or another sender)");
    return verdict;
}

// Disconnects and removes the profile until it is gone, each step on its own: a step that throws must not stop the next.
async Task TearDownAsync()
{
    for (var attempt = 0; attempt < 10; attempt++)
    {
        VpnPlugInProfile? left;
        try { left = await Find(); }
        catch (Exception) { await Task.Delay(500); continue; }
        if (left is null) return;
        try { await agent.DisconnectProfileAsync(left); } catch (Exception) { }
        try { if (await agent.DeleteProfileAsync(left) == VpnManagementErrorStatus.Ok) return; } catch (Exception) { }
        await Task.Delay(1000);
    }
    Console.WriteLine("guard: the profile could not be removed - run: sallyport-vpn panic");
}

// Relays until stopped: the plug-in's packets into the tunnel, the tunnel's to the plug-in. Only the endpoint that sent
// "SPRT" and the run's token is heard; a datagram from anyone else on this machine is refused.
static Task Relay(UdpClient relay, Tunnel tunnel, byte[] token, Counts counts, CancellationToken ct)
{
    IPEndPoint? plugin = null;
    var fromPlugin = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult got;
            try { got = await relay.ReceiveAsync(ct); }
            catch (SocketException) { continue; }
            var datagram = got.Buffer;
            if (datagram.Length == 4 + token.Length && datagram.AsSpan(0, 4).SequenceEqual("SPRT"u8)
                && CryptographicOperations.FixedTimeEquals(datagram.AsSpan(4), token))
            {
                // A reconnected plug-in says hello again from a new port: the latest is the one to answer.
                Volatile.Write(ref plugin, got.RemoteEndPoint);
                Console.WriteLine("relay: the plug-in said hello");
                continue;
            }
            if (!got.RemoteEndPoint.Equals(Volatile.Read(ref plugin))) { Interlocked.Increment(ref counts.Refused); continue; }
            await tunnel.SendAsync(datagram, ct);
            Interlocked.Increment(ref counts.ToTunnel);
        }
    }, ct);
    var fromTunnel = Task.Run(async () =>
    {
        await foreach (var packet in tunnel.Packets.ReadAllAsync(ct))
        {
            if (Volatile.Read(ref plugin) is not { } to) continue;
            await relay.SendAsync(packet, to, ct);
            Interlocked.Increment(ref counts.ToPlugin);
        }
    }, ct);
    return Task.WhenAll(fromPlugin, fromTunnel);
}

// Connects the VPN profile, then watches the direct path until the time is up: three failed probes in a row (or four of
// the last six), the platform dropping the connection, the tunnel ending, Ctrl+C or the end of --for all end in a
// disconnect and the profile's removal.
async Task<int> GuardAsync(VpnPlugInProfile profile, IPEndPoint probe, int seconds, Tunnel tunnel)
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.WriteLine($"vpn: connect {await agent.ConnectProfileAsync(profile)}");
    var clock = Stopwatch.StartNew();
    var failures = 0;
    var recent = new Queue<bool>();
    string verdict;
    while (true)
    {
        if (stop.IsCancellationRequested) { verdict = "stopped by Ctrl+C"; break; }
        if (clock.Elapsed.TotalSeconds >= seconds) { verdict = $"--for {seconds} s elapsed"; break; }
        if (tunnel.Completion.IsCompleted) { verdict = "the tunnel ended"; break; }
        // Read afresh: a profile object's status is not promised to be live. A status that cannot be read is neither up nor
        // down; the probe below still guards while it cannot.
        if (clock.Elapsed.TotalSeconds > 3 && await StatusAsync() is { } now && now != VpnManagementConnectionStatus.Connected)
        {
            verdict = $"the platform reports {now}";
            break;
        }
        var ok = await Reaches(probe);
        failures = ok ? 0 : failures + 1;
        recent.Enqueue(ok);
        if (recent.Count > 6) recent.Dequeue();
        if (failures >= 3 || recent.Count == 6 && recent.Count(r => !r) >= 4)
        {
            verdict = $"the direct path to {probe} failed while connected: the VPN is taking traffic it must not";
            failures = Math.Max(failures, 3);
            break;
        }
        try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { }
    }
    var decided = clock.Elapsed.TotalSeconds;
    // Said before anything else is tried: a teardown that fails must not take the reason with it.
    Console.WriteLine($"guard: {verdict}, {decided:0.0} s after connecting");
    var disconnected = await agent.DisconnectProfileAsync(profile);
    // Removed only once the platform says it is down: removing while it still tears down is refused (measured, D13). A
    // profile object's handle can go stale while it does (E_HANDLE, measured): unreadable counts as not down yet.
    for (var i = 0; i < 20 && await StatusAsync() is not VpnManagementConnectionStatus.Disconnected; i++)
        await Task.Delay(500);
    var removed = VpnManagementErrorStatus.Other;
    for (var i = 0; i < 5 && (removed = await agent.DeleteProfileAsync(profile)) != VpnManagementErrorStatus.Ok; i++)
        await Task.Delay(1000);
    // Windows withdraws the VPN's routes a moment after the disconnect: the direct path is given time to come back.
    var recovery = Stopwatch.StartNew();
    var after = false;
    while (!(after = await Reaches(probe)) && recovery.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(500);
    Console.WriteLine($"guard: torn down by {clock.Elapsed.TotalSeconds:0.0} s: disconnect {disconnected}, remove {removed}; "
        + $"direct path {(after ? $"works within {recovery.Elapsed.TotalSeconds:0.0} s" : "STILL FAILS after 10 s")}");
    return failures >= 3 || !after ? 1 : 0;
}

async Task<VpnPlugInProfile?> Find() =>
    (await agent.GetProfilesAsync()).OfType<VpnPlugInProfile>().FirstOrDefault(p => p.ProfileName == VpnProfileName);

// The profile's status now; Disconnected when it is gone; null when it could not be read (rule 6).
async Task<VpnManagementConnectionStatus?> StatusAsync()
{
    try { return await Find() is { } found ? found.ConnectionStatus : VpnManagementConnectionStatus.Disconnected; }
    catch (Exception) { return null; }
}

static async Task<bool> Reaches(IPEndPoint endpoint)
{
    using var client = new TcpClient(endpoint.AddressFamily);
    // Half a second: a direct path that is up answers well inside it, and three misses must not take long to count.
    using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    try { await client.ConnectAsync(endpoint, limit.Token); return true; }
    catch (Exception) { return false; }
}

static (IPAddress Network, int Length) Prefix(string text)
{
    var parts = text.Split('/');
    if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork
        || !int.TryParse(parts[1], out var length) || length is < 0 or > 32)
        throw new FormatException();
    return (Masked(address, length), length);
}

static bool Covers((IPAddress Network, int Length) route, IPAddress address) =>
    address.AddressFamily == AddressFamily.InterNetwork && Masked(address, route.Length).Equals(route.Network);

static IPAddress Masked(IPAddress address, int length)
{
    var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    var mask = length == 0 ? 0u : uint.MaxValue << (32 - length);
    var bytes = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value & mask);
    return new IPAddress(bytes);
}

List<string> Values(string option)
{
    var found = new List<string>();
    for (var i = 0; i < args.Length - 1; i++) if (args[i] == option) found.Add(args[i + 1]);
    return found;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

sealed class Counts
{
    public long ToTunnel, ToPlugin, Refused;
}
