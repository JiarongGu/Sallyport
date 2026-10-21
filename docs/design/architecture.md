# Architecture — how Sallyport fits together now (2026-10-21)

The one page that says what is, not what was planned: the projects and what each may know, the processes and the
accounts they run as, the pipes between them, and what is kept where. Every other page in this folder is the record
of a design as it was made (its own header says what of it still holds); where one disagrees with this page, this
page is the code's and that one the history. Why each part is as it is: `docs/DECISIONS.md`.

## Projects and what each may know

```
src/Sallyport             net10.0, BCL only (rule 4)   the protocol library — bound for NuGet
src/Sallyport.Windows     net10.0-windows               → Sallyport
apps/Sallyport.Service    library                       → Sallyport.Windows
apps/Sallyport.Setup      library                       → Sallyport.Windows
apps/Sallyport.Client     WinExe, Shenora 0.21.1 (D9)   → Sallyport, Sallyport.Windows, Sallyport.Service, Sallyport.Setup
apps/Sallyport.Uninstall  WinExe, Native AOT (D32)      → Sallyport.Windows; Setup's Installer*.cs, words and ShellLink compiled in
apps/Sallyport.Host       Exe                           → Sallyport
tools/Sallyport.Probe     Exe                           → Sallyport
tools/Sallyport.Driver    Exe                           → Sallyport, Sallyport.Windows
apps/Sallyport.Observer   Exe, not in the product (D16) → Sallyport.Windows
apps/Sallyport.Vpn        not in Sallyport.slnx (D16)   the driverless route, kept, not built with the rest
```

- **`Sallyport`** — `Profile/` (parsing `.ovpn`, the connection policy), `Protocol/` (internal: control channel and its
  reliability, tls-crypt, key method 2, the data channel, transports, certificates, the handshake), `Tunnel`, `Net/`
  (IPv4, the userspace TCP, DNS, echoes, the dispatcher that is a tunnel's one reader), `Proxy/` (SOCKS5, CONNECT
  only), `Runtime/` (`TunnelSession`, `TunnelService`, `ProfileStore`). Nothing in it is Windows' or the app's: a
  reference from it to either is a mistake. Its internals are visible to its tests and the two tools only.
