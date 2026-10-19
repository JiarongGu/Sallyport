# TASKS.md — open work only, in order

A finished item is deleted (git is the archive). Work goes top to bottom, one at a time; an item is done when what it
says under **Done when** has been measured — against the real server where a server is involved (CLAUDE.md rule 3).
**Needs** names what only the owner can give.

## 1. Zenless Zone Zero routed in a fresh session (driverless)
The game's learned destinations (`docs/design/game-routing.md`) routed for its game profile's programs, through the
bridge (`sallyport-vpn route --folder`), started before the game.
- **Done when:** a fresh session is played with the route up and the guard holding; the bridge's counters show the
  game's UDP gameplay going through the tunnel, or show that this session's server was a new one that went direct;
  which UDP server each session used is recorded, so it is known whether learning once is enough for this game.
- **Needs:** the owner launches and plays the game while the route is up.

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

## 5. Stage 1 — every packet of a game's programs, on Windows Packet Filter
Per-process routing from the first packet (game-routing §5, amendments 1–2): packets taken at the network layer,
matched to their process through the owner tables, NAT'd into the tunnel, replies put back. Our code MIT; the driver
NT Kernel's, free for personal use.
- **Done when:** Defender accepts the driver; Zenless Zone Zero runs beside it; a routed program's TCP and UDP leave
  through the tunnel from their first packet while another program stays direct; a peer-to-peer harness (one side
  behind the home NAT, one behind the VPN's) connects through it.
- **Needs:** the owner's approval to install `ndisrd.sys`.

## 6. Stage 2 — our own WFP driver behind the same seam
Replaces `ndisrd.sys`; nothing above the seam changes.
- **Needs:** a registered business for an EV code-signing certificate and a Microsoft Hardware Dev Center account
  (attestation signing; drivers are not signed by Microsoft's cloud signing service); a test VM, because development
  needs test-signing mode, which anti-cheats refuse.
- **Done when:** the attestation-signed driver passes item 5's measurements, and Zenless Zone Zero runs beside it.

## 7. What the client's measurement did not reach (2026-10-19)
The file dialogs past opening and cancelling (this machine's dialog shows UI Automation no File name box; app shortcuts
were seeded instead), Firefox and Electron launches, the dark theme of the new window, the tray driven by hand.
- **Done when:** each is measured, or recorded as could-not-be-checked with the reason.

## 8. Ship
An installer: the client, and the game-routing part (the VPN package with its loopback exemption today, the driver
after item 5 or 6), one administrator prompt. A Start-menu entry, `.ovpn` files opened with the client, start with
Windows (`--tray`). The development set-up comes off this machine: the test certificate, the exemption, the package
(`apps/Sallyport.Vpn/README.md`).
- **Needs:** a certificate others trust, for the package and the installer.

## 9. Phase 4 — the long run (the library)
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

## 10. Release
