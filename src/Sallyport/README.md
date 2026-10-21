# Sallyport

An OpenVPN-compatible client in pure .NET that keeps the tunnel inside the process using it. No network adapter, no
administrator rights, no system routes: the tunnel ends in packets, a userspace TCP/IP network, and a loopback SOCKS5
proxy. Written from the protocol's public description, not from OpenVPN's code (MIT). Not affiliated with OpenVPN Inc.

The library behind [Sallyport](https://github.com/JiarongGu/Sallyport), the Windows app; it is not Windows-only.
Target: .NET 10 (`net10.0`). Dependencies: none beyond the base class library — TLS is `SslStream`, the ciphers
`AesGcm`.

## A route for one application

Keep a tunnel up behind a SOCKS5 proxy, reconnecting while it runs; point an application (or a browser, for the sites
that need it) at the proxy:

```csharp
using System.Net;
using Sallyport;
using Sallyport.Profile;
using Sallyport.Runtime;

var profile = OvpnProfile.Parse(File.ReadAllText(path));   // read in place; it holds a private key
// A profile that asks for a username and password (auth-user-pass) is given them here; without, it is refused.
var options = new TunnelSessionOptions { Tunnel = new TunnelOptions { Login = new Login(username, password) } };
await using var service = new TunnelService(profile, new IPEndPoint(IPAddress.Loopback, 1080),
    status => Console.WriteLine($"{status.State} {status.Remote}"), options);
await service.RunAsync(stoppingToken);   // ends when the token is cancelled, or the profile cannot be served
```

The proxy binds once the first tunnel is up and stays bound across reconnects; between two tunnels it answers
"network unreachable". `TunnelService.Status` says which.

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

`tls-crypt` (v1), signing in with a client certificate, a username and password (`auth-user-pass`, given as
`TunnelOptions.Login`), or both; TLS 1.2/1.3 through `SslStream`, AES-256-GCM / AES-128-GCM data packets (`P_DATA_V2`),
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
