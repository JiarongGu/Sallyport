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

## D12 — Games are routed by Windows' own per-app VPN: Sallyport as a VPN plug-in (2026-10-19; measured, see D16)

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

## D16 — A game's packets reach the tunnel through the plug-in as a loopback relay (2026-10-19; measured)

D12's last open step, measured, and its shape decided. The plug-in carries no tunnel: it relays each IP packet as one
datagram over loopback to the bridge (`apps/Sallyport.Vpn`), a full-trust process in the same package, run through its
execution alias. The bridge runs the OpenVPN tunnel (the library, unchanged), relays the replies back, manages the VPN
profile and guards the direct path (D13). So no key or profile enters the plug-in's AppContainer, and the tunnel lives
in a process the platform does not suspend between events. Chosen over the tunnel inside the plug-in, which would have
needed the profile in the AppContainer and a tunnel that keeps its timers in a process that is frozen between packets.

What it took, each measured:
- **A loopback exemption for the package** (`CheckNetIsolation LoopbackExempt -a -n=<package family>`, administrator,
  once). Without it the plug-in started its channel and sent, and the bridge received nothing (4 packets sent, 0
  received, not even its hello). With it, all of it arrived.
- **Decapsulate copies each datagram into a buffer from `GetVpnReceivePacketBuffer`.** Handing back the transport's own
  receive buffer ended the host process each time: the platform started the plug-in four times in 60 s, the bridge
  relayed 12 replies, and none reached the program.
- **The relay answers only the endpoint that first sends "SPRT" and the run's token**, which the bridge puts in the
  profile's configuration, so no other socket on the machine can join it.
- **The guard has to survive the platform.** A profile's `ConnectionStatus` threw `E_HANDLE` while it was being torn
  down and killed the first guard, after it had disconnected but before it removed the profile. Status reads are now
  three-outcome (rule 6), the verdict is printed before teardown, and the route is torn down in a `finally`.

Measured 2026-10-19 with Kobo's JP profile, the package test-signed, and only /32 routes:
- **TCP.** curl copied into a folder of its own and listed, with ipinfo.io's address routed: the listed curl read JP
  Tokyo, five requests of five, TCP connect 0.22–0.30 s. The same program to an unrouted host read AU. An unlisted
  program to an unrouted host read AU. An unlisted program to the routed address was refused, as D12 found.
- **UDP.** nslookup copied and listed, with ns1.google.com routed: the source Google's server saw for its TXT query was
  the tunnel's exit address, the same one ipinfo saw through the listed curl.
- **Both legs balanced:** 61 packets out and 61 into the tunnel, 59 back and 59 delivered, in one run. One plug-in process
  per run. Every run was torn down by the guard with the direct path intact.

Open: which prefixes a game needs (known, or learned from its own connections); the bridge folded into the client; a
release certificate, and the exemption made at install.

## D17 — Windows' VPN platform cannot route by process alone; the products that do bring a kernel driver (2026-10-19; measured)

The owner expected routing by process — a game's traffic, every destination — rather than by address range. So the
spike's default-route finding (D12) was measured again, now with the filter's protocols set and the relay working
(D16): one listed program (a curl copy), routes 0.0.0.0/0, and exclusion routes for the VPN server and the guard's
probe. Both runs were under the guard, `--default-route allow`, and 30 s at most.
- **Split policy:** the guard's probe — from the bridge, an unlisted program, to an *excluded* address — went into the
  VPN anyway; the plug-in received it and dropped it as excluded. The guard tripped 2.1 s after connecting. The
  bridge's own socket to the excluded VPN server failed with WSAEINVAL. Torn down by 6.8 s, the direct path back within
  3.3 s.
- **Forced policy:** the bridge's tunnel failed the moment the VPN came up (WSAEINVAL). 18 packets went into the
  plug-in and 63 were dropped. Torn down by 6.2 s, the direct path back within 5.3 s.

So D12 holds: on this platform an app filter selects the program but cannot narrow a default route, and exclusion
routes did not keep traffic off it. A game is routed by its destinations here or not at all.

The products that route by process do it below the platform, each with a signed kernel driver of its own:
- Mudfish: a WFP driver (MUDWFP), "each process's traffic is handled using the WFP function"; its other mode changes the
  routing table, as D16 does.