- **`Sallyport.Windows`** — what only Windows has: `PacketFilter/` (Windows Packet Filter's interface, its pinned
  installer), `Routing/` (the router and its NAT, flows, owner tables, the program selection, `GameRoute` across
  generations), `Service/` (the service's protocol: messages, framing, the pipes' peers, `RouterClient`, `RunPump`,
  the service manager), `Apps/` (a game folder's programs, the earlier game store read to migrate it), `InstallLayout`.
- **The one program (D30)** — `Sallyport.Client.exe` is the window, the service and setup: `Program.Main` hands
  `--service` to `Sallyport.Service`, a file name holding "setup" (or `/uninstall`, `--setup`) to `Sallyport.Setup`,
  and anything else to the window. The window's host (`ClientCore` and its views, D15) decides every word and action;
  the page (`web/`, React, embedded) only shows them.

## Processes and accounts

| Process | Started as | Account, session | Holds |
|---|---|---|---|
| The window and tray | `Sallyport.Client.exe` (`--tray` at sign-in, the user's Run key) | the person, their session; one per session (`Local\Sallyport.Client`, a later start handing its arguments over pipe `Sallyport.Client.<session>`) | every profile's tunnel (`TunnelSession`, one per connected profile, its SOCKS5 proxy on a free loopback port), the sealed profiles opened only to connect |
| The supervisor | `--service`, by the service manager (service `Sallyport`, automatic, restarted 1, 5 and 30 s after a failure) | LocalSystem, session 0 | the control pipe, one run on the machine, the direct-path guard (D13: 1.1.1.1 and 8.8.8.8 on 443, four times a second while a run intercepts) |
| A run's worker | `--service worker <run> <client pid>`, by the supervisor, in a job object that dies with it, high priority | LocalSystem, session 0 | the packet driver, the router; its own pipe |
| Setup | the release's file, or `/uninstall`, or `--setup` | elevates itself (`runas`), one prompt; an update run from inside the folder reruns from `<folder>.update` (`--staged`) | the install folder, the service's registration, the driver's install |
| The uninstaller | `uninstall.exe` beside the program | administrator (its manifest) | setup's removal |
| The host | `Sallyport.Host serve|stop`, by another app (Kobo) | whoever starts it | one profile's tunnel and proxy, a status file |

The tunnel never runs as the system: OpenVPN and TLS stay in the person's process (router design, change 1); only IP
packets cross to the worker.

## The pipes

Both carry frames: a 4-byte little-endian length, a 1-byte kind, the payload — a control message as JSON, or a packet
(its 4-byte generation, lane in the top byte, then the IP packet) — at most 70,000 bytes; both sides speak protocol
version 5 exactly. The DACL on both: LocalSystem full, interactive users read and write (not create), the network
denied. No profile, key or credential crosses either.

- **`\\.\pipe\Sallyport.Router.v1`**, made by the supervisor with `FILE_FLAG_FIRST_PIPE_INSTANCE` — nothing can hold the
  name before it. A peer is refused unless Windows says it is in a person's session (not 0), not the system, and runs
  an executable in the service's own folder (writable by administrators only, product design change 2). Messages:
  Hello → Welcome; PrepareRun → RunPrepared or Rejected; Heartbeat every second (the lease is 3 s); StopRun; RunEnded.
- **`\\.\pipe\Sallyport.Router.v1.run.<run>`**, made by the worker for the one client the run was prepared for (its
  process id, from Windows). Attach → Attached; CommitGeneration, RetireGeneration and SelectPrograms from the client;
  RouteState every second from the worker; packet frames both ways.
- **The client's check of the service:** the pipe's server process against the service manager's record — the
  service's process, its account (LocalSystem) and executable (the install folder's) — which a person may read where
  the service's own process is closed to them (D30).
- **What the worker routes:** the programs of the run — by full path, never one of Sallyport's own — and only processes
  in the session of the client the run is for (the supervisor passes it from Windows).
- **Not checked until Sallyport is signed:** publishers. A peer is trusted by account, session and install folder.

## What is kept where

All of it in the install folder (D19, D25), `C:\Program Files\Sallyport` unless the person chose another:

```
Sallyport.Client.exe, WebView2Loader.dll, uninstall.exe    administrators write, users read and run
data\                                                       the machine's part, administrators write
  setup.json, language, update.json (optional)              setup's record, the language setup ran in, where updates come from
  logs\                                                     setup.log, service.log (public addresses masked)
  drivers\                                                  setup's copy of the driver's pinned installer
  profiles\  config\  library\  apps\  cache\               the shared areas: every user of the computer reads and writes them
```

- **profiles** — each imported profile sealed by DPAPI for the Windows account that imported it, its description beside
  it (name, transports, never its servers' names), its sign-in sealed the same way when kept.
- **config** — Settings (`client.json`), the window's place; **library** — the apps; **apps** — each browser's own
  profile folder; **cache** — what can be made again (WebView2's, the driver's installer, an update's download).
- Kept by a removal unless the person says otherwise: profiles, config, library, apps.
- Earlier installs' own folders per user (`users\<SID>`) and games (`games`) are moved in or migrated once.
- The driver's own files go beside the install folder (`<parent>\Windows Packet Filter`); the driver itself where
  Windows keeps drivers.
- A development build keeps its data in `%LOCALAPPDATA%\Sallyport`, apart from an installed copy's.

## What talks to what

The window: each profile's server (UDP or TCP, as the profile says); through the VPN, 1.1.1.1 for latency every 30 s
(and as the proxy's DNS when the server pushes none) and ipinfo.io when asked; GitHub's release API daily and its
download when updating; a link the person types to import a profile; NT Kernel's GitHub release for the driver's
installer. The supervisor: 1.1.1.1 and 8.8.8.8 directly while a run intercepts (the guard). A routed program's own
connections go through its profile's tunnel; its name lookups are Windows' (the DNS Client service), not routed.

## References read for shape, never code

Read for structure only (AGENTS.md rule 1 binds OpenVPN's source): WireGuard for Windows (a manager service brokering
for an unelevated UI), Mullvad (a daemon and its clients; route by executable; DNS made by the system's service cannot
be attributed to an app), sing-box / Clash (inbound → rules → outbound), Netch (games routed per process at the packet
level), and Shenora's own rule — a package boundary only where a dependency or a platform differs.
