# Decisions — Sallyport

## D1 — Written from the protocol, not from OpenVPN's code; MIT (2026-10-12)

The owner wanted OpenVPN brought into their own software "but avoid the agpl license". OpenVPN 3 Core, the embeddable
library, is AGPLv3; OpenVPN 2 is GPLv2. A protocol is not copyrightable; an implementation is. So Sallyport implements
the OpenVPN wire protocol from its public description and the server's observable behaviour, and uses no OpenVPN code —
not linked, not ported, not translated structure. Licensed MIT, like the owner's other packages (Lyntai, Shenora). The
name avoids the OpenVPN trademark; the README says "OpenVPN-compatible" and states the non-affiliation.

## D2 — The tunnel ends in a SOCKS5 proxy, not a network adapter (2026-10-12)

The point is a VPN for ONE application. An adapter (TAP/wintun) puts the tunnel in the machine's routing table, needs
administrator rights, and takes every application's traffic unless routes are fought over. Ending the tunnel inside
the process — a userspace TCP/IP stack behind a loopback SOCKS5 server — routes exactly what is pointed at it, with no
privileges. Kobo's browser route already consumes a SOCKS5 proxy, so the swap is invisible to it.

## D3 — Data-channel keys by the legacy PRF expansion, not the TLS exporter (2026-10-12)