- ExitLag: `ndextlag.sys`, "NDIS 6 LWF packet redirector driver".
- PingAim: a kernel-level WFP driver matching the game by process name.
- Proxifier v4: a WFP callout driver that intercepts connections. Its maker, Initex, publishes WTFast's Windows client;
  WTFast documents no mechanism.

Open, the owner's to choose: (a) stay driverless — the VPN platform with each game's ranges, learned from its own
connections (TCP's are in the owner tables; UDP's need ETW, which needs administrator) or supplied per game, as the
products also ship; (b) a driver of our own — C, not .NET, a WFP callout redirecting the game's flows to the bridge, MIT
like the rest. It needs an EV code-signing certificate and Microsoft's attestation signing to load on an ordinary
machine; development needs test signing, which anti-cheats refuse, so a separate machine.

**Amended 2026-10-19: an exclusion route carries its address INTO the VPN.** After D17 the bridge kept adding exclusion
routes for the guard's probe and the VPN server to every run. A split run of nine /32s for Zenless Zone Zero then had
the guard trip 2.1 s after connecting — the probe (1.1.1.1, excluded) went into the VPN and the plug-in dropped it —
torn down by 7.4 s, the direct path back 4.1 s after. The same nine /32s without exclusion routes held the full 300 s
with no trip. So exclusion routes are gone from the bridge and the plug-in; the probe and the server stay direct only
because no prefix may cover them, which the bridge refuses.

## D18 — Per-process routing starts on Windows Packet Filter; our code stays MIT (2026-10-19)

The owner, asked to choose how a game's every packet is routed (D17): *"so how hard is to develop our own with
everything working properly and it need to be easy to install for end user"*, then, after the answer (our own driver
is weeks to months of kernel C, and installable by others only once Microsoft attestation-signs it, which needs an EV
certificate issued to a registered organisation): *"I think we can use a free one for now because this is not for
commercal rn and just the code need to stay MIT"*.

So Stage 1 uses NT Kernel's Windows Packet Filter driver (`ndisrd.sys`), free for personal, educational and non-profit
use; its user-mode library (`ndisapi`) is MIT. Sallyport's own code — matching packets to processes, the NAT into the
tunnel, putting replies back — is MIT as everything else (D1), and the driver sits behind the seam the game-routing
design names (§5), so our own WFP driver can replace it (Stage 2) without changing what is above. The driver is the
owner's to install from NT Kernel; it is not copied into this repository or shipped with Sallyport. Sallyport is not
offered commercially while this holds; distributing it with the driver to others would need NT Kernel's licence or
Stage 2.

First measured before anything is built on it: that Defender accepts the driver (it blocked WinDivert, D10), that
Zenless Zone Zero runs beside it, and whether opening it needs administrator.

**Amended 2026-10-20: WTFast, read from its own installer.** WTFast 5.5.42's installer (Inno Setup, signed by AAA
Internet Publishing Inc) was downloaded and read — not installed, nothing of it run; innoextract listed and unpacked
its `Driver` folder. It ships `WtfEngineDrv.sys` ("WTFastEngine Driver Win8 x64", 3.0.0.334, a KMDF 1.9 service
installed by its `DriverTool.exe`), signed by **Initeks, OOO** of Saint Petersburg — Initex, Proxifier's maker. Its
import table: twenty WFP functions (`fwpkclnt.sys`) — callouts and filters (`FwpsCalloutRegister1`, `FwpmFilterAdd0`),
**connect redirection** (`FwpsRedirectHandleCreate0`, `FwpsQueryConnectionRedirectState0`,
`FwpsAcquireWritableLayerDataPointer0`, `FwpsApplyModifiedLayerData0`) and **packet injection**
(`FwpsInjectTransportSendAsync0`, `FwpsInjectNetworkReceiveAsync0`, `FwpsAllocateCloneNetBufferList0`,
`FwpsQueryPacketInjectionState0`) — and only three NDIS buffer helpers, so no NDIS filter. Its client is .NET over
gRPC. So WTFast selects a game's traffic in a WFP driver of its own: connections redirected to its local engine at
connect time, packets cloned and injected for the rest — the shape of Stage 2 here (D18).

