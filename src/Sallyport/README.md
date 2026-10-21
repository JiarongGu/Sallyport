# Sallyport

An OpenVPN-compatible client in pure .NET that keeps the tunnel inside the process using it. No network adapter, no
administrator rights, no system routes: the tunnel ends in packets, a userspace TCP/IP network, and a loopback SOCKS5
proxy. Written from the protocol's public description, not from OpenVPN's code (MIT). Not affiliated with OpenVPN Inc.

The library behind [Sallyport](https://github.com/JiarongGu/Sallyport), the Windows app; it is not Windows-only.
Target: .NET 10 (`net10.0`). Dependencies: none beyond the base class library — TLS is `SslStream`, the ciphers
`AesGcm`.

## A route for one application

Keep a tunnel up behind a SOCKS5 proxy, reconnecting while it runs, in your own process; point an embedded browser (or
another program) at the proxy, and send your own requests straight through the tunnel:

```csharp
using Sallyport;
using Sallyport.Profile;
using Sallyport.Runtime;

var profile = OvpnProfile.Parse(File.ReadAllText(path));   // read in place; it holds a private key
await using var service = new TunnelService(profile, options: new TunnelSessionOptions
{
    // Asked before each connection, for a profile that signs in with a username and password (auth-user-pass):
    // keep them in your own store; a password changed meanwhile is the one the next reconnect uses.
    Credentials = async ct => new Login(await store.UsernameAsync(ct), await store.PasswordAsync(ct)),
    Log = line => logger.Write(line),   // each attempt and handshake step; never a key, a password or a token
});
service.StatusChanged += status => ShowRoute(status.State, status.Remote, status.LastError);
var running = service.RunAsync(stoppingToken);   // cancel the token to stop; nothing else to call

if (await service.Ready)   // true once the first tunnel is up and the proxy listening; false if it never came up
{
    browser.UseSocksProxy(service.ProxyEndpoint!);   // a free loopback port, chosen when none is given
    using var http = new HttpClient(service.CreateHttpHandler());   // no proxy hop: the tunnel's TCP and its DNS
    var exit = await http.GetStringAsync("https://api.ipify.org");
}
await running;
```

The proxy binds once the first tunnel is up and stays bound across reconnects; between two tunnels it, and the
handler, answer "network down". A reconnect is reported as `reconnecting`, never as a `stopped` between two `up`s.
When the run ends without ever coming up, `Status.State` is `refused` (the profile, the credentials, or the port
asked for was taken) or `stopped`, with `LastError` saying why; `Ready` is then false.

## The tunnel in your own code

```csharp
using Sallyport;
using Sallyport.Net;

await using var tunnel = await Tunnel.ConnectAsync(profile, profile.Remotes[0], TimeSpan.FromSeconds(30));
await using var network = new TunnelNetwork(tunnel);
var addresses = await network.ResolveAsync("example.com");        // the tunnel's DNS, never the machine's
await using var stream = await network.ConnectAsync(addresses[0], 443);
```

`Tunnel` alone gives IP packets in and out; `TunnelNetwork` adds outbound TCP and DNS over it; `Socks5Server` with a
`TunnelConnector` serves that network to other processes; `TunnelSession` keeps tunnels coming, one after another, as
the profile's connection policy says (`OvpnProfile.Policy`, overridable through `TunnelSessionOptions.Policy`).

## What it speaks

`tls-crypt` (v1), signing in with a client certificate, a username and password (`auth-user-pass`, asked for through
`TunnelSessionOptions.Credentials` or given as `TunnelOptions.Login`), or both; TLS 1.2/1.3 through `SslStream`, AES-256-GCM / AES-128-GCM data packets (`P_DATA_V2`),
keepalive, exit notices; UDP and TCP remotes. The server's certificate must chain to the profile's `<ca>`, be marked
for server use when the profile says `remote-cert-tls server`, and carry the name the profile gives in
`verify-x509-name` (`subject`, `name` or `name-prefix`).

Not yet: `tls-auth`, `tls-crypt-v2`, `P_DATA_V1`, renegotiation (a server's renegotiation ends the tunnel, and a session
connects again), and checks of the server by `peer-fingerprint`, `verify-hash` or `tls-verify`. What a profile asks for
that this version cannot do is refused before any packet is sent, never connected without: `Tunnel.Unsupported(profile)`
says why, so a host can say so when a profile is added; a missing username and password is refused the same way when
connecting. What only the server reveals — the older data format, a refusal of the credentials — ends the handshake
with a `HandshakeFailed` that names its stage and whether the server answered (`Outcome.Failed`) or nothing came back
(`Outcome.Unverifiable`).

## Keeping secrets

- A profile holds a private key and a static key. Read it where its owner keeps it; nothing here writes it out, logs
  it or puts it in an exception's message.
- `Login` prints as `Login (hidden)`. The server's pushed options can carry a session token: show them through
  `PushOptions.Redact`. A server's own words in a status or exception are at most 120 characters and never a
  challenge's state or a token.
- `ProfileStore` keeps imported profiles sealed through an `IProfileProtector` you give it (DPAPI on Windows, say);
  the library ships none.
- `Socks5Server` takes no credentials: any process that can reach its port can use the tunnel. Bind it to loopback.

## Licence and provenance

MIT. Written from the OpenVPN protocol's public description and the server's observable behaviour — not from
OpenVPN's source code, which is GPLv2 (OpenVPN 2) and AGPLv3 (OpenVPN 3) and is not used, ported or translated here.
"OpenVPN" is a registered trademark of OpenVPN Inc.; this is an independent, OpenVPN-*compatible* implementation.
