# Sallyport

An OpenVPN-compatible client in pure .NET that keeps the tunnel inside the process using it. No network adapter, no
administrator rights, no system routes: the tunnel ends in packets, a userspace TCP/IP network, and a loopback SOCKS5
proxy. Written from the protocol's public description, not from OpenVPN's code (MIT). Not affiliated with OpenVPN Inc.

## A route for one application

Keep a tunnel up behind a SOCKS5 proxy, reconnecting while it runs; point an application (or a browser, for the sites
that need it) at the proxy:

```csharp
using System.Net;
using Sallyport.Profile;
using Sallyport.Runtime;

var profile = OvpnProfile.Parse(File.ReadAllText(path));   // read in place; it holds a private key
await using var service = new TunnelService(profile, new IPEndPoint(IPAddress.Loopback, 1080),
    status => Console.WriteLine($"{status.State} {status.Remote}"));
await service.RunAsync(stoppingToken);                     // the port opens once the first tunnel is up
```

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
`TunnelConnector` serves that network to other processes.

## What it speaks

`tls-crypt` (v1), signing in with a client certificate, a username and password (`auth-user-pass`, given as
`TunnelOptions.Login`), or both; TLS 1.2/1.3 through `SslStream`, AES-256-GCM / AES-128-GCM data packets (`P_DATA_V2`),
keepalive, exit notices; UDP and TCP remotes. Not yet: `tls-auth`, `tls-crypt-v2`, `P_DATA_V1`, renegotiation (a
server's renegotiation ends the tunnel, and a session connects again). A profile that asks for one of these, or for a
username and password when none is given, is refused before any packet is sent (`Tunnel.Unsupported` says why).