**Amended 2026-10-20: the app manages the driver, from NT Kernel's own release.** The owner: *"you can bundle that in
since if we need that we need to install it so the app need to mamaged the install and uninstall of it"*. NT Kernel's
free tier excludes only commercial redistribution and says nothing of a free application carrying the driver, so
Sallyport carries none of it: `Sallyport.Windows` fetches the official installer from NT Kernel's own repository
(`wiresock/ndisapi`, release v3.6.2) on the person's machine when they ask, checks it against a pinned SHA-256, and
installs or removes it through Windows Installer with one administrator prompt; the installer is kept for removal.
Pinned after reading it, 2026-10-20: the MSI and `ndisrd.sys` signed by "The Anti-Cloud Corporation" (EV), the
driver's catalog by Microsoft Windows Hardware Compatibility Publisher, `ndisrd.sys` 3.6.2.1 "NT KERNEL | NDIS Filter
driver", an NDIS lightweight filter (`nt_ndisrd`, service `ndisrd`).

Measured on this machine the same night, through `tools/Sallyport.Driver install`: done in 25 s (Windows Installer exit
0); the service running; the installed `ndisrd.sys` signed by Microsoft Windows Hardware Compatibility Publisher; the
filter bound to every adapter; **Defender: no detection and no malware or behaviour event** — unlike WinDivert (D10);
the direct path 10 of 10 after. The driver answered its version request (`0x06023000`). **Opening `\\.\NDISRD` needs
administrator**: a basic-user token was refused, access denied — so the router runs elevated, not in the client.

Measured next (2026-10-20): Zenless Zone Zero, launched with the driver installed (its anti-cheat starting after it),
started and played normally. Removal through the app: done in 30 s (Windows Installer exit 0) — the service gone, no
adapter binding left; `ndisrd.sys` itself stayed in `System32\drivers`, inert without its service. Reinstalled from the
kept installer in 36 s. A probe of the direct path every 250 ms during both: removal cut the network for about 6 s
(adapters released by the filter), installation for about 2 s — at those moments only, so the app says so before it
manages the driver.


## D19 — Everything Sallyport keeps is in the folder it was installed to (2026-10-20; measured)

The owner: *"do not install on my c drive"*, *"so the intallation should be able to select the install location"*, then
yes to moving the rest. So setup asks where (`%ProgramFiles%\Sallyport` only as the default), and what Sallyport keeps
goes in that folder, not where Windows would have put it:

- **The machine's part** — setup's record, the driver's installer, the service's logs, setup's own log once an install
  ends — in `data` inside it, not `%ProgramData%` or `%TEMP%`.
