using Sallyport;
using Sallyport.Profile;

// Runs the control channel against a real server and says how far it got. Prints nothing secret: no key, no
// certificate, no host name — the profile is read in place, never copied, and the push reply goes through Redact.
// Exit code: 0 holds, 1 failed (the server answered and refused), 2 unverifiable (no answer, or could not try).

if (args.Length < 1 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.Error.WriteLine("usage: Sallyport.Probe <profile.ovpn> [--remote N] [--timeout seconds]");
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
Console.WriteLine($"profile: {profile.Remotes.Count} remote(s) [{string.Join(", ", profile.Remotes.Select(r => $"{r.Proto}/{r.Port}"))}]; "
    + $"tls-crypt {(profile.TlsCryptKey is null ? "absent" : "present")}; remote-cert-tls server {(profile.RemoteCertTlsServer ? "yes" : "no")}; "
    + $"auth-user-pass {(profile.AuthUserPass ? "yes" : "no")}");
if (index < 0 || index >= profile.Remotes.Count)
{
    Console.Error.WriteLine($"no remote {index + 1}: the profile lists {profile.Remotes.Count}");
    return 2;
}
var remote = profile.Remotes[index];
Console.WriteLine($"trying remote {index + 1} of {profile.Remotes.Count}: {remote.Proto}/{remote.Port}, timeout {timeout.TotalSeconds:0} s");

try
{
    var result = await Handshake.RunAsync(profile, remote, timeout, step => Console.WriteLine($"  {step}"));
    Console.WriteLine($"server certificate: {result.ServerSubject}");
    Console.WriteLine($"TLS: {result.TlsProtocol}, {result.CipherSuite}");
    Console.WriteLine($"pushed {result.PushOptions.Count} option(s):");
    foreach (var option in result.PushOptions) Console.WriteLine($"  {PushOptions.Redact(option)}");
    foreach (var message in result.OtherMessages) Console.WriteLine($"other message: {message}");
    Console.WriteLine($"dropped packets: {result.DroppedPackets}; elapsed {result.Elapsed.TotalSeconds:0.0} s");
    Console.WriteLine("outcome: holds — the server accepted this client and pushed its configuration");
    return 0;
}
catch (HandshakeFailed e)
{
    Console.WriteLine($"outcome: {e.Outcome.ToString().ToLowerInvariant()} at {e.Stage} — {e.Message}");
    return e.Outcome == Outcome.Failed ? 1 : 2;
}

int Option(string name, int fallback)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var value) ? value : fallback;
}
