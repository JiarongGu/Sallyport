# TASKS-DONE.md — what was finished, and how it was measured

`TASKS.md` holds the open work; a finished item comes here with its record, newest first (the owner, 2026-10-21: "you
should archive all completed TASKS"). A record says what was sent and what came back (CLAUDE.md rule 3); the commit it
names has the rest. What a record says could not be checked stays open in `TASKS.md`.

## After 0.1.0 — the owner's reports of 2026-10-21

Each was one item of TASKS §0, done in its own commit (newest first), whose message has its measurement on the
installed copy.

- `a262fa6` No defaults for new apps: Add an app sets each one up; an earlier export still imports.
- `d8c8982` Help in a modal beside what it explains; Settings a line at most; How to use in About; two English words in
  Export's Chinese, and the scan that missed them.
- `c4d6ae9` The tray's menu above the taskbar, not hanging over it (WinForms' ShowInTaskbar).
- `4eb569f` The tray drawn as the window is: a row per profile with its state and action; its bottom room measured.
- `4f780ed` Add an app offers its icon, as Edit does.
- `236ec3c` A connection's round trip on its graph as a figure, the rest one line under it (UDP or TCP, its proxy, what
  is open through it); no Details fold, no gateway line.
- `81e25a9` Adding an app, apps and VPNs many to many: which VPNs, ticked; from a profile's section too.
- `c00d167` Each app's own use and latency on the Overview; a profile's latency checked on its own every 30 s.
- `3a7cf33` An uninstall where a person looks: the Start menu's Sallyport folder, and Settings' About.
- `cb3e768` One program, the window, the service and setup (D30): the release 51 MB, from 176.
- `4610ab0` Sizes that follow Windows' Text size: the page in rem, setup scaled with its fonts.
- `c19f589` Setup: the folder chosen survives a change of language.
- `8e4bea0` The tray: room above the entries, an even highlight, the submenu beside its row.
- `a2844e3` A profile's Edit and Remove on its row; one Edit page for its name, sign-in and how it connects.
- `9b2c8ec` A client that is not elevated can check the service (the service manager's record); the service's reasons in
  the window's language. The owner's "not running as the system" not reproduced (UAC is off here): TASKS §4.

Before these, the items finished between the start and 0.1.0 were deleted from TASKS as they were done; `git log`
up to `7a794b2` (v0.1.0) holds them, and the records below are those that were kept in TASKS until now.

## Release

- **0.1.0 published (2026-10-20):** the Release workflow's first dispatch (run 1, workflow_dispatch, success) tagged
  `v0.1.0` and released "Sallyport 0.1.0" (not a draft): its setup (184,546,498 bytes, D28's three-program package) and
  `.sha256`. The next release is D30's one program, about 53 MB; its CHANGELOG is `## Unreleased`.

## The product — `docs/design/product.md` (TASKS §2)

The steps' numbers are TASKS §2's, which other records name.

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
8. **The client — `docs/design/window.md`**, its increments:
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
   3. **One reader per tunnel — measured 2026-10-20** (`Sallyport.Net.TunnelDispatcher`, the session's default
      consumer): TCP and UDP by destination port, ICMP errors by the quoted source port, later pieces to the router;
      the proxy's TCP and DNS on 40960–49151, the router's NAT keeping a program's own port only outside it and above
      1023; `RunPump` the dispatcher's router side. `Sallyport.Driver service-route jp.ovpn --child <copy> --workload
      mixed --proxy 41080 --seconds 45`, one login (one tunnel, 0 reconnects): the routed child's STUN every 500 ms
      and HTTPS every 2 s — UDP 4 of 4 and every HTTPS request answered in each 2 s, the far end seeing the VPN —
      while the same session's SOCKS5 proxy fetched over HTTPS by name (DNS through the tunnel) every 2 s, every one
      answered from the VPN; dispatcher: 79 to the proxy, 137 to the router, 0 unclaimed, 0 not for the tunnel;
      router: 141 routed, 137 handed up, 4 flows, 0 late, 0 stale. The first try failed and taught one thing: on a
      CPU loaded by other work (70 %), the worker's loop stood still 0.5 and 1.4 s and the guard ended the run
      ("the network stopped answering"), the child then going direct; the worker now runs at high priority with its
      router on a highest-priority thread, and the run above was on the same loaded machine (56 %), no stall logged.
   4. **One channel per profile — measured 2026-10-20** (`Connections` is the channel manager; `GameRunner` holds a
      profile's channel instead of a login of its own): up while the person connected it or a route holds it; its
      proxy binds when it can and says why when it cannot, never failing a route; Disconnect with a route on it asks
      first. On the installed client, through UI Automation, with the tool's copy as a stand-in app (`childbin`,
      added by `--add-game`, removed after): jp connected → proxy reached the VPN; childbin run → Network one card,
      "1 connection · 1 app routed", jp with its SOCKS5 proxy and childbin "Routed through jp"; childbin's own STUN and
      HTTPS (4 of 4 and every request, the VPN seen) while the proxy fetched (the VPN seen) — one login; Stop routing →
      jp stayed connected, the proxy still the VPN; run again, Disconnect → "Disconnect jp? childbin is routed through
      it…", confirmed → nothing connected, childbin "jp was disconnected.", the proxy gone, the service's run ended and
      the adapter `None -> None`; jp not connected, childbin run → jp brought up (its proxy listening too), childbin's
      traffic the VPN; Stop routing → jp down again.
   5. **Many channels in the service's one run — measured 2026-10-20 (D24):** a lane per connected profile, two at
      once through the client and the tool, one lane ended while the other carried on. Not yet: two different servers
      (only one exists to measure against), and an outage of one lane while the other's server stays up.
   6. **The Apps editors — measured 2026-10-20** (`AppViews`): one card per app — a program or a folder — with Launch,
      Route / Stop, its profile, methods and loss policy; Add a program… (a browser launched with proxy settings, anything
      else all its traffic) and Add a folder…; each program's row with its methods, Methods… (all traffic, proxy settings
      at launch, or both; how it is told of the proxy; extra arguments when launched), Include / Leave out, and Launch
      for a proxy program beside the app's; the app's editor (name, profile, Launch starts, loss policy); removal asked.
      Launch routes the app first and starts the program only once its generation is committed. On the installed client,
      through UI Automation: Zenless's card (Launch starts ZenlessZoneZero.exe, all traffic of 3 programs); a stand-in
      (`childbin`) given both methods, then refused with neither ("Choose at least one way…"), then all traffic only;
      its Launch with the tool's `mixed-to` as its arguments — the service committed generation 1 at 09:42:46.577, the
      program started at 09:42:47.569, and from its first 2 s every STUN and HTTPS request was answered and the far end
      saw the VPN; the run: 49 routed, 48 handed up, 0 late, 1 selected. The stand-in was removed after.
      Apps naming the same program: the strictest rule of the two (D23).
   7. **Network and Settings — measured 2026-10-20**, but for connection settings as own / override / effective (the
      editor still shows what is in force with the profile's own beneath it):
      - **The retry countdown:** `SessionStatus.NextAttemptAt` and `FailedRounds`, set only while the session waits for
        a round, and "connecting" until a tunnel has been up once (it said "reconnecting… the connection was lost"
        before). jp's server cut for 70 s by Windows Firewall (`Sallyport.Driver cut`, its address never printed) and jp
        connected from Profiles: "Retrying connection… N attempts so far", and "Next attempt in N s" counting down — 2 s
        waits, then 3, 8, 13 s (doubling after the fifth round, jitter seen) — and connected 3 s after the cut ended.
      - **Latency:** `TunnelEcho` in the library, the dispatcher claiming its echoes before any other side; Check
        latency on a channel. jp: direct to 1.1.1.1 14 ms (4 of 4), through the VPN 196 ms (4 of 4), the tunnel gateway
        172.27.232.1 no reply — said as "No reply — latency unavailable", not as a failed tunnel.
      - **Defaults for new apps** (Settings): a profile and a loss policy; set to "your own connection", a folder added
        took it while Zenless kept its own "held"; set back to hold after. Start with Windows was step 1.
      Could not be checked through UI Automation: adding a program through the file dialog from this elevated shell
      (the dialog's controls offered no patterns); the folder path took the same defaults.
   8. **Moving to another computer — measured 2026-10-20 (D26):** Export by part, sealed with a password or not, and
      Import, which adds and never overwrites; an imported app found on another drive by itself, or with Locate….
      Measured on this one computer only: a second computer's paths were stood in for by a drive that does not exist
      (`Q:`). Not yet: an export taken to another machine and imported there.
   9. **Profiles as settings, an app's settings under its head — measured 2026-10-20 (D27).**
   10. **Adding a profile and signing in with it — measured 2026-10-21 (D29):** the import page by a file, a link and
       Explorer's Open with, the review, the sign-in at connect, Edit, through the installed client with stand-ins.
       Not yet: a real server that asks for a username and password (the owner's account is the measurement), an
       Access Server's `/rest/GetUserlogin`, and a drop by mouse.
9. **Setup:**
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
     Then one shared data folder and single files (D25, measured there): `data\profiles`, `config`, `library`, `apps`,
     `cache` for everyone who uses Sallyport on the machine, the installing person's moved in; the install folder four
     files and `data`; a 175.4 MB setup. Not yet: removal with D25's keep, a driver install through the installer's
     hold, an upgrade across versions, an interrupted install, a clean machine.

## Stage 1 — every packet of a game's programs, on Windows Packet Filter (TASKS §1)

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