- **Each user's part** — sealed profiles, settings, apps, the window's cache — in `data\users\<SID>`, not
  `%LOCALAPPDATA%` (supersedes D14's location; its sealing is unchanged, DPAPI for the user with no path in it). Setup
  makes `data\users` with rights that let users create a folder in it and nothing more — not list it, not open
  another's. The client makes its own, owned by the person and open to them, the system and administrators only (an
  elevated start would otherwise leave it owned by Administrators and shut to the person), and uses one it finds only
  if its owner is the person, the system or administrators: any user may make a folder there, and one named for
  someone else is not theirs. The first installed start copies an earlier `%LOCALAPPDATA%\Sallyport` over (the
  window's cache excepted, which WebView2 rebuilds), marks the copy, and removes the old folder; never into a folder
  that already holds something. A development build, which has no `data\users`, keeps `%LOCALAPPDATA%\Sallyport`.
- **The driver's own files** — NT Kernel's installer puts them in its public `INSTALLLOCATION`, read from the pinned
  MSI's Directory table — beside Sallyport's folder (`<parent>\Windows Packet Filter`, as Program Files would have it;
  not inside, which an update replaces whole). `ndisrd.sys` itself goes where Windows keeps every driver, whatever this
  says.

Off the system drive a new folder inherits the drive's rights — here `D:\Program Files` lets Authenticated Users modify
— and the service believes only clients run from its own folder, so setup gives its folders their own rights before
anything goes in: owned by Administrators; SYSTEM and Administrators full, Users read and run; nothing inherited. It
takes only a folder that is empty, Sallyport's, or what removing Sallyport kept of it. Removing keeps `data\users`
unless the person says otherwise, and installing in that folder again finds it. Windows' own entries — the service,
the Start menu, the installed-apps list, `.ovpn` under Open With — stay where Windows keeps them.

Measured 2026-10-20 on the owner's machine. Installed and then reinstalled from setup's window to `D:\Program
Files\Sallyport`. The first installed start of the client made `data\users\<SID>`, owned by the owner, SYSTEM,
Administrators and the owner full, nothing inherited; it copied `apps`, `config`, `drivers`, `games` and `profiles`
across, wrote its mark, and `%LOCALAPPDATA%\Sallyport` was gone. `data\users`: SYSTEM and Administrators full; Users
create-folder, traverse, read attributes and permissions, this folder only. Setup's log moved to `data\logs\setup.log`,
none left in `%TEMP%`. The driver: removed and reinstalled through `Sallyport.Driver install --location "D:\Program
Files\Windows Packet Filter"` — both Windows Installer exit 0; its certificates and driver package in that folder, no
`C:\Program Files\Windows Packet Filter`; the service `Running`, the device answering (`0x06023000`), 1.1.1.1:443
reachable after each. That folder had inherited `D:\Program Files`' rights and was given Sallyport's; setup now does
that before it installs the driver. Nothing of Sallyport's left on C: but Windows' own entries and `ndisrd.sys`.

## D20 — A profile's proxy listens on whatever port is free when it connects (2026-10-20; measured; supersedes D14's ports)

D14 gave each profile a port of its own, 1088 upward, kept in the client's settings and changed by hand. The owner:
"the connection port should be auto setup (not fixed just use any free when connecting)". So the proxy binds
127.0.0.1 port 0 when the tunnel comes up and Windows gives it a free port from its dynamic range. Nothing is stored;
the window, the tray's apps, the copy button and the setup guide read the port from the listening proxy, and say "a
free port when it connects" until there is one. What a fixed port bought — a browser's manual proxy setting that stays
right across reconnects — is what Sallyport's own launch does for the apps it starts, and those read the port at
launch. Two profiles can never want the same port, and something else holding one is no longer a reason a profile
cannot connect. The cost: a manual setting made by hand in another program has to be made again after each connect.

Windows' dynamic range is a machine setting (`netsh int ipv4 show dynamicport tcp`). On the owner's machine it had
been changed to 1024–15000, so the ports measured are low. A port something already listens on is never given, but
one given to a profile first is that profile's until it disconnects: a program that wants a fixed port inside the
range (Kobo's Host, 1080) has to start before, or the range exclude it.

Measured 2026-10-20 against the owner's server (jp, udp/1194). Connected from Profiles: the client listened on
127.0.0.1:10308 and nothing else; `curl --socks5-hostname 127.0.0.1:10308 https://www.cloudflare.com/cdn-cgi/trace`
returned 200 in 1.6 s with `loc=JP`, `colo=NRT`. Disconnected and connected again: 127.0.0.1:14962; after a restart
of the client, 127.0.0.1:2420, which "Copy address" put on the clipboard as `127.0.0.1:2420` and the setup guide
showed in its curl line. Not connected, the card showed "a free port when it connects" and no copy button.

## D21 — Connection settings are Settings' and every profile's; one profile's changes are a view of their own (2026-10-20; measured)

The owner: "the connection type (more like open vpn does)", "the retry setting should be simplfied", then "this
connection config should be just for setting not for a single profile only if we need to config for single profile but
that will be in a seperated view (since this is more advanced)", and of the first wording ("UDP only: udp/1194"): "its
mostly for all traffic (only port 443 or 1194 is not really the case)".

So Settings has a **Connection** group, as OpenVPN Connect's settings have "Protocol" and "Connection Timeout" (its
public documentation; a reference for what a person expects, nothing more). **VPN protocol**: Automatic, UDP or TCP —
the transport of the tunnel to the server, which carries an app's TCP and UDP alike either way, and is said so beside
it. `ConnectionPolicy.Protocol` holds a session to the remotes of that transport; a profile has no directive for it (a
transport is named per remote), so it is only ever the person's. A profile without servers of Settings' choice keeps
all of its own rather than none; one held to a transport on its own that a later import took away is refused before
anything is tried ("the profile has no TCP servers any more"), and the library refuses a session whose policy leaves it
no remote, since such a round fails at once and waits forever. **If it cannot connect**: keep trying, or stop after 3,
5 or 10 failed rounds (`connect-retry-max`; a round tries every server once).

What one profile connects with: its own directives, Settings' choices over them, and its own changes over those. Its
changes are made in **Connection settings for {profile}**, a view that takes the Profiles page — the same two choices,
the finer ones (waits, attempt timeout, silence, order, refused login) folded under More settings — and only what
differs from Settings and the profile is kept, so a later change in Settings is followed where the person did not
choose. "Automatic" for one profile where Settings say TCP is kept as `any`. Follow Settings clears them.

Measured 2026-10-20 against the owner's server (jp: udp/1194 then tcp/443). Held to TCP on its own: connected over
tcp/443, the client holding one TCP connection to port 443 and no UDP socket; `curl --socks5-hostname` through its
proxy: 200, `loc=JP`. Held to UDP: over udp/1194, one UDP socket and no TCP to 443; 200, `loc=JP`. Settings' TCP with
nothing of jp's own: tcp/443, its card "VPN protocol: TCP"; 200, `loc=JP`. jp's own Automatic over Settings' TCP:
udp/1194, one UDP socket, "Changed for this profile". Stop after 0 rounds was refused ("Use 1 to 1000 rounds.");
Follow Settings, then Settings back to Automatic, left jp as it was.

## D22 — Apps are set up on a page of their own, watched program by program, and never launched for their packets (2026-10-20; measured)

The owner, for a person who is not technical: "we dont really need start the game because this is not only for game,
also for the detail config for each exe monitoring"; of adding: "can we have the selection to be for both?", then "or
we have a proper setup page, so user can define its from begining"; and "keep in mind this is for a non-tech user to
use so the ui/ux is important".

**Adding.** "Add an app…" opens **Add an app**, a view of its own. First *Where is it?* — a path typed or pasted, or
Browse…, one dialog that takes a program or a folder (Shenora's `OpenFolderAsync` with `AllowFileSelection`: Windows'
own dialog offers one or the other, so its title says how to choose a folder). What is there is said before anything
is kept (`AppSetup.Preview`): a folder and how many of its programs go through the VPN, a program on its own, or a
browser launched with proxy settings; a program in a folder of its own brings its folder, as before. Then its name,
VPN profile and what happens when the VPN drops; then, for a folder, each program with a switch. Add app makes it and
opens its page. Nothing is written until then.

**Launching.** Sallyport starts a program only where it must: one told of the proxy as it starts (a browser). A game,
or anything routed by its packets, is started as the person always starts it; its traffic is routed from its first
packet whoever starts it. So an app that routes packets has no Launch — on its card, its page, the Overview or the
tray — and a program with proxy settings keeps its own Launch on its row.

**Each program.** An app's page lists its programs with a switch each (through the VPN or not), what each is doing, and
folded under it, how it is routed, its connections now and what went through the VPN. Two sources, each saying only
what it can see:
- *The window*, while that app's page is open and only then (`ProgramWatch`, every 2 s): its processes found by the
  executable each runs (`RunningPrograms`, limited query access, unelevated) and their sockets from Windows' owner
  tables (`OwnerTable.SocketsByProcess`: TCP rows with a far end, UDP sockets). "Not running" means no process the
  client can read runs it; a process that refuses even limited access (a protected one) is not counted either way.
- *The router*, in the service, counting by process on its own thread (`Router.ByProcess`: a flow the first time it is
  routed for a process, every packet it sends into the tunnel), summed by executable through the selection that found
  each process (`ProgramSelection.PathOf`) and sent in `RouteState.Programs`. The flow table itself is the routing
  thread's alone; it is never read from the state timer.

Measured 2026-10-20 on the installed client and service against jp (udp/1194), with a copy of the driver tool as a
stand-in app (`childbin`, its `mixed-to` workload: STUN over UDP and HTTPS over TCP every few seconds): added through
Add an app by typing its folder — "A folder with 3 programs. All of them go through your VPN while it is routed." —
named "childbin", jp chosen; Add app opened its page. Route on: "Routed through jp". The stand-in started: its row
"Running · through jp", Connections now "1 TCP · 1 UDP", Through the VPN "3 connections · 89 packets sent"; after its
40 s, "Not running" and "5 connections · 184 packets sent". The stand-in's own record, every round: "udp 4 of 4
answered, tcp 2 ok, 0 failed, the far end saw VPN". Stopped and removed after. Browse opened the dialog titled for
both choices and was cancelled; a physical click on it could not be checked from this session (the window could not
be brought forward), and UI Automation cannot see into the dialog from an elevated shell.

**The words, after a review for a person who is not technical (2026-10-20).** Codex (`codex exec -s read-only`, the
brief in `local/codex/ux-audit-brief.md`, twelve screenshots of the installed client and one of the tray menu attached)
reviewed every page as that person would. Claude checked each point against the code and the owner's own words before
adopting it; the owner's "not the 2 button" ruled out its two choosers for Add an app, and "VPNs" for Profiles was not
taken (the owner and OpenVPN Connect both say profile). Adopted:
- one vocabulary for an app: its VPN on or off — *Use VPN* beside every app's switch, *Turn on VPN* / *Turn off VPN*,
  *VPN on · jp*, *VPN off*; "route" and "routing" are gone from what a person reads, kept in the experts' notes;
- "held" said as what it does: *Block its internet until the VPN is back*;
- after a first import, the window goes to Apps to add the game;
- an app's page: its header without the loss policy (Setup has it), no empty Monitoring card — a next step instead —
  the programs it uses first, the others folded under *Other programs (n)*, the folder on a line of its own;
- the Overview says nothing of the proxy before it has a port, and shows the driver's card only when something needs
  doing; Profiles likewise;
- About folds its long paths under *Technical details*; the guide's steps say what to do now;
- the title on the content's edge at the window's narrow width; heading buttons and *Open Apps* drawn as buttons; Add
  app's reason under the pair rather than between them.

## D23 — An app's Use VPN is a choice that stands; Connect is the connection (2026-10-20; measured)

The owner: "the vpn toggle for app should not be a connection that should be enabled the route or not and connect for
vpn is the actual connection and the app toggle can enable during vpn on or off". Until now an app's switch started a
run, which brought its profile up itself, and one app was routed at a time.

- **The switch is kept** (`RoutedItem.UseVpn` in `apps.json`). On, the app goes through its profile whenever the person
  has connected that profile; it never connects anything, and stays as set while the profile is not ("Waiting for
  jp" — "Connect jp and it goes through the VPN."). An app added is added with it on.
- **The client follows a plan** (`GameRunner.Want`): the apps switched on whose profile the person connected. It starts
  the service's run when there is one, changes it while it runs, and stops it when none is left or the person
  disconnects. It joins only a connection the person made (`Connections.Acquire(open: false)`). The service still runs
  one route, so a second connected profile's apps wait, and say so. A run that ends on its own (a service that refuses,
  a driver missing, the guard) is not started again until the person asks again: a switch turned on, a connect, the
  driver put in.
- **One route, several apps** (`RouterProtocol` version 2): the run's programs each carry their app's loss policy
  (`ProgramRule`). While the tunnel is lost the route holds, and lets go program by program (`GameRoute` with `lossOf`,
  `Router.DirectWhileHeld`): *own connection* at once, *block* after its wait if it has one; before the first tunnel
  everything is held. *Turn the VPN off for it* turns that app's switch off when the loss is noticed. Two apps naming
  one program: the strictest rule stands.
- **Changed while it runs**: `SelectPrograms` on the run's pipe. The worker's router takes the new selection between
  two batches and decides every flow again (`Router.Reselect`, `ProgramSelection.Replace`): nothing restarts, and the
  other apps' connections are not touched. So an app's programs, profile and loss policy can be changed while it goes
  through the VPN; the selection follows.
- **Icons**: an app shows its program's own icon (`AppIcons`: the executable's icon at 64 px, a PNG data URL — the page
  is given no path); *Use its icon for the app* on another of its programs.
