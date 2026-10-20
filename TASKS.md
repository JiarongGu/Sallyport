# TASKS.md — open work only, in order

A finished item moves to `docs/TASKS-DONE.md` with how it was measured (the owner, 2026-10-21: "you should archive all
completed TASKS"). Work goes top to bottom, one at a time; an item is done when what it says under **Done when** has
been measured — against the real server where a server is involved (CLAUDE.md rule 3). **Needs** names what only the
owner can give.

## 0. Next session: a full code review, then the protocol library on NuGet
The owner, 2026-10-21: "next session will be a full code review + perpare for nuget release for our core (the MIT
library for openvpn)". The list of 2026-10-21 is done: `docs/TASKS-DONE.md`, After 0.1.0.

The owner's reports since, each first:
- **The setup exe's size.** "I also found the setup.exe is really big?" 53 MB is .NET's runtime and Windows Forms whole
  (124 MB loose, compressed); WebView2 is Windows' own. Trimmed (`TrimMode=partial`) it measured 28.8 MB, but
  Microsoft does not support trimming Windows Forms (NETSDK1175) and the library's reflection JSON warns (IL2026).
  **Done when:** a trimmed release, every path tried on it (the window, the tray, the file dialogs, copy, icons, the
  service and its workers, setup's install, upgrade and removal, export and import), or the reason it stays whole
  recorded in DECISIONS.
- **A folder release with a manifest.** "instead bundle we will need a zip/folder bundle too with mainfest file for file
  hash check (and this is been widely used in sibling repo you can check and pick the best one you think)". **Done
  when:** the release has, beside the setup exe, a zip of the installed folder and a manifest (each file's size and
  SHA-256, the version), its choice from the sibling repositories recorded in DECISIONS; a rehearsal's zip checked
  against its manifest.
- **App updates, from GitHub for now, the address in config.** "we will need to support app update (via github for now)
  and the address should be able to change in config (so we might use other host in future)". **Done when:** Settings
  says whether a newer version is out, and updates with one press (download, every file checked against the manifest,
  setup's elevated apply); the address a setting in `data\`, GitHub's releases by default; measured from a rehearsal
  release to the installed copy.

1. **A full code review**, before anything is published from it.
   - **Scope:** the library first (`src/Sallyport`: Profile, Protocol, Tunnel, Net, Proxy, Runtime), then
     `src/Sallyport.Windows` (the driver, the router, the service's protocol), then the one program (`apps/Sallyport.Client`
     with `Sallyport.Service` and `Sallyport.Setup`, D30), then the tests.
   - **Against:** AGENTS.md's six rules — above all rule 2 (every log line, exception and test failure that could carry a
     profile's text, a key or a password) and rule 6 (a check that cannot be made said as that); the service's pipe
     rights and its identity checks (D30's manager-record path, setup's elevated restart and its quoting); the sealed
     files and the export's password; the router's two threads (tallies and mappings written by one, read by the other);
     `GameRunner`, `Connections`' poll and its latency checks; handles and `Process` objects let go; error paths.
   - **How:** as before — Codex read-only audits on briefs in `local/codex/`, one area each, every finding checked against
     the code before it is acted on, the record saying how it was made.
   - **Done when:** each finding fixed, refused with its reason, or open here; every test, and a live run (jp, an app
     through it, the guard) after the fixes.
2. **`src/Sallyport` on NuGet** as `Sallyport` (free on nuget.org on 2026-10-21, as is `Sallyport.OpenVpn`).
   - **Its public API:** 46 public types now; decide what is meant to be public (`OvpnProfile`, `Tunnel`, `TunnelOptions`
     and `Login`, `TunnelNetwork`, `Socks5Server`, `TunnelService`, `ProfileStore`…) and make the rest internal; XML docs
     on every public member (CS1591 is suppressed today: lift it for the library); its README's examples compiled.
   - **The package:** repository and project URLs, an icon (the mark), symbols (`.snupkg`) and SourceLink,
     `ContinuousIntegrationBuild` on CI, `EnablePackageValidation` with a baseline from its first version on — as
     Tyanor's `src/Directory.Build.props` does.
   - **Its own version**, apart from the app's (D28's `version.ps1` writes one `<Version>` for everything): where it is
     written, its own CHANGELOG section, and its first (a preview?).
   - **Publishing:** a workflow beside `release.yml`, started by hand, a rehearsal by default, NuGet Trusted Publishing
     (OIDC, `id-token: write`, `NuGet/login@v1`, `--skip-duplicate`) as Tyanor's `release.yml` does.
   - **Needs (the owner):** a nuget.org account's Trusted Publishing policy for `JiarongGu/Sallyport` and that workflow.
   - **Done when:** a rehearsal's `.nupkg` and `.snupkg` looked inside (README, XML docs, licence, SourceLink); a sample
     project on the local package connects to the owner's server (rule 3); then the owner's go.

## 1. Stage 1 — every packet of a game's programs, on Windows Packet Filter
Per-process routing from the first packet (game-routing §5, amendments 1–2): packets taken at the network layer,
matched to their process through the owner tables, NAT'd into the tunnel, replies put back. Our code MIT; the driver
NT Kernel's, free for personal use.
- **Done when:** Defender accepts the driver; Zenless Zone Zero runs beside it; a routed program's TCP and UDP leave
  through the tunnel from their first packet while another program stays direct; a peer-to-peer harness (one side
  behind the home NAT, one behind the VPN's) connects through it.
- **Measured:** the driver (D18: Defender, Zenless beside it, removal and reinstallation) and the router's design and
  gates 1–6, Zenless routed whole among them — `docs/TASKS-DONE.md`, Stage 1.
- **Open:** the peer-to-peer harness (item 2, step 10); whether to delete `ndisrd.sys`, which stays in
  `System32\drivers` after removal, inert.

- **If Defender refuses the driver,** the driverless route (D16) is what remains, and its relay's inbound loss comes first:
  in the last Zenless run the bridge sent 3,870 packets to the plug-in and Windows delivered 2,100 — about 45% lost on
  the loopback leg, under bursts (HTTPS downloads); gameplay alone stayed well below it.

## 2. The product — `docs/design/product.md`, built in its steps
One install; a game profile and a VPN profile, **Run**. The tunnel reconnects by its profile's connection policy
(OpenVPN's directives, which the person can override); while it is down, the game's traffic is held, sent direct, or
the route stops, as the game profile says (the owner: *"there can be auto reconnect mode with different config (this
is more like openvpn)"*). The app manages the driver. Each step is measured before the next (rule 3).
- **Measured:** steps 1–5 (the connection policy and the session's rounds, consumer generations, the service, the
  packet pipe, the router across generations) and the client's increments 8.1–8.10 — `docs/TASKS-DONE.md`, The
  product. Their numbers stand, as other items name them.
6. **Adapters:** Wi-Fi and Ethernet both up, a handover, sleep, the driver removed → what the packets did, every gap
   said, the machine back on its own.
7. **Zenless through the service:** Run from launch, attach and detach, a real drop under each policy, sleep → the
   game's session and the coverage recorded. **Needs:** the owner, playing.
8. **The client — `docs/design/window.md`** (the owner: one Apps list, every routing method in one channel per profile;
   tabs Apps · Network · Profiles · Settings), in its working increments, each with C# view tests and the real window
   driven through UI Automation. Its increments are measured (above). Not yet: two different servers in one run, and one lane's outage while the
   other's server stays up (8.5, one server exists to measure against); an export taken to another machine and
   imported there (8.8); a real server that asks for a username and password, an Access Server's
   `/rest/GetUserlogin`, and a profile dropped by mouse (8.10).
   - **Done when:** Zenless Zone Zero is an app from its folder, routed by packets while a browser of the same profile
     uses its proxy, through one login; window and tray agree with the service's and the server's evidence.
9. **Setup:** `apps/Sallyport.Setup`, BCL-only and self-contained, one administrator prompt — the files in the
   folder the person chooses (`%ProgramFiles%\Sallyport` unless they choose otherwise; the owner's is on D:, nothing
   of it on C:), its data (service log, setup record, driver installer) in `data` inside it, the service, the driver on
   request, a Start-menu entry, `.ovpn` under Open With, start with Windows per user, the uninstall entry, upgrade in
   place, and removal that leaves the machine as it was (user data only if asked).
   - **Begun 2026-10-20 and installed on the owner's machine since** (D19, D25, D30: `docs/TASKS-DONE.md`, The
     product). Not yet: removal with D25's keep, a driver install through the installer's hold, an upgrade across
     versions, an interrupted install, a clean machine.
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
- **A client that is not elevated, routing (2026-10-21):** the owner's machine has UAC off (`EnableLUA` 0), so every
  program there runs elevated and the case cannot arise; the check it needs (the service manager's word where the
  service's process is closed, the worker named by its process) is unit-tested with an unreadable peer, and routing
  through jp measured with it on the owner's machine (Zenless's Use VPN up over a minute, the guard 196 rounds, 0
  failed). Measure on a machine with UAC on. The owner's own "not running as the system" was not reproduced: it is
  what a worker gone before the client reached it showed, which now gives the service's reason instead.
- **Sizes at another display scale (2026-10-21):** the window and setup follow Windows' Text size (measured at 100% and
  150% on the owner's one screen, at 200%, through `SALLYPORT_TEXT_SCALE`); the screen's own scale is WinForms' and
  WebView2's, as before. Not measured at another display scale: this machine has one screen. Measure at 100% and 150%.
- **One program's install and removal (2026-10-21, D30):** measured as upgrades of the owner's install and used; not a
  first install on a machine without Sallyport, nor a removal to the end (the Start menu's Uninstall Sallyport, Settings'
  Uninstall Sallyport… and Windows' installed apps each open setup's removal, cancelled there), nor with UAC on. A clean
  machine or VM is where those are measured (§2's install item).
- **Add an app with more than one VPN (2026-10-21):** the owner has one profile, so an app ticked into two at once is
  measured only as far as the code and its test go.
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
- **The next release** is D30's one program, about 53 MB (0.1.0, its record in `docs/TASKS-DONE.md`, was D28's three
  programs); its CHANGELOG is `## Unreleased`.
- **Needs (the owner):** private vulnerability reporting turned on (Settings → Code security → Private vulnerability
  reporting), which SECURITY.md points to — the API said `enabled: false` on 2026-10-21.
- **Needs:** a certificate others trust, for the setup and the binaries (until then SmartScreen and UAC show an unknown
  publisher, and peers are checked by install path, not by signature — `docs/design/product.md`, change 2).
