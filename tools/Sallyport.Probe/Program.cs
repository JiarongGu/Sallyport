using System.Net;
using Sallyport;
using Sallyport.Net;
using Sallyport.Profile;

// Connects to a real server and says how far it got. Prints nothing secret: no key and no profile text — the profile is
// read in place, never copied, and the push reply goes through Redact. What it does print to name the server — its
// certificate's subject — and every public address it is told (the egress's above all) is the owner's to mask before a
// measurement is written down; the addresses it masks itself (A.B.C.x).
// Exit code: 0 holds, 1 failed (the server answered and refused), 2 unverifiable (no answer, or could not try).

if (args.Length < 1 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.Error.WriteLine("usage: Sallyport.Probe <profile.ovpn> [--remote N] [--timeout seconds] [--data-half 0|1] [--no-dns] [--fetch] [--concurrent N] [--serve seconds] [--nat]");
    return 2;
}

OvpnProfile profile;
try { profile = OvpnProfile.Parse(File.ReadAllText(args[0])); }
catch (Exception e)
{
    Console.Error.WriteLine($"could not read the profile: {e.Message}");
    return 2;
}

var index = Option("--remote", 1) - 1;
var timeout = TimeSpan.FromSeconds(Option("--timeout", 30));
var options = new TunnelOptions { DataSendHalf = Option("--data-half", 0) };
Console.WriteLine($"profile: {profile.Remotes.Count} remote(s) [{string.Join(", ", profile.Remotes.Select(r => $"{r.Proto}/{r.Port}"))}]; "
    + $"tls-crypt {(profile.TlsCryptKey is null ? "absent" : "present")}; remote-cert-tls server {(profile.RemoteCertTlsServer ? "yes" : "no")}; "
    + $"auth-user-pass {(profile.AuthUserPass ? "yes" : "no")}");
if (index < 0 || index >= profile.Remotes.Count)
{
    Console.Error.WriteLine($"no remote {index + 1}: the profile lists {profile.Remotes.Count}");
    return 2;
}
var remote = profile.Remotes[index];
Console.WriteLine($"trying remote {index + 1} of {profile.Remotes.Count}: {remote.Proto}/{remote.Port}, timeout {timeout.TotalSeconds:0} s, data half {options.DataSendHalf}");

Tunnel tunnel;
try { tunnel = await Tunnel.ConnectAsync(profile, remote, timeout, options, step => Console.WriteLine($"  {step}")); }
catch (HandshakeFailed e)
{
    Console.WriteLine($"outcome: {e.Outcome.ToString().ToLowerInvariant()} at {e.Stage} — {e.Message}");
    return e.Outcome == Outcome.Failed ? 1 : 2;
}

await using (tunnel)
{
    Console.WriteLine($"server certificate: {tunnel.ServerSubject}");
    Console.WriteLine($"TLS: {tunnel.TlsProtocol}, {tunnel.CipherSuite}; connected in {tunnel.ConnectTime.TotalSeconds:0.0} s");
    Console.WriteLine($"pushed {tunnel.PushOptions.Count} option(s):");
    foreach (var option in tunnel.PushOptions) Console.WriteLine($"  {PushOptions.Redact(option)}");
    foreach (var message in tunnel.OtherMessages) Console.WriteLine($"other message: {message}");
    if (args.Contains("--no-dns"))
    {
        Console.WriteLine("outcome: holds — the server accepted this client and pushed its configuration (data channel not exercised)");
        return 0;
    }

    // Phase 2's measurement: an answer can only come back if both directions of the data channel are right.
    var resolver = tunnel.Config.DnsServers.FirstOrDefault() ?? IPAddress.Parse("1.1.1.1");
    var checks = new (string Label, IPAddress Server, string Name)[]
    {
        ("pushed resolver", resolver, "example.com"),
        // OpenDNS answers this name with the address the query came FROM — where the tunnel leaves the internet.
        ("egress address", IPAddress.Parse("208.67.222.222"), "myip.opendns.com"),
    };
    var answered = 0;
    foreach (var (label, server, name) in checks)
    {
        try
        {
            var answer = await TunnelDns.QueryAsync(tunnel, server, name, TimeSpan.FromSeconds(10));
            answered++;
            Console.WriteLine($"{label}: {name} via {server} -> [{string.Join(", ", answer.Addresses.Select(Addresses.Masked))}] rcode {answer.ResponseCode} "
                + $"in {answer.Elapsed.TotalMilliseconds:0} ms ({answer.OtherPacketsSkipped} other packet(s) skipped)");
        }
        catch (OperationCanceledException) { Console.WriteLine($"{label}: {name} via {server} -> no answer in 10 s"); }
        catch (TunnelEnded e) { Console.WriteLine($"{label}: the tunnel ended — {e.Message}"); break; }
    }
    var c = tunnel.Counters;
    Console.WriteLine($"counters: in {c.PacketsIn} pkt / {c.BytesIn} B, out {c.PacketsOut} pkt / {c.BytesOut} B, pings in {c.PingsIn} out {c.PingsOut}, "
        + $"rejected {c.Rejected}, opposite-direction {c.OppositeDirection}, replayed {c.Replayed}, control dropped {c.ControlDropped}, overflowed {c.Overflowed}");
    if (answered == checks.Length && args.Contains("--nat"))
        return await Nat.RunAsync(tunnel);
    if (answered == checks.Length && (args.Contains("--fetch") || args.Contains("--serve") || args.Contains("--concurrent")))
        return await Phase3.RunAsync(tunnel, args.Contains("--fetch"), Option("--serve", 0), Option("--concurrent", 0));
    if (answered == checks.Length)
    {
        Console.WriteLine("outcome: holds — DNS answered through the tunnel's data channel");
        return 0;
    }
    if (c.OppositeDirection > 0)
        Console.WriteLine($"outcome: failed — the server's packets open under this side's own sending key: try --data-half {1 - options.DataSendHalf}");
    else if (c.Rejected > 0)
        Console.WriteLine("outcome: failed — the server's data packets do not authenticate under either key half: the key derivation disagrees");
    else
        Console.WriteLine("outcome: unverifiable — nothing came back on the data channel, so it says nothing either way");
    return c.OppositeDirection > 0 || c.Rejected > 0 ? 1 : 2;
}

int Option(string name, int fallback)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var value) ? value : fallback;
}