- **The tray** connects and disconnects profiles only. The owner: "we dont need the app selection in the tray menu",
  "the tray menu just simplily connect with the configured profile + apps config".

Measured 2026-10-20, the service and client installed in `D:\Program Files\Sallyport`, against jp, with two copies of
the driver tool as stand-ins (`childbin`, `childbin2`) and the guard outside (0 failed in every run):
- *Two programs, two policies, one route* (`service-route --child … --child2 … --loss hold --loss2 direct
  --ping-restart 15`, the server cut by Windows Firewall at 33 s for 40 s): the loss noticed at 47.9 s; from then the
  held child had no answer (54 packets held) and the other was seen at home within 1.4 s (53 sent direct); generation 2
  at 75.2 s, both seen at the VPN again within 2 s; 0 late flows.
- *Turned on while it runs* (`--start 1 --select-after 16 --select 12`): the second child seen at home until the
  selection, at the VPN from the next 2 s; the first answered 4 of 4 throughout.
- *Turned off while it runs* (`--start 12 --select 1`): the second child at home from the next 2 s, the first at the
  VPN, 4 of 4, throughout; no restart.
- *Through the client* (UI Automation on the Chinese window): `childbin2` added by `--add-game` with its switch on —
  "等待 jp"; jp connected from the Overview's switch — the service's run with its 3 programs, "VPN 已开启 · jp". Its
  `mixed-to` workload running: Zenless turned on at 20 s ("selection now 6 program(s)"), the stand-in still the VPN,
  every request answered; the stand-in turned off at 40 s ("selection now 3 program(s)") — one HTTPS request failed at
  the switch, then home every round; Zenless off at 60 s — the run ended, "stopped by the client", 5 flows decided
  again, 0 late.