Newer servers prefer deriving data keys with the TLS keying-material exporter (RFC 5705, "tls-ekm"). .NET's `SslStream`
does not expose an exporter. A client that does not advertise the exporter capability in its peer info is given the
older key-method-2 expansion (the TLS 1.0 PRF over both sides' random material), which servers still support. Revisit
if .NET gains an exporter, or if a server refuses clients without it.

Measured 2026-10-12 against the first real server: it accepted the client without the capability, and its push reply's
`protocol-flags` held only `cc-exit` — no `tls-ekm` — so it expects the PRF expansion. One server, so this says that one
server does not insist, not that none will.

## D4 — The peer info claims no OpenVPN version (2026-10-12)

Peer info carries `IV_PLAT`, `IV_PROTO`, `IV_NCP`, `IV_CIPHERS` and `IV_GUI_VER=Sallyport_<version>` — and no `IV_VER`.
Sallyport is not OpenVPN and does not claim to be a version of it; what a server needs to know is which capabilities the
client has, and those have their own fields. Measured 2026-10-12: the first real server pushed its full configuration
without it. If a server is ever found that gates on `IV_VER`, record which one before adding it.

## D5 — The client sends data with the derived block's FIRST half (2026-10-12)

The data-channel key block has two halves, one per direction, and the client sends with half 0. That is the opposite
of the tls-crypt static key, where the client sends with half 1 — so the two must not share a constant. Measured
2026-10-12: with half 0 both DNS lookups were answered and no packet was rejected. `TunnelOptions.DataSendHalf` keeps
the other half reachable, and `TunnelCounters.OppositeDirection` names the mix-up when it happens (a packet that opens
only under this side's own sending key), so a server that disagrees is diagnosed rather than merely silent.

The same run confirmed D3's derivation end to end: a wrong master secret or key expansion would have authenticated
nothing in either direction.

## D6 — The userspace TCP is sized by measurement: 4 MB window, 4 MB socket buffer, SACK (2026-10-12)

**Axis measured: one connection's 10 MB download, from Australia through the Tokyo server, against WSL's OpenVPN
(Linux TCP) on the same server, runs interleaved.** Uploads and hours-long sessions were not measured; many concurrent
connections are D7.

| change | Sallyport | WSL in the same window | what the counters showed |
|---|---|---|---|
| first cut: 512 KB window, default socket buffer | 0.51–1.06 MB/s | 1.09–2.94 | 403–1,807 segments out of order per run |
| UDP receive buffer 4 MB | 1.72–1.88 | 2.23–3.40 | out of order fell to 0–2: the drops were OURS, a burst overflowing the socket |
| receive window 4 MB (scale 6) | 3.87, 1.86, 3.92 | 3.45–3.52 | the slow run lost 11 packets on the path |
| receiver SACK | 3.57, 2.92, 3.23 | 0.67, 2.97, 3.37 | 53 and 337 packets lost in the fast runs, recovered |

A connection's ceiling is window ÷ round trip, so 512 KB at 200 ms could never pass ~2.5 MB/s. The receive buffer grows
as bytes queue, so the 4 MB is a ceiling, not a cost per connection. Without SACK a server repairs one hole per round
trip; with it, many. The counters that made this visible — segments out of order, ids missing from the server's data
packets — stay, because a download's loss is otherwise invisible from the receiving side.

## D7 — Many connections at once: the reader's cost per segment was the limit; the GC stays the default (2026-10-19)

**Axis measured: 32 and 64 connections opened at once through the proxy against the Tokyo server — each asking one of
six Cloudflare hosts where it came from, then each downloading 1 MB — from a client in the same process
(`Sallyport.Probe --concurrent N`); and a real browser page.** Not compared with Linux OpenVPN.

At 64 the network's single reader fell behind the tunnel: its queue reached 4,096 of 4,096 and 61, 1,345 and 10,510
packets were thrown away by this process itself — invisible until `TunnelCounters.Overflowed` counted them, because it
looks exactly like loss on the path. Every early segment is answered at once with SACK blocks, and those were rebuilt
from every segment held past the hole: offline, a hole of 3,000 segments cost 105 µs a segment, against 1 µs in order.
Each drop made a new hole, so the reader slowed as it fell behind.

| change | 64 × 1 MB at once, together | what the counters showed |
|---|---|---|
| as it was (Debug build, 3 runs) | 9.8–15.2 MB/s | 61, 1,345 and 10,510 overflowed; the queue full |
| SACK ranges kept as segments arrive (9 runs) | 11.3–16.6 | a hole 4.6–9.7 µs a segment offline; 0 overflowed in 8 runs, 2,568 in one |
| a waiting reader woken once per burst, not per segment (12 runs, Release) | 11.8–18.2 | contended locks 190–325 → 24–51 a round; 0 overflowed; the queue at most 3,378 once, otherwise 289–881 |

What still stalls the reader is mostly the collector: of its single dispatches that took 5 ms or more in three timed
runs, 24 of 27 held a gen 0/1 collection of 5–11 ms (two earlier stalls of 62 and 101 ms were not timed that way).
About 300 MB is allocated per 64 MB downloaded, the client's share included.

**Server GC was measured and not taken.** In the probe, where the client shares the process, it cut pauses from
115–146 ms a round to 50–55 and the queue's peak to 88–119. In `Sallyport.Host`, with the client in another process as
Kobo's browser is (64 parallel curl downloads, two runs each), it changed nothing measurable — 8.9–10.1 MB/s against
9.2–10.2 — and cost 30 MB more at peak (145–148 MB private against 110–117). A host that runs Sallyport in its own
process chooses its own GC.

At 32 at once (6 runs, before the fixes and after): 8.2–13.4 MB/s together, 0 overflowed. A real page through the
proxy, headless Edge with a cold cache (Yahoo! JAPAN, then nikkei.com, after a trace page that said JP): up to 49
connections at once, 98–114 opened, 0 failed to connect, 0 overflowed.

## D8 — A half-closed relay whose other side is silent for a minute is let go (2026-10-19)

The proxy sees only end-of-stream, never whether a client shut down its sending side or closed outright, so it passes
it on as a half-close and keeps the reply open (RFC 1928 says nothing either way). A browser closes outright, and some
servers never close their side after our FIN. Measured: a cold Edge session left 6 of its 114 connections held 90 s
after it exited, each with our FIN acknowledged and none from the server. A Host up all day would only gather them.
An operating system bounds the same state for a closed socket — Linux gives it a minute (`tcp_fin_timeout`) — so once
one direction of a relay has ended, the relay ends when the other has been silent for 60 s, every byte on it postponing
that. Measured with it, the same cold session served for 150 s: three relays were still held when the last page
ended, none by 80 s, and no connection was left in the network by 110 s, after the linger.

## D9 — Two packages and the applications on them (2026-10-19)

The owner asked for Sallyport to be "more like a library" with a client of its own, on a proper package layer. Read
for shape against WireGuard for Windows, Mullvad, sing-box/Clash and Netch, and against Shenora's own rule — a package
boundary only where a dependency or platform differs (`docs/design/architecture.md`). So: `src/Sallyport`, BCL only
(rule 4), with the wire (`Sallyport.Protocol`: control and data channels, transports, certificates) internal and
`Profile`, `Tunnel`, `Net`, `Proxy`, `Runtime` public; `src/Sallyport.Windows` later, the one package that may carry a
driver; `apps/` for what runs (`Sallyport.Host`, and to come `Sallyport.Service` and `Sallyport.Client`); `tools/` for
the probe. Rule 4 binds the packages; an application may take a package by a decision here. **`Sallyport.Client` takes
Shenora** (the owner's framework, from nuget.org at a pinned release, never its source — Kobo's review desk broke on a
namespace move the day it referenced the source), on the WebView2 shell: a small client should not carry CEF's 400 MB.

## D10 — Games are routed at the packet level, through WinDivert (2026-10-19; measured, and reopened)

**Measured the same day, before anything was built on it: Windows Defender will not let WinDivert in.** The official
2.2.2 release, downloaded from its GitHub page, was blocked on arrival as `Misleading:Win32/Malgent!AMTB` (severity 4;
the file could not even be read). Nothing was loaded, and the download was deleted. Every user's Defender would do the
same to an install carrying it, so WinDivert is not shippable, and by this entry's own condition the choice is reopened
(D12). Measured beside it: WireGuard's Wintun 0.14.1 passed Defender, validly signed by WireGuard LLC; its prebuilt
licence allows redistribution alongside software that uses it only through `wintun.h`'s API.

Games ignore proxy settings, so routing one needs the operating system's packets. The options weighed: our own WFP
callout driver (Mullvad's way) needs Microsoft's attestation signing behind an EV certificate; Wintun's TUN mode
(sing-box's way) takes all the machine's traffic into user space and needs a full TCP/UDP stack to send the rest back
out; injecting a hook into the game is what anti-cheat exists to refuse. WinDivert is a signed driver that diverts only
what a filter names, and an OpenVPN tunnel already carries IP packets: a routed process's packets are NAT'd into the
tunnel and its replies injected back, TCP, UDP and ICMP alike, with no second TCP stack. Costs, accepted by the owner:
administrator rights (so the runtime moves into a service) and a native dependency (LGPLv3 or GPLv2 at our choice —
loaded dynamically, shipped unmodified with its licence).

Nothing here works against a game's anti-cheat: as with game VPNs such as WTFast, the driver sits in the network stack,
outside the game's process, and the game is never touched. The owner's point, and the right frame. What remains is
compatibility, not bypass — a few anti-cheats refuse to start beside particular drivers they have listed (WinRing0 is
the known case; none was found for WinDivert) — and a real game is measured for its traffic, not its anti-cheat. The
spike (TASKS) measures the driver before anything is built on it; if it will not load here, this is decided again.

**Ours, and MIT, down to the one piece .NET cannot be.** The owner's rule: if a borrowed part ever stands in the way, we
write our own, and the project stays MIT. Everything above the kernel is ours — the NAT, the routing, attributing a flow
to a process, the tunnel, the service, the client. The exception is the driver itself: Windows lets only a kernel-mode
driver see or redirect another program's packets (its user-mode filtering API can allow or block, not capture), a
driver is C against the Windows Driver Kit, and Windows 11 loads one only with Microsoft's attestation signature behind
an EV certificate. So WinDivert's signed driver is used unmodified, a separate file under its own licence, behind one
narrow interface in `Sallyport.Windows`. Replacing it with a driver of our own (C, WDK, signing) would change nothing
above that interface.

## D11 — Imported profiles are held by the service, encrypted (2026-10-19; superseded by D14)

A profile holds a private key and a static key. The client imports a copy (the owner chose a copy over a path, so it
keeps working when the original moves), and the service owns it: `%ProgramData%\Sallyport\profiles`, readable by SYSTEM
and Administrators only, sealed at rest with DPAPI under the service's own account (`CryptProtectData` through
P/Invoke, no package) — not DPAPI's machine scope, which any process on the machine could open. The unelevated client
sees a profile's name, remotes' transports and status — never its key material. The store itself is platform-neutral
(`ProfileStore` behind `IProfileProtector`); the DPAPI protector lives in `Sallyport.Windows`.

## D12 — Games are routed by Windows' own per-app VPN: Sallyport as a VPN plug-in (2026-10-19; to be measured)

WinDivert is out (D10: Defender blocks it on download). Chosen by the owner over a driver of our own (C, and Microsoft's
attestation signature behind an EV certificate) and over Wintun (it routes by destination, not by process): Windows'
VPN plug-in platform (`Windows.Networking.Vpn`). Windows creates the VPN interface and filters by application itself —
a desktop application is named by its file path (VPNv2 `TrafficFilterList` `App/Id`), and a plug-in may start its
channel with app filters of its own (`VpnChannel.StartWithTrafficFilter`). Only the chosen programs' packets enter it,
TCP and UDP alike, as IP packets — which is what an OpenVPN tunnel carries. No third-party driver, nothing for an
antivirus to flag, and every line Sallyport's own, MIT; Microsoft's C#/WinRT, needed to author the plug-in's
background component in .NET, is MIT as well. Microsoft's own C# plug-in sample (MIT) shows the shape: a `vpnClient`
background task whose class is an in-process WinRT server.

What it costs: an MSIX package declaring the restricted `networkingVpnProvider` capability — "Store approval isn't
required to sideload an app that declares restricted capabilities" (Microsoft's capability reference) — so a package
signed by a certificate the machine trusts to install elsewhere, and Developer Mode or a trusted test certificate to
develop here. And a change of shape: the plug-in is driven by the platform's callbacks (connect, encapsulate,
decapsulate) rather than running loops of its own, and it runs where the platform puts it, which reopens where D11's
store lives. The spike proves, in order: a .NET 10 component is activated as the `vpnClient` task; with an app filter,
one program's packets reach it and another's do not; then the real tunnel — that program reads the server's address
while another reads AU. If the first two cannot be made to hold, this is decided again.

**Measured 2026-10-19** (the spike's code is in git at `e8cb2e4`, removed after; a sideloaded package signed by a
test certificate the machine trusted, both since removed from the machine):
- The platform runs a .NET 10 plug-in. A `vpnClient` task must name both a custom host (`Executable`) and a class
  (`EntryPoint`); the host is a framework-dependent .NET executable running `CoreApplication`, the class a C#/WinRT
  component served in-process by `WinRT.Host.dll`. Connect, encapsulate and disconnect reached it. On the way: C#/WinRT
  ships its x86 host for AnyCPU (`ERROR_BAD_EXE_FORMAT` in a 64-bit host — set `Platform` x64); with no Windows SDK
  installed, its metadata comes from `Microsoft.Windows.SDK.Contracts`; `ServerHostNameList` threw `E_INVALIDARG` for
  a server URI of `https://1.1.1.1`, so the plug-in carries its server in its own configuration.
- **App filters work only with an explicit protocol.** A filter's `Protocol` defaults to `None` (0) and then matches
  nothing — curl was refused at once under its plain, lower-case, `%windir%` and device-path forms, at the plug-in and at
  the profile alike. With `Protocol` TCP and UDP the plain path matched: under a split route only the listed program's
  packets reached the plug-in, and an unlisted program's traffic to the routed prefixes was refused, not sent direct.
- **Force policy confines the program, not the routes:** a listed program forced through the VPN could not reach a
  destination outside the VPN's routes ("unreachable network").
- **A default route is not confined by app filters:** with 0.0.0.0/0 and the listed program filtered, other programs'
  connections (four outside hosts within two seconds) went into the VPN as it started, and the guard (D13) tripped.
- The plug-in cannot end its own connection: `TerminateConnection` never did in four runs, and a timer in its process
  fired late or not at all — the process appears to be suspended between the platform's events.

**So, open:** routing all of a game's traffic needs its destinations routed into the VPN, and a default route takes the
whole machine. The shape that the measurements allow is split routes for the game's own server prefixes — known per
game, or learned from its connections — with the app filter confining them to it. Undecided.

## D13 — Nothing that can black-hole the machine is connected without a guard outside it (2026-10-19)

The spike's first run routed 0.0.0.0/0 with an app filter that matched nothing (D12's protocol default) into a plug-in
that drops every packet: the whole machine lost its network until the platform dropped the connection. The owner's rule
since: a proper backup first — when the network stops, the VPN is disabled.

- The plug-in refuses a default route unless its profile explicitly allows one.
- A guard in a process outside the VPN — never a routed program itself — refuses to connect when the direct path already
  fails, probes it every 250 ms while connected (500 ms each), and on three failures in a row or four of the last six
  disconnects, waits for the platform to report it down, removes the profile, and waits up to 10 s for the direct path.
- The guard's process must outlive the connection. A test script that died took its guard with it and left the profile
  connected (measured), so the shipped guard lives in the long-running client or service, and `panic` — disconnect and
  remove every profile — is always one command away.

Measured: with 1.1.1.1 black-holed it tripped 4.4 s after connecting; with a default route, 0.6 s after, torn down by
3.8 s; the direct path was back within 0.1 s each time and no profile was left. The plug-in's own deadline is not a
failsafe (D12): the guard outside is the one that is.

## D14 — The client runs the runtime itself, unelevated; imported profiles are sealed for the user (2026-10-19; supersedes D11)

D11 gave the store to a LocalSystem service because packet routing (D10) needed one. WinDivert is gone (D10) and game
routing is open (D12), and nothing the client does today needs administrator rights: proxy mode is a loopback SOCKS
port. So `apps/Sallyport.Client` runs `TunnelService` in its own process — a session per connected profile, each on
its own loopback port (1088 upward by default; 1080 is Kobo's Host) — and there is no service.

Imported copies live in `%LOCALAPPDATA%\Sallyport\profiles`, sealed with DPAPI for the current user (`ProtectedData`,
which ships in the Windows Desktop runtime the client already runs on: no package, no P/Invoke), with a purpose
string as entropy so that another DPAPI consumer of the same user does not open them by mistake — it is a label, not a
secret. What that scope buys: the copy is unreadable to other users, to another machine, and in a backup or a synced
folder; any program running as the user can still open it, as it could the `.ovpn` the user imported from. The page
(WebView2) sees names, transports and status, never key material; a profile is opened only in the moment it connects.

D13 does not bind proxy mode: a SOCKS port carries only what an application sends it, so a dead tunnel stops those
applications and nothing else. If game routing brings a privileged part, the store moves with it and D11's reasoning
applies again.

## D15 — The client's window shows what its host decides (2026-10-19)

The owner: *"use event messaging so ui just for display"*. The page (React in WebView2) is sent `CLIENT.STATE` — the
whole view model, every state-dependent word included — on Shenora's ready handshake and on every change, and
`CLIENT.NOTICE` for each outcome; it posts intents that answer nothing (`post`, which Shenora's own D23 makes the
default; never `invoke`). An edit, a confirmation, an open dialog and a launch under way are host state; the page keeps
only what is visual — the page shown, a row folded, text being typed. So the tray and the window render one view and
cannot disagree, and what the client decides is tested in C# (`tests/Sallyport.Client.Tests`) without a browser. The
contract is `apps/Sallyport.Client/web/src/contract.ts`; the design that needed it, Codex's, is
`docs/design/client-ui.md`.

The window is frameless — Shenora's `FramelessChrome` with `NativeCaptionButtons`: the page draws the title bar and
reports its caption buttons, the form paints them in that hole, so Snap Layouts stay — and follows the OS's light or
dark, which the client reads from the registry because Shenora.Windows does not say.

No DevTools port on the packaged page. Shenora drops remote-debugging switches outside development since 2026-09-29
(its `6aed4927`): a port there hands any local process the page's IPC, which for this client is connect and launch.
Tests drive the development build instead — the same host and page, the page served by Vite.

