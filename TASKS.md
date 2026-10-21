# TASKS.md — open work only, in order

A finished item moves to `docs/TASKS-DONE.md` with how it was measured (the owner, 2026-10-21: "you should archive all
completed TASKS"). Work goes top to bottom, one at a time; an item is done when what it says under **Done when** has
been measured — against the real server where a server is involved (AGENTS.md rule 3). **Needs** names what only the
owner can give.

## 0. Next session: a full code review, then the protocol library on NuGet
The owner, 2026-10-21: "next session will be a full code review + perpare for nuget release for our core (the MIT
library for openvpn)". The list of 2026-10-21 is done: `docs/TASKS-DONE.md`, After 0.1.0.

1. **A full code review**, before anything is published from it — **done but for its live run.** Seven audits (Codex
   three, then Claude four when Codex ran out of credits), every finding checked against the code; the record of each
   and what became of it is `docs/design/code-review.md`, the decisions it made D35. Fixed and refactored in `aeeb9eb`,
   `993f49d`, `c9f4e04`, `e8f8312`, `bf4a6e7` and the documents' commit; every test passes (550, from 499). Its open
   findings are below, each where its work belongs.
   - **Done when:** the live run — the installed copy on this tree, jp connected, an app through it (TCP and UDP), a
     download through the proxy, the guard beside it, the window's pages opened — recorded in the review's record.
   - **Needs (the owner):** a moment with no game running: the deploy restarts the client and the service.
2. **`src/Sallyport` on NuGet** as `Sallyport` (free on nuget.org on 2026-10-21, as is `Sallyport.OpenVpn`).
   - **Its public API:** 46 public types now; decide what is meant to be public (`OvpnProfile`, `Tunnel`, `TunnelOptions`
     and `Login`, `TunnelNetwork`, `Socks5Server`, `TunnelService`, `ProfileStore`…) and make the rest internal; XML docs
     on every public member (CS1591 is suppressed today: lift it for the library); its README's examples compiled. The
     code review's part of it: the measurement helpers in the public surface (`Handshake`, `TunnelDns`, the dispatcher's
     classification — LB5) and the tools' reach into the internals (`InternalsVisibleTo` Probe and Driver — TB2).
   - **Its JSON:** the profile store's records by reflection, which a trimmed build reports (IL2026, D31):
     source-generated for the package, so that a trimmed or ahead-of-time app can take it.
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
   tabs Overview · Apps · Profiles · Settings), in its working increments, each with C# view tests and the real window
   driven through UI Automation. Its increments are measured (above). Not yet: two different servers in one run, and one lane's outage while the
   other's server stays up (8.5, one server exists to measure against); an export taken to another machine and
   imported there (8.8); a real server that asks for a username and password, an Access Server's
   `/rest/GetUserlogin`, and a profile dropped by mouse (8.10).
   - **Done when:** Zenless Zone Zero is an app from its folder, routed by packets while a browser of the same profile
     uses its proxy, through one login; window and tray agree with the service's and the server's evidence.
   - **From the code review (`docs/design/code-review.md`), open:** a TCP connection never attributed is not counted for
     the app's check (W7, the rest of it); a service state not known is shown as installed and stopped (CA2); the store's
     and the export's reasons are English in a Chinese sentence (UA6); an app with no icon of its own shows another's as
     chosen (UA7); a folder `GameFolder.Scan` could not read is not said (L22); a saved login that does not read shows as
     not saved (L23); the page's own refactors — a test that the contract's fields agree both ways, `contract.ts` and
     `ui/Editors.tsx` split, the subject's encoding and the states the page derives sent by the host, the editor views
     in a class of their own, `saving` and the empty Overview (UB1–9, UC4–5) — each with the window driven through UI
     Automation, the page having no tests but its type check.
9. **Setup:** `apps/Sallyport.Setup`, a library the one program runs (D30), one administrator prompt — the files in the
   folder the person chooses (`%ProgramFiles%\Sallyport` unless they choose otherwise; the owner's is on D:, nothing
   of it on C:), its data (service log, setup record, driver installer) in `data` inside it, the service, the driver on
   request, a Start-menu entry, `.ovpn` under Open With, start with Windows per user, the uninstall entry, upgrade in
   place, and removal that leaves the machine as it was (user data only if asked).
   - **Begun 2026-10-20 and installed on the owner's machine since** (D19, D25, D30: `docs/TASKS-DONE.md`, The
     product); upgrades across versions measured (D30, D34). Not yet: removal with D25's keep, a driver install through
     the installer's hold, an interrupted install and its recovery by the next run (the review's P4), a clean machine.
     From the code review, open: a removal's cleanup after exit said as done before it is (P11); an unreadable setup
     record read as "did not install the driver" (P13).
   - **Done when:** install, upgrade, an interrupted install and removal are each measured on a clean machine, the
     network usable throughout. **Needs:** a clean Windows machine or VM.
10. **The long run:** hours on the real server with outages, and the peer-to-peer harness → bounded memory and
    handles; NAT and replies right. From the code review, open, measured here: a NAT mapping outliving its socket's
    owner (W4); a UDP endpoint becoming shared keeps its first owner's decision (W6). And the tools: a guard that every
    intercepting command checks for (TA4); `service-failure` finding the run's worker since D30 (TA5); the Driver's
    `Program.cs` in its pieces (TB1), its duplicated echo and STUN parsers (TB3–4), its usage text (TC4); tests for the
    supervisor's refusals (TA10), the replay window's `Missing` (TA13) and `GameRoute` behind a seam (TA16, TB5); one
    place for the identity checks (TB6) and the tests' shared helpers (TB7).

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
- **The small uninstaller's removal (2026-10-21, D32):** opened from every way in and cancelled on the owner's machine,
  its removing and ending pages seen through a harness; a removal to the end, on a machine where that is safe.
- **One program's install and removal (2026-10-21, D30):** measured as upgrades of the owner's install and used; not a
  first install on a machine without Sallyport, nor a removal to the end (the Start menu's Uninstall Sallyport, Settings'
  Uninstall Sallyport… and Windows' installed apps each open setup's removal, cancelled there), nor with UAC on. A clean
  machine or VM is where those are measured (§2's install item).
- **Add an app with more than one VPN (2026-10-21):** the owner has one profile, so an app ticked into two at once is
  measured only as far as the code and its test go.
- **Done when:** each is measured, or recorded as could-not-be-checked with the reason.

## 5. Phase 4 — the long run (the library)
Renegotiation (`ControlStream` refuses a soft reset, so a server that renegotiates costs a reconnect; the owner's did
not in a 75-minute run on 2026-10-13, though OpenVPN's default is hourly), `auth-user-pass` measured against a server
that asks (written, D29), `tls-auth`, `tls-crypt-v2`, `P_DATA_V1` and fingerprint-pinned profiles (refused, D35), uploads at volume (the send side has no SACK of its own), and allocation on the receive path
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
- **The next release** is whatever `## Unreleased` in CHANGELOG holds; 0.1.1 (D30's one program, 31.0 MB, and the
  first that updates itself) is out, its record in `docs/TASKS-DONE.md`.
- **Needs:** a certificate others trust, for the setup and the binaries (until then SmartScreen and UAC show an unknown
  publisher, and peers are checked by install path, not by signature — `docs/design/product.md`, change 2).
- **The workflow (the code review's P15):** its actions are pinned by commit; the rest is a build job with read rights
  only and a publishing job with write rights apart from it, so a rehearsal never holds them. **Needs:** a dispatch to
  measure it.