## D24 — Apps and profiles are many to many: each profile a section of its own, each lane of the one route (2026-10-20; measured)

The owner: "split the config of which profile use for the app in the app section so we make a turly N2M relationship
for app and profile and combine them in the overview so we can have a vpn section with multiple apps and multiple
configuration of profile + apps"; of the tray, "we dont need the app selection in the tray menu", "the tray menu just
simplily connect with the configured profile + apps config".

- **An app is in profiles' sections** (`RoutedItem.Uses`: a profile and the switch beside the app there, D23's
  choice kept per section). An app may be in many sections and a section holds many apps. An earlier client's one
  profile and switch read as one section and are never written again. An app has no profile field of its own any
  more: Add an app puts it in a first section ("Goes through", or none for now); more are set on the Overview or on
  its page. Removing a profile takes it out of every section; the apps stay.
- **The Overview is one section per profile** (`SectionView`), in import order, connected or not: the profile's switch,
  its traffic, ping and details while connected, its apps each with their own switch, Launch for a browser (launched
  through that section's proxy), a remove, and a picker of the apps not in it. **An app's page** lists every profile
  under *Goes through*, the same switch as its row in that section. The Apps list says where each app goes, without a
  switch of its own. **The tray** connects and disconnects profiles; what goes through each is the section's.
- **One route, a lane per profile** (`Router.Lanes`, eight): each connected profile whose section has an app switched
  on is a lane of the service's one run, with its own NAT, path and generations (`GameRoute` per lane; a generation on
  the pipe carries its lane in its top byte, so lane 0's are the session's own numbers — `RouterProtocol` version 3).
  Each program's rule names its lane (`ProgramRule.Lane`), so a flow is decided into its program's profile's tunnel and
  stays there; a lane is lost, held or let go on its own, the others untouched. The client's runner (`GameRunner`)
  joins a lane to each profile the person connected and lets it go when its profile is disconnected or carries nothing.
  An app switched on in two connected sections goes through the first, the one already carrying it first; the other
  section says "Through us now".

