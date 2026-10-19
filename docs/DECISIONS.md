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

## D10 — Games are routed at the packet level, through WinDivert (2026-10-19; to be measured)

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

## D11 — Imported profiles are held by the service, encrypted (2026-10-19)

A profile holds a private key and a static key. The client imports a copy (the owner chose a copy over a path, so it
keeps working when the original moves), and the service owns it: `%ProgramData%\Sallyport\profiles`, readable by SYSTEM
and Administrators only, encrypted at rest with DPAPI's machine scope (`CryptProtectData` through P/Invoke, no package).
The unelevated client sees a profile's name, remotes' protocols and status — never its key material.

