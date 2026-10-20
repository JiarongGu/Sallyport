# Changelog

## Unreleased

- Routing an app works from a client that is not elevated (2026-10-21). The client checked the router service by
  opening the service's own process, which Windows lets only the system and elevated administrators do; started
  normally (by setup, by Windows at sign-in), it could not, and said the service was "not running as the system".
  It now asks the service manager — the service's process, its account, its executable, which a person may read —
  and takes the run's router to be the process the service names. A reason the service gives is now shown in the
  window's language, and a router that stops before the client reaches it gives the service's own reason.
- A profile's Edit and Remove are on its row (2026-10-21). Edit is one page with one Save: its name, its sign-in, and
  how it connects — as in Settings, or its own settings (the protocol, retrying, the finer settings). A silence limit
  shown only because the server sets none is no longer kept as the profile's own when it is saved unchanged.
- The tray menu (2026-10-21): room above the first entry and below the last as at its sides, the highlight as far
  from the right edge as from the left, and a submenu beside the menu with its entry level with the row it opens from.
- Setup keeps the install folder chosen when its language is changed (2026-10-21); before, the window opened again in
  the other language on the default folder.
- The setup is 27.5 MB, from 176 (2026-10-21, D30, D31). Sallyport is one program — the window, the router service and
  setup — so the release carries one .NET runtime instead of four, trimmed to what it uses of .NET's own assemblies, and the install folder holds that program, WebView2's
  loader and `data`. An upgrade from 0.1.0 moves the service to the program; the Start menu, sign-in start and `.ovpn`
  association stay as they were.
- Adding an app for apps and VPNs many to many (2026-10-21): Add an app asks which VPNs it goes through — each a
  tick, its Use VPN on in each one ticked — after the program is found; what happens when the VPN drops is folded under
  its choice. A profile's section on the Overview adds an app already there or a new one (A new app…, with that
  profile ticked). `--add-game` takes a program as well as a folder, and opens Add an app on it.
- Each app's own use and latency (2026-10-21): on the Overview and Apps, an app going through a VPN shows what it
  has received and sent through it and its own round trip — its TCP handshakes through the tunnel, or for a program on
  UDP alone its shortest wait for a reply; its check and its figures are on its own page. A connected profile's latency
  is checked on its own every 30 seconds; there is no Check ping button. Processes on the computer that opened a
  connection but could not be identified are said on the app's page only, as what they were (closed already, or
  refusing to be read).
- Uninstalling where a person looks (2026-10-21): the Start menu has a Sallyport folder with Sallyport and Uninstall
  Sallyport, and Settings → About has Uninstall Sallyport…; each opens the removal, as Windows' installed apps do.