Measured 2026-10-20 against jp's server, with the guard outside (0 failed):
- *Two lanes, one run* (`service-route --child … --child2 … --lane2 --end-lane2-after 22`: the second copy in lane 1,
  through a second session of the profile — the server took two at once): both lanes "Routing", generation 1 each;
  both copies answered 4 of 4, the far end seeing the VPN. The second session ended alone at 26 s: lane 1 "Held", its
  copy 0 of 4 from then (53 held), the first copy 4 of 4 throughout; lane 0's dispatcher carried only its own (88).
- *Through the client* (UI Automation, the window in Chinese; jp imported a second time to have a second profile,
  removed after): the stand-in put in the second section from its picker; Zenless on in the first; both connected —
  the service's run: "selection now 6 program(s)", "generation 1 of lane 1 committed"; the stand-in's `mixed-to`, 25
  rounds, every request answered, the far end the VPN. The second jp disconnected — "断开 jp? childbin2 正在经由它…" —
  and confirmed: "generation 1 of lane 1 retired", "selection now 3 program(s)", the run going on with Zenless; the
  stand-in at home every round after. The first section showed Zenless "VPN 已开启 · jp", the second the stand-in
  "等待 jp" with its switch still on, and the stand-in's page both profiles under 经由. Zenless off, jp disconnected:
  the run ended, "stopped by the client".
