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
  2. **Unchanged forwarding, and the receive path — measured 2026-10-20** (`pass --mode send|both`, `inject`, `rtt`,
     the guard outside: 0 failed of 142): **a reply handed up with `SEND_PACKETS_TO_MSTCP` reaches its socket with the
     adapter's receive path not taken** — a UDP socket sent to 192.0.2.1:40000 (TEST-NET-1), the datagram was kept off
     the network, the reply built from it was delivered, 3 of 3 (twice send-only, once both); Windows' firewall let it
     in on the socket's own outbound flow, the router's case exactly. So the router takes **only what is sent**: 16 s
     of a download's load cost 844 ms of CPU send-only against 2,984 ms with both paths (~9,700 packets against
     ~173,600). Gateway round trips, 500 each, p50/p95/p99: 14.9/50.8/78.2 ms untouched, 9.7/42.8/58.9 both paths,
     10.8/46.8/59.5 send-only — Wi-Fi's own jitter is larger than anything added; the router times its own processing
     for a figure.
  3. **Attribution — measured 2026-10-20** (`Sallyport.Windows.Routing`: `Frames`, `OwnerTable`; `attribute`, three
     runs of 15–20 s on the send path, a refresh on a miss at most once per drain; a child process playing cases tagged
     in their payloads to TEST-NET addresses, kept off the network; guard 0 failed): **no packet attributed to the
     wrong process except by a reused port, and none ambiguous.** First TCP SYN 29 of 29 right, after one refresh;
     UDP sockets living 100 ms 59 of 60 (the one: the harness's once-per-drain refresh); one socket to 50 peers 150 of
     150; **sockets closed the moment they send: 46 of 60 unowned** — gone before the table was read, the design's
     limit, counted. **Port reuse: a port released by one process and taken at once by another was attributed to the
     old owner, 5 packets over 250 ms, in one run of two** — no refresh fell between; so the router bounds staleness:
     a periodic refresh, each flow's decision rechecked against it. A refresh reads both tables in p50 1.0–1.5 ms, p99
     2.4–4.3 ms. Elsewhere on the machine ~12–24 TCP packets per run were unowned, one each to :443 — final packets
     of connections in TIME_WAIT, whose rows carry PID 0 — so the router decides at a flow's first packet and keeps
     the decision. DNS a game asks for is sent by Windows' DNS Client service, not the game: it stays direct.
  4. **One program routed from its first packet through the real VPN — measured 2026-10-20** (`Sallyport.Windows.Routing`:
     `Router`, `Nat`; `Sallyport.Driver route <profile>`: the tunnel to the JP server over UDP in this process, as the
     client will hold it; the adapter send-only; guard 0 failed; two runs): a child process selected before it sent
     anything, this process not. **The child's UDP to a STUN server: 5/5 answered, the far end saw the VPN's address
     with the child's own port** (the NAT kept it, and so did the server's); **its HTTPS to an address echo: the VPN's
     address.** This process, the same calls at the same time: the home address both ways. Each of the child's two
     flows was decided "routed" at its first packet — the TCP flow's first is its SYN — none left directly; 13 packets
     routed, 13 and 14 replies handed up, none refused, none held back, no late flow. STUN round trip p50 21–22 ms
     direct, 235–281 ms through the VPN in Tokyo.
     Decisions are made per five-tuple at the first packet, after at most one refresh (2 ms apart), and kept for the
     flow's life; UDP decisions are rechecked every 250 ms (the reused port of gate 3).
  Built since: **selection by a game folder's executables** (`ProgramSelection`: image path and creation time of the
  owning PID, so it holds from launch whatever starts the game), decisions and mappings expiring after 2 minutes idle
  unless a socket still holds them; `route --folder` and `stop`.
  5. **Packet boundaries — measured 2026-10-20** (`route --workload boundaries`: the same calls from this process,
     direct, and from a selected child, routed; five runs, Windows' path-MTU cache flushed before each; guard 0 failed):
     - **MSS:** the server clamps its own SYN-ACKs to 1286 (OpenVPN's mssfix), under the router's 1360 either way; a
       2 MB HTTPS upload: 200 every run.
     - **Too big for the tunnel** (1448 on this 1500-byte path, less the outer IPv4, UDP and data channel's 52): a
       1,500-byte datagram *with* don't-fragment is answered with "fragmentation needed" — Windows took it (its
       path-MTU cache read 1448 for that server) and dropped the program's next such send itself. That cache is the
       machine's, per destination: an unselected program sending there gets the smaller size too, for about 10 minutes
       (measured: the direct control's 1,500-byte datagram then went in pieces, unanswered). *Without* don't-fragment
       it is sent whole and Windows splits the outer datagram: Google's STUN server answered it in 3 runs of 3. Split
       inside the tunnel instead, 0 of 2 — that server ignores fragments (measured direct) — so whole is the default.
     - **Pieces:** a 3,000-byte datagram Windows sends in pieces is routed piece by piece, its checksum carried over the
       NAT rather than recomputed (Quad9 answered 3 of 3). A 3,959-byte reply in three pieces (Akamai's name server)
       was handed up and put together by Windows 14 of 16, both misses in the two runs whose tunnel lost packets;
       pieces that came before their first were held for it (up to 3 a run, none lost).
     - **ICMP:** time exceeded one hop out (the VPN server) turned back to the program's own flow, 3 of 3 each run.
       **Port unreachable could not be checked:** no far end sent one (the tunnel's gateway, routers on the path,
       scanme.nmap.org).
     - **Blocked for a selected program, and counted:** broadcast, multicast, its own LAN (TCP to the home router: a
       reset, refused in ~2 s — Windows retries a refused connect itself), and IPv6 (multicast; TCP to a link-local
       neighbour: a reset). Unbound, Windows sends broadcast and multicast by the adapter of lowest metric, here not
       Wi-Fi: those never reach the router — LAN traffic only, but outside it. **External IPv6 could not be checked:**
       this machine has no IPv6 route.
     - **Offloads:** Wi-Fi has large-send offload on, yet no frame over 1514 in 1.3 million sent, a 40 MB upload among
       them; partial checksums are recomputed whole.
  6. **Zenless Zone Zero routed whole, and detached and attached mid-game — measured 2026-10-20** (`route --folder`,
     the owner in menus and walking around; guard 0 failed): armed before launch, **all 116 of the game's flows were
     routed from their first packet** — 115 TCP (its HTTPS), 1 UDP (the game itself, to its server, 2,515 packets) —
     none direct, none late, none held back. Routing stopped mid-game and started again, twice (the game direct for
     100 s, then 20 s): the game kept its UDP socket, its server answered at the new address at once, and no new login
     connection opened. **Its ping:** the server answers nothing but the game, so the farthest hop that does answer
     stands for it — through the VPN, hop 9 (China Telecom, Shanghai) p50 267 ms, of which the VPN server itself
     195 ms; direct (by way of the US) the same network at hops 16–17, p50 311–350 ms. The game's own datagrams waited
     p50 ~300 ms for its server's next. The tunnel lost, the driver removed and sleep during a game: item 2, steps
     5–7.
  7. The peer-to-peer harness: item 2, step 10.

- **If Defender refuses the driver,** the driverless route (D16) is what remains, and its relay's inbound loss comes first:
  in the last Zenless run the bridge sent 3,870 packets to the plug-in and Windows delivered 2,100 — about 45% lost on
  the loopback leg, under bursts (HTTPS downloads); gameplay alone stayed well below it.

## 2. The product — `docs/design/product.md`, built in its steps
One install; a game profile and a VPN profile, **Run**. The tunnel reconnects by its profile's connection policy
(OpenVPN's directives, which the person can override); while it is down, the game's traffic is held, sent direct, or
the route stops, as the game profile says (the owner: *"there can be auto reconnect mode with different config (this
is more like openvpn)"*). The app manages the driver. Each step is measured before the next (rule 3).
1. **The connection policy and the session's rounds:** `connect-retry`, `connect-retry-max`, `remote-random`,
   `server-poll-timeout`, `auth-retry`, `ping`/`ping-restart`/`keepalive` read from the profile (`ConnectionPolicy`);
   `TunnelSession` keeping to them, with a consumer built for each tunnel generation.
   - **Measured 2026-10-20** (`Sallyport.Driver reconnect`: the JP profile behind a remote that never answers,
     TEST-NET-1; attempts 5 s; once up, the server's address dropped at the adapter both ways for 150 s; the guard
     outside, 0 failed): the dead remote timed out at 5 s and the server was up 1.4 s after. The server pushes
     `ping 12`, `ping-restart 50`; **the cut was noticed 48 s in** — its ping-restart, from the last packet received.
     Each round then went dead remote, udp/1194, tcp/443 (5 s each) and waited 1.9–2.1 s (2 s ±10%), 3.8 s after the
     fifth failed round and 8.2 s after the sixth — the manual's doubling after five; **up again 14.3 s after the cut
     was freed**, as generation 2 (that 8 s wait, then the dead remote's 5 s). **An authentication refusal could not be
     checked against the server**, which refuses nobody; the session ends on one in tests (`FakeServer`).
2. **Consumer generations — measured 2026-10-20, with step 5's router, in one process** (`Sallyport.Windows.Routing`:
   `GameRoute` over a `Router` whose selected traffic goes into the current generation's NAT, is held, or goes direct;
   `Sallyport.Driver route --workload steady`: a selected child asks Google's STUN server every 500 ms and says which
   address it was seen at; 25 s in, the server's address dropped at the adapter both ways for 80 s, inside the router's
   own interception; guard 0 failed). Each tunnel's pump commits its generation and retires it when the session lets
   the tunnel go; one reader each; nothing stale handed up in any run. **Every reconnect brought a new tunnel address**
   (…111 → …112, …113 → …114, …116 → …117), and the child was seen at the VPN's again within 2 s of it.
   - **Hold:** no answer from the cut until generation 2 (61 packets held); up 1.2 s after the cut was freed.
   - **Direct:** from the moment the loss was noticed the far end saw the home address (61 packets direct); the VPN's
     again 1.3 s after the cut was freed.
   - **Stop:** the home address from the moment the loss was noticed; no reconnect.
   - **Hold, direct after 15 s, and the client's own `ping-restart 15` over the server's** (`pull-filter ignore`):
     the loss noticed 15.0 s after the cut, held 15 s, then the home address; the VPN's 4.3 s after the cut was freed.
   - **What a loss costs before any policy acts: the ping-restart in force** — the server pushes 50 s, so for 50 s the
     game's packets go into a dead tunnel. The person can set a shorter one of their own (OpenVPN's `pull-filter
     ignore "ping-restart"`, read from the profile too); `docs/design/product.md` change 4 keeps OpenVPN's precedence
     with that override, Codex's own proposal in the end.
   - Found and fixed on the way: a tunnel's packets end with the error that ended it, which escaped a pump's dispose
     and took the session down; a consumer that throws as it goes now ends itself, not the session (`ConsumerFailed`).
3. **The service skeleton — measured 2026-10-20** (`apps/Sallyport.Service`, installed from a development build in
   `%ProgramFiles%\Sallyport`: LocalSystem, automatic, restarted 1/5/30 s after a failure; a supervisor, and a worker
   per run in a job object that dies with it; `Sallyport.Windows.Service`: the SCM by P/Invoke, the pipe's DACL —
   system full, interactive users read/write, the network denied — `PipePeer`, `RouterProtocol`, `RouterClient`;
   `Sallyport.Driver service-hello | service-malformed | service-failure`; a selected copy of the tool outside the
   folder playing steady STUN; the guard outside, 0 tripped):
   - **Front door:** the client in the folder welcomed; Python refused ("not Sallyport's own client") before a byte
     it sent was read; a copy of the tool outside the folder refused by the service, and the service refused by it
     ("does not run from Sallyport's folder"). A hello of version 99 rejected; a 50,000,000-byte frame and JSON cut
     short each closed their connection, and the next client was welcomed.
   - **A run through it:** prepared and attached in ~1 s; the child's STUN 5/5 and HTTPS seen at the VPN's address
     (its port kept), this process's at home; generations committed and retired over the run's pipe.
   - **The worker killed:** the adapter given back in 0.06 s. **But the driver gives an adapter back on its own only
     when the last handle to it closes:** the first run, with the measuring process holding a handle, left the
     adapter intercepting with 1,845 packets queued — the machine's traffic held 4.2 s, until the guard outside reset
     it. So the supervisor resets the run's adapter itself on every end: with a handle held, 0.06 s.
   - **The worker frozen:** everything Windows sent waited in the driver, the machine's traffic with it. The guard's
     3 s ended it the first time (4.4 s); the worker now reports the router's drains four times a second, and one
     still for 1 s ends the run: **1.46 s**, the guard not tripping.
   - **The supervisor killed:** the job object took the worker; the adapter given back in 0.07 s; the service
     running again 1.3 s later. **The client frozen:** its lease ran out and the run ended in 2.93 s.
   - In every failure the selected child went from the VPN's address to its own: routing fails open.
4. **The packet pipe — measured 2026-10-20** (`Sallyport.Driver service-route --workload cost`: a selected copy of
   the tool times twenty STUN round trips and a 20 MB HTTPS download, the same as `route` in one process; guard 0
   failed over the last runs):
   - **Round trips: the pipe adds nothing seen** — STUN p50 193–208 ms through the service, 196–206 ms in one process.
   - **Downloads:** 5.4–6.6 MB/s in one process; through the service 2.4–4.6 MB/s with a pipe write per packet, then
     3.0–6.0 MB/s once each side wrote what it had queued in one write (`FrameStream.PumpPacketsAsync`).
   - **Found: a TCP connection's SYN left unowned, its whole connection direct, in 2 runs of 12** — the selected
     child's download among them. A miss read the owner tables again only if they were 2 ms old; the guard's two
     probes, a SYN each at the same moment, used the reading up (logged: "read 0.0 ms before, not read again", no row
     on the port). Now a miss reads them again unless they were read after the packet's batch left the driver — such a
     read holds every socket that sent in it: **14 runs since, 0 unowned packets** of any process on the machine,
     0 SYN misses. Gate 3's harness sent no SYNs in pairs.
   - **One stall:** on a machine starved for a moment (the worker 5.3 s to start, the direct path slow), the router
     stood still past 1 s, the machine's traffic held 2–3 s, and the supervisor ended the run. Its threshold is 2 s
     now, inside the guard's 3, and the worker names each phase of the router's loop that takes over 200 ms. None
     since, in 6 runs.
   - The tunnel's own socket stayed direct throughout: the client is never a selection, and the tunnel never carried
     itself.
5. **The router across generations, through the service — measured 2026-10-20** (`service-route --workload steady
   --ping-restart 15`; the outage now made by Windows Firewall blocking the server's address both ways — it stops a
   UDP flow already open, and the worker's adapter is not disturbed by a second handle; guard 0 failed): the loss
   noticed 15 s after each cut, as in one process, and every reconnect with a new tunnel address, nothing stale.
   - **Hold:** no answer until generation 2, up 2 s after the cut was freed (53 packets held).
   - **Direct:** the home address from the moment the loss was noticed (65 packets direct), the VPN's again 3 s after.
   - **Stop:** the home address from then on (117 direct); the client ends its tunnel too.
   - **Hold, direct after 15 s:** 15 s held (29), then home (36), then the VPN's 2.6 s after the cut was freed.
6. **Adapters:** Wi-Fi and Ethernet both up, a handover, sleep, the driver removed → what the packets did, every gap
   said, the machine back on its own.
7. **Zenless through the service:** Run from launch, attach and detach, a real drop under each policy, sleep → the
   game's session and the coverage recorded. **Needs:** the owner, playing.
8. **The client — `docs/design/window.md`** (the owner: one Apps list, every routing method in one channel per profile;
   tabs Apps · Network · Profiles · Settings), in its working increments, each with C# view tests and the real window
   driven through UI Automation:
   1. **Four tabs over what exists — measured 2026-10-20** on the installed client (`D:\Program Files\Sallyport`),
      through UI Automation: opens on Apps (packet section with Zenless, proxy section empty), Network "Nothing is
      connected" with Open Apps / Open Profiles and the routing card ("Packet routing is ready"), Settings (driver and
      service, start with Windows, how to use, about: version, installed in D:, the data in `data\users\<SID>`).
      Start with Windows on → HKCU Run `"D:\Program Files\Sallyport\Sallyport.Client.exe" --tray`, off → gone. jp
      connected from Profiles → Network: one channel, "SOCKS5 proxy on 127.0.0.1:1088", udp/1194, its tunnel address;
      Disconnect there → nothing connected. Zenless run from Apps → Network: "1 connection · 1 app routed", the jp
      channel connected with Zenless as its selection; Stop routing there → the service's run ended ("stopped by the
      client"), the adapter `None -> None`, 0 queued. Could not be checked through UI Automation: the tray's Open
      Network and Settings items (the tray's menu is not reached from it).
   2. **One store — measured 2026-10-20** (`apps/Sallyport.Client/RoutedItems.cs`): an app is a program or a folder,
      each program with an id, its inclusion and who chose it, and its methods (packets; proxy at launch with its own
      browser folder); one versioned `library\apps.json`, written beside and moved in, the migration read back before
      it counts; the earlier games folder and settings left as they were. On the installed client the first start
      migrated the owner's Zenless (its 8 programs, their choices and reasons, jp, hold) — the window showed it as
      before; a program included from the window was still included after a restart, and left out again.
   3. **One reader per tunnel:** a dispatcher, the port split (proxy 40960–49151, the router's NAT the rest), pieces to
      the router; measured with packet TCP/UDP and proxy TCP/DNS through one login at once.
   4. **`ChannelManager`:** one channel per profile, shared by proxy and packet routing; the "one profile serves a game
      or a proxy" refusals removed after step 3's measurement; stopping one method leaves the other working.
   5. **Many channels in the service's one run:** selections on different profiles at once; one channel's outage;
      the adapters back when the last ends.
   6. **The Apps editors:** add a program or a folder, methods per program, Route / Launch / Stop, conflicts refused.
   7. **Network and Settings in full:** retry telemetry and a true countdown, latency through the dispatcher, connection
      settings as own / override / effective, start with Windows, defaults for new apps.
   - **Done when:** Zenless Zone Zero is an app from its folder, routed by packets while a browser of the same profile
     uses its proxy, through one login; window and tray agree with the service's and the server's evidence.
9. **Setup:** `apps/Sallyport.Setup`, BCL-only and self-contained, one administrator prompt — the files in the
   folder the person chooses (`%ProgramFiles%\Sallyport` unless they choose otherwise; the owner's is on D:, nothing
   of it on C:), its data (service log, setup record, driver installer) in `data` inside it, the service, the driver on
   request, a Start-menu entry, `.ovpn` under Open With, start with Windows per user, the uninstall entry, upgrade in
   place, and removal that leaves the machine as it was (user data only if asked).
   - **Begun 2026-10-20, on the owner's machine** (`package.ps1`: 302 files, 145.6 MB unpacked, 55.2 MB zipped, a
     104.7 MB setup). The window: choose the folder (checked as typed — a fixed NTFS drive, not a drive's root nor
     Windows' own, empty or Sallyport already, twice the payload free), the driver option (seen as already installed),
     a progress bar, the end. The owner installed to `D:\Program Files\Sallyport` from it: the folder owned by
     Administrators, its rights SYSTEM and Administrators full, Users read and run, not inherited — where `D:\Program
     Files` itself lets Authenticated Users modify; the service `Auto`, running from D: and logging to
     `data\logs\service.log`; the uninstall entry's `InstallLocation` and `UninstallString` on D:; the Start-menu
     entry; no `C:\Program Files\Sallyport`, no `C:\ProgramData\Sallyport`. Run again, setup offers only to
     reinstall where Sallyport is. Then everything else off C: (D19, measured there): each user's part in
     `data\users\<SID>`, moved from `%LOCALAPPDATA%` on the first installed start; setup's log in `data\logs`; the
     driver's own files in `D:\Program Files\Windows Packet Filter`. Removal keeps `data\users` unless asked.
     Not yet: an upgrade across versions, an interrupted install, removal, a clean machine.
   - **Done when:** install, upgrade, an interrupted install and removal are each measured on a clean machine, the
     network usable throughout. **Needs:** a clean Windows machine or VM.
10. **The long run:** hours on the real server with outages, and the peer-to-peer harness → bounded memory and
    handles; NAT and replies right.

- The observer (`apps/Sallyport.Observer`) and the VPN package (`apps/Sallyport.Vpn`) are not in the product: they
  stay the driverless route, for if Defender ever refuses the driver (item 1). Hardening the observer (its ETW session
  left behind when killed) belongs to that route.

## 3. Stage 2 — our own WFP driver behind the same seam
Replaces `ndisrd.sys`; nothing above the seam changes.
- **Needs:** a registered business for an EV code-signing certificate and a Microsoft Hardware Dev Center account
  (attestation signing; drivers are not signed by Microsoft's cloud signing service); a test VM, because development
  needs test-signing mode, which anti-cheats refuse.
- **Done when:** the attestation-signed driver passes item 1's measurements, and Zenless Zone Zero runs beside it.

## 4. What the client's measurement did not reach (2026-10-19)
The file dialogs past opening and cancelling (this machine's dialog shows UI Automation no File name box; app shortcuts
were seeded instead), Firefox and Electron launches, the dark theme of the new window, the tray driven by hand.
- **Done when:** each is measured, or recorded as could-not-be-checked with the reason.

## 5. Phase 4 — the long run (the library)
Renegotiation (`ControlStream` refuses a soft reset, so a server that renegotiates costs a reconnect; the owner's did
not in a 75-minute run on 2026-10-13, though OpenVPN's default is hourly), `auth-user-pass`, `tls-auth`, `tls-crypt-v2`
and `P_DATA_V1` profiles, uploads at volume (the send side has no SACK of its own), and allocation on the receive path
(about 300 MB allocated per 64 MB downloaded, D7; gen 0/1 collections of 5–11 ms now stall the network's single
reader).
- **Written but never run against a server:** whether the server acts on the exit notices (`explicit-exit-notify`,
  `EXIT`). The reconnect path is item 2's first step.
- **Unexplained:** a 112-minute idle tunnel on 2026-10-20 dropped 225 control packets (`ControlDropped`: failed
  tls-crypt or named another session), about one every 30 s; the next sessions that night dropped none.
- **Measured already:** the keepalive (75 minutes up, idle 5 minutes between probes, never dropped); the TCP transport
  (`tcp/443`, TLS 1.3, ipinfo JP Tokyo, 10 MB at 1.34 MB/s, TCP inside TCP).
- Kobo runs the library as a provider process (`Sallyport.Host.exe serve|stop`); in-process hosting waits for the release.

## 6. Release
- **Needs:** a certificate others trust, for the setup and the binaries (until then SmartScreen and UAC show an unknown
  publisher, and peers are checked by install path, not by signature — `docs/design/product.md`, change 2).