- The release as a folder too (2026-10-21, D33): beside the setup, `Sallyport-<version>-win-x64.zip` (the installed
  folder's files as they are installed) and `manifest.json` (each file's size and SHA-256, and the two downloads'),
  checked against each other before anything is published (`verify-release.ps1`, which also checks a download).
- A small uninstaller (2026-10-21, D32): `uninstall.exe` beside the program, 4.4 MB, compiled ahead of time with no .NET
  inside, in Windows' own dialog and Sallyport's language; every way to remove Sallyport starts it. The removal is
  setup's own, as before.
- The window and setup follow Windows' Text size (Settings → Accessibility → Text size; 2026-10-21): the window's
  page is sized in rem of a root that the client sets from it, and arranges itself for a narrow window by its width in
  that size; setup's window and fonts grow by it too.

## 0.1.0 — 2026-10-20

- The release (2026-10-20, D28). One setup, `Sallyport-<version>-win-x64-setup.exe`, with its SHA-256 beside it and
  notes from this file, built by `apps/Sallyport.Setup/package.ps1` and by the Release workflow (started by hand,
  a rehearsal unless told otherwise; it tests, packages, then dates this section, tags and drafts the GitHub
  release). Every program says what it is in Windows: Sallyport, Sallyport Service, Sallyport Setup, its version.
- Adding a profile and signing in (2026-10-21, D29). Import a VPN profile is a page: a file chosen or dropped on the
  window, or a link (https only; a provider's server address, signed in to when it asks), then the profile looked at
  before it is kept — its servers, its name, its sign-in. A profile may sign in with a username and password, with or
  without a certificate of its own: the password remembered (sealed, like the profile) or asked when it connects, and
  asked again when the server does not accept it. Each profile's row shows its server and account; Edit renames it,
  changes its username and kept password, and holds its connection settings and removal. Not yet measured against a
  server that asks for a username and password.
- Moving to another computer (2026-10-20, D26). Settings exports VPN profiles, apps and connection settings to one
  `.sallyport` file, the profiles sealed with a password if the person gives one (AES-GCM, PBKDF2-SHA256 600,000);
  import adds and never overwrites. An imported app not where it was is looked for on the other drives, else says so
  and offers Locate…. Profiles show how they connect, as in Settings or for that profile only, and are connected on
  Overview (D27).
- One data folder for the machine (2026-10-20, D25). Profiles, settings, apps and the cache in `data` beside the
  programs, the profiles still sealed to the Windows account that imported them; the install folder is the client,
  the service, the uninstaller, WebView2's loader and `data`.
- Apps and profiles many to many (2026-10-20, D23–D24). The Overview is a section per VPN profile: its switch
  connects it, and each app in it has its own Use VPN, which stands until the person turns it off; an app may be in
  several. The service routes up to eight profiles at once, each a lane of its own that is held, lost or let go
  alone. The tray connects and disconnects profiles.
- The window (2026-10-20, D15, D20–D22). Overview, Apps (each app's page: its programs, what each is doing, its
  icon, its settings), Profiles and Settings, in English or 中文简体; ping, the exit location, traffic and
  reconnecting with a countdown; a profile's proxy on whatever port is free; connection settings for every profile,
  one profile's own as a view of their own. Closing it keeps Sallyport beside the clock.
- Games and apps through the VPN by their packets (2026-10-19–20, D10–D18). The Sallyport service (LocalSystem)
  takes the chosen programs' TCP and UDP through NT Kernel's Windows Packet Filter driver into the profile's tunnel,
  from their first packet; everything else keeps the person's own connection. A browser can instead be launched
  with the profile's proxy. If the VPN drops, an app's traffic is held until it is back, goes direct meanwhile, or
  the app is taken off the VPN, as chosen. Measured on the owner's machine with Zenless Zone Zero: all 116 of its
  flows routed from their first packet.
- Setup (2026-10-20, D19). One administrator prompt; the folder is the person's choice (`C:\Program Files\Sallyport`
  unless they choose otherwise), nothing of Sallyport's left on C: when it is elsewhere; the packet driver on
  request; English or 中文简体; removal keeps the person's profiles and settings unless asked.
- The runtime (2026-10-19). `TunnelSession` keeps one profile's tunnel up — the remotes in turn, reconnecting with
  backoff — and reports `SessionStatus` on every change of state, with byte counts that survive a reconnect;
  `NetworkUp` lets a host attach a proxy (or, later, a packet router) before "up" is said. `TunnelService` is now that
  session behind the SOCKS5 proxy, with the status shape Kobo reads unchanged. `ProfileStore` keeps imported copies sealed
  by an `IProfileProtector`, described without keys or host names, under ids that cannot name a path (D11).
  `Tunnel.Unsupported` says why a profile cannot be served before any packet is sent. Fixed: stopping a service while
  it was still connecting threw out of `RunAsync` instead of stopping. Measured: Kobo's Host republished on it — its
  status file reads as before, and the route test says JP Tokyo through the route, AU Sydney directly.
- Restructured as a library with applications on it (2026-10-19, D9). `src/` the packages, `apps/` what runs
  (`Sallyport.Host`), `tools/` the probe. The wire is internal: `Opcode`, `ControlPacket`, `TlsCrypt` and `StaticKey` are
  no longer public, and the control and data channels, transports and certificates share `Sallyport.Protocol`.
  **Breaking** for in-process consumers: `Sallyport.Socks` is now `Sallyport.Proxy`, `Sallyport.Hosting` is now
  `Sallyport.Runtime`. `Sallyport` packs as a NuGet package with its own README. Kobo runs the Host and is unaffected:
  republished, its route test says JP Tokyo through the route and AU Sydney directly.
- Many connections at once (2026-10-19). The receiver keeps its SACK blocks as ranges as segments arrive — rebuilt from
  every held segment, one hole cost the network's reader 105 µs a segment and at 64 downloads it fell behind the tunnel
  and dropped thousands of packets itself. A waiting reader is woken once per burst, not per segment. A half-closed
  relay whose other side is silent for a minute is let go (D8). `TunnelCounters.Overflowed` counts packets this process
  drops itself; `Sallyport.Probe --concurrent N` measures N connections at once. Measured: 32 and 64 at once, twice,
  all whole and all from the server's address, 12.1 and 18.2 MB/s together, nothing overflowed; a cold Edge session
  (Yahoo! JAPAN, nikkei.com) through the proxy held up to 49 at once with none failing (D7).
- Phase 3, the userspace network (2026-10-12): `TunnelNetwork` — outbound TCP (MSS, window scaling, SACK, RFC 6298
  retransmission, slow start, fast retransmit, reassembly, FIN both ways) and DNS through the pushed resolvers with a TTL
  cache — and `TunnelConnector`, which makes `Socks5Server` dial through the tunnel. Measured: ipinfo.io through the
  proxy says JP Tokyo; DMM serves its domestic age check rather than the English one; a 10 MB download arrives whole at
  2.9–3.9 MB/s, level with Linux OpenVPN on the same server (D6).
- SOCKS5 server (RFC 1928 CONNECT) over a pluggable connector, loopback-only (D2).
- Phase 2, the data channel (2026-10-12): key method 2's PRF key expansion, `P_DATA_V2` with AES-256-GCM / AES-128-GCM,
  a replay window, keepalive and `ping-restart`, exit notices; `Tunnel` is the public surface. Measured: two DNS
  lookups answered through the tunnel in 204 ms each, and the egress address was the server's own.
- Phase 1, the control channel (2026-10-12): profile parsing, `tls-crypt` v1, the reliability layer, TLS through
  `SslStream`, key method 2 and the push request. Measured: a real server answered `PUSH_REPLY` over UDP in 1.2 s with
  no packet dropped; `Sallyport.Probe` runs it and prints nothing secret.
- Repository created (2026-10-12): an app-scoped, OpenVPN-compatible client in pure .NET, written from the protocol
  (D1), ending in a SOCKS5 proxy (D2).
