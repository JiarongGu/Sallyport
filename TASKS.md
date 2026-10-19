# TASKS.md — open work only, in order

A finished item is deleted (git is the archive). Work goes top to bottom, one at a time; an item is done when what it
says under **Done when** has been measured — against the real server where a server is involved (CLAUDE.md rule 3).
**Needs** names what only the owner can give.

## 1. Stage 1 — every packet of a game's programs, on Windows Packet Filter
Per-process routing from the first packet (game-routing §5, amendments 1–2): packets taken at the network layer,
matched to their process through the owner tables, NAT'd into the tunnel, replies put back. Our code MIT; the driver
NT Kernel's, free for personal use.
- **Done when:** Defender accepts the driver; Zenless Zone Zero runs beside it; a routed program's TCP and UDP leave
  through the tunnel from their first packet while another program stays direct; a peer-to-peer harness (one side
  behind the home NAT, one behind the VPN's) connects through it.
- Approved (D18): free for personal use while Sallyport is not commercial. The app manages the driver
  (`Sallyport.Windows`, `tools/Sallyport.Driver`): NT Kernel's pinned installer fetched and checked, installed and
  removed with one administrator prompt.
- **Measured 2026-10-20:** installed in 25 s; Defender: no detection; signed by Microsoft (WHCP); bound to every
  adapter; the direct path unharmed. **Opening the driver needs administrator** (a basic-user token: access denied), so
  the router runs elevated — a Windows service installed with the driver under the same prompt is the shape that keeps
  one prompt for the person; the profile then has to reach it without its text crossing a command line (D14).
- Also measured: Zenless Zone Zero starts and plays with the driver installed; removal through the app in 30 s and
  reinstallation in 36 s, cutting the network about 6 s and 2 s while adapters are released and bound (D18).
  `ndisrd.sys` stays in `System32\drivers` after removal, inert — whether to delete it is open.
- **The router is designed** (`docs/design/router.md`, Codex's, checked and adopted with three changes): a LocalSystem
  service takes, attributes, NATs and injects; the tunnel stays in the client and only packets cross the pipe. Built
  in the design's gates, each measured before the next:
  1. **The driver's interface and recovery — measured 2026-10-20** (`tools/Sallyport.Driver adapters | pass | reset |
     guard`, a guard probing 1.1.1.1:443 and 8.8.8.8:443 twice a second outside every interceptor): the adapter list
     read back as the header lays it out (12 adapters, names, MACs, MTUs matching Windows'); Wi-Fi, the route to the
     internet, put in tunnel mode and every packet handed back unchanged for 20 s and 15 s — ~180,000 packets each,
     guard 0 failed of 272 and 43 rounds, connect p50 21.8 ms against 19.1 ms without. **A killed interceptor leaves
     nothing behind:** the adapter read `None`, queue 0, at once, no probe failed. **A second handle resets a mode:** it
     read the first's `SentTunnel, ReceiveTunnel` and set `None`; the first's packets stopped that second, nothing
     broke. The driver answers a read of an empty queue with error 87, and writes no count back for a send (its
     success is the answer); the largest frame either way was 1514.
  2. Unchanged forwarding — cost (CPU, latency p50/p95/p99, loss) against the direct baseline. ← next
     Measured in gate 1: 3.0 s of CPU over 15 s at ~12,500 packets/s, 95% of them inbound (a download). Routed replies
     come out of the tunnel, not off the adapter, so the router may not need the receive path: measure whether
     `SEND_PACKETS_TO_MSTCP` delivers on an adapter that is not in receive-tunnel mode.
  3. Attribution: short-lived, reused and shared sockets — owner found, or counted as unattributed.
  4. One program routed from its first TCP SYN and UDP datagram through the real VPN; another program direct.
  5. Packet boundaries: DF/MSS, fragments, ICMP, offloads; selected IPv6 blocked.
  6. Zenless routed whole; mid-game attach and detach; tunnel loss, driver removal, sleep.
  7. The peer-to-peer harness.
- **Attach mid-game** (the owner: "we should be setup vpn in mid of the game too"): the driver moves a running
  session's packets at once, which Windows' VPN cannot (measured, D16's route, 2026-10-19). Measure whether Zenless's server keeps a session
  whose client address changes mid-game, or reconnects it.

- **If Defender refuses the driver,** the driverless route (D16) is what remains, and its relay's inbound loss comes first:
  in the last Zenless run the bridge sent 3,870 packets to the plug-in and Windows delivered 2,100 — about 45% lost on
  the loopback leg, under bursts (HTTPS downloads); gameplay alone stayed well below it.

## 2. Observer hardening
- A killed observer leaves its private ETW session running: on start, find `Sallyport-Observer-<pid>-*` sessions whose
  process is gone and stop them; never touch any other session.
- Short-lived children and a launcher's handoff (the game started by `HYP.exe`), measured.
- **Done when:** a killed observer's session is gone after the next start (measured with `logman query -ets`); a game
  launched from its launcher is attributed from its first connection.

## 3. The bridge opens stored VPN profiles
`route` takes a `.ovpn` path today. For the client it must open a profile from the sealed store by id (D14, DPAPI under
the same user), so no profile's text crosses a command line or a pipe.
- **Done when:** the packaged bridge opens a stored profile by id and connects — which also measures whether a packaged
  process sees `%LOCALAPPDATA%\Sallyport` or a virtualised copy (game-routing design, "not yet measured").

## 4. Games in the client
A Games page — a game profile made from a folder, each program included or left out, rescan, its learned destinations
— and **Run**: one game profile and one VPN profile, driving the bridge, the guard supervised. Designed with Codex in
the window's contract style (D15), built, measured.
- **Done when:** Zenless Zone Zero is created from its folder, learned, and run with the JP profile from the window,
  every state shown by the host; window and tray agree.

## 5. Stage 2 — our own WFP driver behind the same seam
Replaces `ndisrd.sys`; nothing above the seam changes.
- **Needs:** a registered business for an EV code-signing certificate and a Microsoft Hardware Dev Center account
  (attestation signing; drivers are not signed by Microsoft's cloud signing service); a test VM, because development
  needs test-signing mode, which anti-cheats refuse.
- **Done when:** the attestation-signed driver passes item 1's measurements, and Zenless Zone Zero runs beside it.

## 6. What the client's measurement did not reach (2026-10-19)
The file dialogs past opening and cancelling (this machine's dialog shows UI Automation no File name box; app shortcuts
were seeded instead), Firefox and Electron launches, the dark theme of the new window, the tray driven by hand.
- **Done when:** each is measured, or recorded as could-not-be-checked with the reason.

## 7. Ship
An installer: the client, and the game-routing part (the VPN package with its loopback exemption today, the driver
after item 1 or 5), one administrator prompt. A Start-menu entry, `.ovpn` files opened with the client, start with
Windows (`--tray`). The development set-up comes off this machine: the test certificate, the exemption, the package
(`apps/Sallyport.Vpn/README.md`).
- **Needs:** a certificate others trust, for the package and the installer.

## 8. Phase 4 — the long run (the library)
Renegotiation (`ControlStream` refuses a soft reset, so a server that renegotiates costs a reconnect; the owner's did
not in a 75-minute run on 2026-10-13, though OpenVPN's default is hourly), `auth-user-pass`, `tls-auth`, `tls-crypt-v2`
and `P_DATA_V1` profiles, uploads at volume (the send side has no SACK of its own), and allocation on the receive path
(about 300 MB allocated per 64 MB downloaded, D7; gen 0/1 collections of 5–11 ms now stall the network's single
reader).
- **Written but never run against a server:** the reconnect path (nothing ended the tunnel in 75 minutes); whether the
  server acts on the exit notices (`explicit-exit-notify`, `EXIT`).
- **Measured already:** the keepalive (75 minutes up, idle 5 minutes between probes, never dropped); the TCP transport
  (`tcp/443`, TLS 1.3, ipinfo JP Tokyo, 10 MB at 1.34 MB/s, TCP inside TCP).
- Kobo runs the library as a provider process (`Sallyport.Host.exe serve|stop`); in-process hosting waits for the release.

## 9. Release
