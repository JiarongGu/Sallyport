# Architecture — proposal (2026-10-19)

**Status: approved 2026-10-19 (D9–D11); step 1, the restructure, done.** What Sallyport becomes so that a host other than Kobo can use it, and so that one
application — a game included — can be routed through a profile while the rest of the machine stays direct.

## What the references do

Four projects that put a tunnel under per-application routing, and the owner's own framework. Each is read for its
shape, never its code (CLAUDE.md rule 1 binds OpenVPN's source; these are cited for structure only).

| Project | Its layers | What Sallyport takes |
|---|---|---|
| WireGuard for Windows | a service per tunnel (LocalSystem) · a manager service that brokers for the UI over a pipe · an unelevated UI · configuration stored by the service | the UI never runs elevated; privileged work lives in a service the UI talks to |
| Mullvad VPN | a platform core (firewall, routing, split tunnel) · a daemon with a management API · CLI and GUI as clients of that API · a WFP driver that classifies by image path and follows child processes | route by executable path, children included; name lookups made by the system's DNS service cannot be attributed to an app |
| sing-box / Clash (mihomo) | inbounds (SOCKS, TUN) → a router of rules (process name/path, domain, CIDR) → outbounds; the platform behind an interface; GUIs drive the core through an API | inbound → router → outbound as the routing model; process rules; platform work behind an interface |
| Netch | a process mode on a packet driver; a TUN mode on Wintun | games are routed per process at the packet level — they ignore proxy settings |
| Shenora (own) | one package per engine boundary; a capability is a folder, never a package id | a package boundary only where a dependency or a platform differs |

Sources: [WireGuard for Windows](https://git.zx2c4.com/wireguard-windows/about/), [Mullvad win-split-tunnel](https://github.com/mullvad/win-split-tunnel),
[sing-box route rules](https://sing-box.sagernet.org/configuration/route/rule/), [Netch](https://github.com/NetchX/Netch),
[WinDivert](https://github.com/basil00/WinDivert) and its [documentation](https://raw.githubusercontent.com/basil00/WinDivert/master/doc/windivert.html).

## Packages and applications

Two packages — split only where a dependency or platform differs — and four applications on top.

```
src/
  Sallyport/                 the package everything uses        net10.0, BCL only (rule 4)
    Profile/                   .ovpn parsing                      public
    Protocol/                  control + data channel, transports internal — the wire is not an API
    Tunnel.cs                  IP packets in and out              public
    Net/                       userspace TCP + DNS over a tunnel  public
    Proxy/                     SOCKS5 inbound                     public
    Routing/                   rules → outbound; NAT; port        public
                               allocation; the packet switch that
                               shares one tunnel between Net and NAT
    Runtime/                   sessions (a tunnel per profile,    public
                               reconnect, status events), the
                               profile store, the management API's
                               contracts
  Sallyport.Windows/         packet routing on Windows            net10.0-windows; WinDivert (native)
                               WinDivert capture + inbound
                               injection, owner-PID lookup (IP
                               Helper), process tree, service host
apps/
  Sallyport.Service/         Windows service, LocalSystem: Runtime + packet router; a named-pipe management API
  Sallyport.Client/          Shenora 0.21.1 from nuget.org (Shenora.Windows, WebView2), unelevated: profiles, routed apps, live status
  Sallyport.Host/            Kobo's headless provider, unprivileged, SOCKS only — unchanged on the outside
tools/
  Sallyport.Probe/           live measurements against a real server
tests/
```

`Sallyport.Windows` is a package of its own for the reason Shenora gives for an engine: an application that only
wants a SOCKS route (Kobo) must not carry a kernel driver or need administrator rights.

## Routing: one router, two ways in

**Packet mode — any application, games included (needs the service).** A WinDivert handle at the network layer
diverts outbound packets that are not loopback and not to a VPN server. WinDivert's network layer carries no process
id, so each new 5-tuple is attributed by the owner tables (`GetExtendedTcpTable`/`GetExtendedUdpTable` with the
owning PID), helped by its flow and socket layers, which report the PID but can only observe. The PID's image path —
or an ancestor's — is matched against the routed applications.

- **Routed:** the IP packet is NAT'd into that profile's tunnel — source rewritten to the tunnel's address, the port
  remapped through the tunnel's port allocator, TCP's MSS clamped to the tunnel's, checksums recomputed — and sent on
  the data channel as it is. TCP, UDP and ICMP alike, with **no userspace TCP in the path**: the far server's TCP
  talks to the game's. Replies leave the tunnel, are NAT'd back and injected inbound on the original interface.
- **Not routed:** reinjected unchanged. To keep the rest of the machine out of user space, the filter diverts only TCP
  SYNs, outbound UDP and ICMP, and the TCP of flows already routed; an established direct TCP flow never leaves the kernel.

This fits Sallyport better than a proxifier does: an OpenVPN tunnel already carries IP packets, so a game's UDP needs no
SOCKS UDP relay and its TCP needs no second TCP stack.

**Proxy mode — applications that honour a proxy (no administrator rights).** The SOCKS5 inbound and the userspace TCP of
today. The client launches the application with its proxy settings: Chromium-family and Electron flags with their own
profile directory, a Firefox profile, proxy environment variables for CLI and .NET tools. Kobo's route stays here.

**Rules:** an executable path (children included) now; a host or domain (Kobo's per-site list) and CIDR later.

## What has to be measured before it is built on

| Risk | How it is measured |
|---|---|
| WinDivert's driver loads on this machine (Windows 11, vulnerable-driver blocklist on, memory integrity off) | open a handle; Event Viewer 3023/3033 if refused |
| injected inbound replies are accepted by the stack and the firewall | one process's TCP and UDP through the NAT: its address reads the server's while another process reads AU |
| the first packet of a flow is attributed before it leaves | count packets sent direct that belonged to a routed process |
| a real game's traffic is routed whole — its UDP, its pings, its several flows | a game session: its servers see the tunnel's address, the rest of the machine AU |
| cost of diverting outbound UDP | a game session's CPU and added latency |

WinDivert is LGPLv3 or GPLv2 at the user's choice: an MIT application may load its DLL dynamically and ship the signed
DLL and driver unmodified with the licence text (D10). It sits behind one narrow interface in `Sallyport.Windows` — the
only part of Sallyport that is not our own MIT code, because a kernel driver is the one thing .NET cannot be; a driver
of our own could replace it without touching anything above.

## Order of work

1. **Restructure, no behaviour change.** The layout above; wire types internal; Kobo's Host republished and its route test passing.
2. **Runtime.** Sessions per profile with status events, the profile store, the management contracts.
3. **Spike: packet mode for one process,** measured as above. If WinDivert cannot load or a game refuses it, stop and decide again.
4. **Service + `Sallyport.Windows`.**
5. **Client** on Shenora: profiles (import a copy), routed applications, live status.
6. **Proxy-mode launch recipes** in the client.

## Decided, and still open

- Decided: the profile store moves with the service — imported copies held under `%ProgramData%\Sallyport\profiles`,
  SYSTEM and Administrators only, encrypted at rest with DPAPI (D11).
- Not a risk: anti-cheat. Nothing touches a game's process — the driver sits in the network stack, as a game VPN's does
  (WTFast and the like) — so there is nothing to bypass. A game that refused to start beside a listed driver would be a
  compatibility finding; none is known for WinDivert.
- Open: which game to measure — for its traffic, not its anti-cheat.
